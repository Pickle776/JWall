using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace JWall
{
    public class WallpaperInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DirectoryPath { get; set; } = string.Empty;
        public string HtmlPath { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool IsWebUrl => !string.IsNullOrEmpty(Url);

        public override string ToString() => Name;
    }

    public class AppSettings
    {
        public string LastWallpaper { get; set; } = "Live Earth";
        public bool MouseInteraction { get; set; } = true;
        public bool AutoPauseOnUnfocus { get; set; } = true;
        public bool FastStartup { get; set; } = true;
    }

    public class WallpaperManager
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "JWall";
        private const string SerializeRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
        private const string SerializeValueName = "StartupDelayInMSec";
        private const string LiveEarthRepoZipUrl = "https://github.com/Pickle776/Live-Earth/archive/refs/heads/main.zip";
        private const string ThreeJsCdnUrl = "https://unpkg.com/three@0.160.0/build/three.module.js";

        private static readonly HashSet<string> IgnoredFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "wallpapers", "src", "bin", "obj", "dist", "runtimes", ".git", ".vs", ".github", ".idea"
        };

        private readonly SemaphoreSlim _downloadLock = new(1, 1);

        public string BaseDirectory { get; }
        public string RootDirectory { get; }
        public string WallpapersDirectory { get; }
        public string AppDataDirectory { get; }
        public string ConfigPath { get; }
        public AppSettings Settings { get; private set; } = new();

        public WallpaperManager()
        {
            BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            // Check ./wallpapers, then ../wallpapers (if running from bin/ or dist/)
            string localWallpapers = Path.Combine(BaseDirectory, "wallpapers");
            string trimmedBase = BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Directory.GetParent(trimmedBase)?.FullName ?? trimmedBase;
            string parentWallpapers = Path.Combine(parent, "wallpapers");

            if (Directory.Exists(localWallpapers))
            {
                RootDirectory = BaseDirectory;
                WallpapersDirectory = localWallpapers;
            }
            else if (Directory.Exists(parentWallpapers))
            {
                RootDirectory = parent;
                WallpapersDirectory = parentWallpapers;
            }
            else
            {
                RootDirectory = BaseDirectory;
                WallpapersDirectory = localWallpapers;
            }

            AppDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JWall"
            );
            try { Directory.CreateDirectory(AppDataDirectory); } catch { }

            ConfigPath = Path.Combine(AppDataDirectory, "config.json");
            try { Directory.SetCurrentDirectory(RootDirectory); } catch { }
            EnsureDefaultSetup();
            Settings = LoadSettings();

            // Sync startup registration to match FastStartup setting if startup is enabled
            if (IsRunAtStartup())
            {
                SetRunAtStartup(true);
            }
            else
            {
                SetFastStartupRegistry(Settings.FastStartup);
            }
        }

        public void EnsureDefaultSetup()
        {
            try
            {
                if (!Directory.Exists(WallpapersDirectory))
                {
                    Directory.CreateDirectory(WallpapersDirectory);
                }

                string readmePath = Path.Combine(WallpapersDirectory, "README.txt");
                if (!File.Exists(readmePath))
                {
                    File.WriteAllText(readmePath,
@"JWall - Quick Guide
===================

How to add your own wallpapers:

1. Website or Live Stream (YouTube Live, Weather Radar, Webcams, etc.):
   - Create a new folder inside 'wallpapers' (or just drop a .txt file here).
   - Put the website link (starting with https://) inside a .txt file.
   - Example: https://www.youtube.com/@NASA/live

2. Local HTML5 / WebGL Wallpaper:
   - Drop any folder containing an 'index.html' file into 'wallpapers'
     (or even directly next to JWall.exe).

3. Switching Wallpapers:
   - Right-click the JWall icon in your Windows System Tray
     (bottom-right corner of your screen by the clock) to switch wallpapers
     or change options at any time.
");
                }

                string streamDir = Path.Combine(WallpapersDirectory, "ISS Stream");
                if (!Directory.Exists(streamDir))
                {
                    Directory.CreateDirectory(streamDir);
                }
                string streamFile = Path.Combine(streamDir, "stream.txt");
                if (!File.Exists(streamFile))
                {
                    File.WriteAllText(streamFile, "https://www.youtube.com/@NASA/live\r\n");
                    Program.Log("First-run: Successfully created default ISS Stream wallpaper.");
                }

                if (!File.Exists(ConfigPath))
                {
                    Settings = new AppSettings
                    {
                        LastWallpaper = "Live Earth",
                        MouseInteraction = true,
                        AutoPauseOnUnfocus = true,
                        FastStartup = true
                    };
                    SaveSettings();
                    Program.Log("First-run: Created default config.json.");
                }
            }
            catch (Exception ex)
            {
                Program.Log($"EnsureDefaultSetup exception: {ex.Message}");
            }
        }

        public bool IsLiveEarthInstalled()
        {
            string earthDir = Path.Combine(WallpapersDirectory, "Live Earth");
            return Directory.Exists(earthDir) && Directory.GetFiles(earthDir, "*.html").Length > 0;
        }

        public async Task<bool> EnsureLiveEarthInstalledAsync()
        {
            if (IsLiveEarthInstalled()) return true;

            await _downloadLock.WaitAsync();
            try
            {
                if (IsLiveEarthInstalled()) return true;

                string earthDir = Path.Combine(WallpapersDirectory, "Live Earth");
                Directory.CreateDirectory(earthDir);

                Program.Log("First-run: Downloading Live Earth from GitHub (Pickle776/Live-Earth)...");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("JWall/1.0");

                byte[] zipBytes = await http.GetByteArrayAsync(LiveEarthRepoZipUrl);
                using var ms = new MemoryStream(zipBytes);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

                // Locate the folder in the repository zip that contains the main HTML file
                var htmlEntry = archive.Entries.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Name) &&
                    e.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase));

                string prefix = string.Empty;
                if (htmlEntry != null)
                {
                    int lastSlash = htmlEntry.FullName.LastIndexOf('/');
                    if (lastSlash >= 0)
                    {
                        prefix = htmlEntry.FullName.Substring(0, lastSlash + 1);
                    }
                }

                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (!string.IsNullOrEmpty(prefix) && !entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relativePath = string.IsNullOrEmpty(prefix)
                        ? entry.FullName
                        : entry.FullName.Substring(prefix.Length);

                    // Skip nested subdirectories if any; extract wallpaper assets flat into Live Earth
                    if (relativePath.Contains('/')) continue;

                    string destPath = Path.Combine(earthDir, relativePath);
                    entry.ExtractToFile(destPath, overwrite: true);
                }

                // Cache three.module.js locally so Live Earth works 100% offline on future boots
                try
                {
                    string threeLocalPath = Path.Combine(earthDir, "three.module.js");
                    if (!File.Exists(threeLocalPath))
                    {
                        string threeJs = await http.GetStringAsync(ThreeJsCdnUrl);
                        await File.WriteAllTextAsync(threeLocalPath, threeJs);
                    }

                    foreach (var htmlFile in Directory.GetFiles(earthDir, "*.html"))
                    {
                        string htmlContent = await File.ReadAllTextAsync(htmlFile);
                        if (htmlContent.Contains(ThreeJsCdnUrl))
                        {
                            htmlContent = htmlContent.Replace(ThreeJsCdnUrl, "./three.module.js");
                            await File.WriteAllTextAsync(htmlFile, htmlContent);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"Optional Three.js offline cache note: {ex.Message}");
                }

                Program.Log("First-run: Successfully downloaded and extracted Live Earth from GitHub.");
                return IsLiveEarthInstalled();
            }
            catch (Exception ex)
            {
                Program.Log($"EnsureLiveEarthInstalledAsync exception: {ex.Message}");
                return false;
            }
            finally
            {
                _downloadLock.Release();
            }
        }

        public List<WallpaperInfo> DiscoverWallpapers()
        {
            EnsureDefaultSetup();

            var results = new List<WallpaperInfo>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Scan subfolders inside WallpapersDirectory (standard location)
            if (Directory.Exists(WallpapersDirectory))
            {
                foreach (var dir in Directory.GetDirectories(WallpapersDirectory))
                {
                    var info = TryCreateWallpaperFromDirectory(dir);
                    if (info != null && seenNames.Add(info.Name))
                    {
                        results.Add(info);
                    }
                }

                // Also support loose .txt / .url files dropped directly into WallpapersDirectory
                foreach (var file in Directory.GetFiles(WallpapersDirectory))
                {
                    var info = TryCreateWallpaperFromLooseFile(file);
                    if (info != null && seenNames.Add(info.Name))
                    {
                        results.Add(info);
                    }
                }
            }

            // 2. Also scan subfolders & loose .txt/.url files dropped directly next to JWall.exe
            if (Directory.Exists(RootDirectory))
            {
                foreach (var dir in Directory.GetDirectories(RootDirectory))
                {
                    string dirName = Path.GetFileName(dir);
                    if (IgnoredFolderNames.Contains(dirName)) continue;

                    var info = TryCreateWallpaperFromDirectory(dir);
                    if (info != null && seenNames.Add(info.Name))
                    {
                        results.Add(info);
                    }
                }

                foreach (var file in Directory.GetFiles(RootDirectory))
                {
                    var info = TryCreateWallpaperFromLooseFile(file);
                    if (info != null && seenNames.Add(info.Name))
                    {
                        results.Add(info);
                    }
                }
            }

            return results.OrderBy(w => w.Name).ToList();
        }

        private static WallpaperInfo? TryCreateWallpaperFromDirectory(string dir)
        {
            try
            {
                string dirName = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(dirName) ||
                    dirName.StartsWith(".") ||
                    IgnoredFolderNames.Contains(dirName) ||
                    dirName.EndsWith(".WebView2", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var allFiles = Directory.GetFiles(dir);
                if (allFiles.Length == 0) return null;

                // Priority 1: Check for HTML files
                var htmlFiles = Directory.GetFiles(dir, "*.html");
                string? targetHtml = null;

                if (htmlFiles.Length == 1)
                {
                    targetHtml = htmlFiles[0];
                }
                else if (htmlFiles.Length > 1)
                {
                    targetHtml = htmlFiles.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f).IndexOf("index", StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? htmlFiles[0];
                }

                if (!string.IsNullOrEmpty(targetHtml))
                {
                    return new WallpaperInfo
                    {
                        Name = dirName,
                        DirectoryPath = dir,
                        HtmlPath = targetHtml,
                        Url = string.Empty
                    };
                }

                // Priority 2: Text / URL files (when no HTML file exists)
                var textFiles = Directory.GetFiles(dir, "*.txt")
                    .Concat(Directory.GetFiles(dir, "*.url"));

                foreach (var file in textFiles)
                {
                    if (Path.GetFileName(file).Equals("README.txt", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? url = ExtractUrlFromFile(file);
                    if (!string.IsNullOrEmpty(url))
                    {
                        return new WallpaperInfo
                        {
                            Name = dirName,
                            DirectoryPath = dir,
                            HtmlPath = string.Empty,
                            Url = url
                        };
                    }
                }
            }
            catch { }

            return null;
        }

        private static WallpaperInfo? TryCreateWallpaperFromLooseFile(string filePath)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                if (fileName.StartsWith("README", StringComparison.OrdinalIgnoreCase) ||
                    fileName.StartsWith("ISSUES", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string ext = Path.GetExtension(filePath);
                if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    string? url = ExtractUrlFromFile(filePath);
                    if (!string.IsNullOrEmpty(url))
                    {
                        return new WallpaperInfo
                        {
                            Name = Path.GetFileNameWithoutExtension(filePath),
                            DirectoryPath = Path.GetDirectoryName(filePath) ?? string.Empty,
                            HtmlPath = string.Empty,
                            Url = url
                        };
                    }
                }
            }
            catch { }

            return null;
        }

        private static string? ExtractUrlFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;

                var lines = File.ReadAllLines(filePath);
                foreach (var rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("//"))
                        continue;

                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        line = line.Substring(4).Trim();
                    }

                    if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        return line;
                    }
                }
            }
            catch { }
            return null;
        }

        public AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null) return settings;
                }
            }
            catch { }

            return new AppSettings();
        }

        public void SaveSettings()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(Settings, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        private const string TaskSchedulerName = "JWall";

        public bool IsScheduledTaskRegistered()
        {
            try
            {
                Type? tsType = Type.GetTypeFromProgID("Schedule.Service");
                if (tsType == null) return false;
                dynamic ts = Activator.CreateInstance(tsType)!;
                ts.Connect();
                dynamic folder = ts.GetFolder(@"\");
                dynamic? task = folder.GetTask(TaskSchedulerName);
                if (task == null) return false;
                return (bool)task.Enabled;
            }
            catch
            {
                return false;
            }
        }

        public bool RegisterScheduledTask(string exePath)
        {
            try
            {
                Type? tsType = Type.GetTypeFromProgID("Schedule.Service");
                if (tsType == null) return false;
                dynamic ts = Activator.CreateInstance(tsType)!;
                ts.Connect();
                dynamic folder = ts.GetFolder(@"\");

                dynamic taskDef = ts.NewTask(0);
                taskDef.RegistrationInfo.Description = "Starts JWall immediately at user logon without Explorer delay.";
                taskDef.RegistrationInfo.Author = "JWall";

                taskDef.Principal.UserId = Environment.UserName;
                taskDef.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
                taskDef.Principal.RunLevel = 0;  // TASK_RUNLEVEL_LUA (Standard user / limited)

                taskDef.Settings.DisallowStartIfOnBatteries = false;
                taskDef.Settings.StopIfGoingOnBatteries = false;
                taskDef.Settings.ExecutionTimeLimit = "PT0S";
                taskDef.Settings.Priority = 4; // Normal priority

                dynamic trigger = taskDef.Triggers.Create(9); // TASK_TRIGGER_LOGON
                trigger.UserId = Environment.UserName;
                trigger.Enabled = true;

                dynamic action = taskDef.Actions.Create(0); // TASK_ACTION_EXEC
                action.Path = exePath;
                action.Arguments = "--startup";
                action.WorkingDirectory = RootDirectory;

                folder.RegisterTaskDefinition(TaskSchedulerName, taskDef, 6, null, null, 3, null); // TASK_CREATE_OR_UPDATE
                return true;
            }
            catch (Exception ex)
            {
                Program.Log($"RegisterScheduledTask exception: {ex.Message}");
                return false;
            }
        }

        public bool DeleteScheduledTask()
        {
            try
            {
                Type? tsType = Type.GetTypeFromProgID("Schedule.Service");
                if (tsType == null) return false;
                dynamic ts = Activator.CreateInstance(tsType)!;
                ts.Connect();
                dynamic folder = ts.GetFolder(@"\");
                folder.DeleteTask(TaskSchedulerName, 0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool IsRunAtStartup()
        {
            if (IsScheduledTaskRegistered()) return true;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                return key?.GetValue(RunValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        public string GetExecutablePath()
        {
            try
            {
                string? procPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(procPath) && File.Exists(procPath))
                {
                    return procPath;
                }

                string localExe = Path.Combine(BaseDirectory, "JWall.exe");
                if (File.Exists(localExe)) return localExe;

                string rootExe = Path.Combine(RootDirectory, "JWall.exe");
                if (File.Exists(rootExe)) return rootExe;
            }
            catch { }

            return Path.Combine(BaseDirectory, "JWall.exe");
        }

        public void SetRunAtStartup(bool enable)
        {
            try
            {
                string exePath = GetExecutablePath();

                if (enable)
                {
                    if (Settings.FastStartup)
                    {
                        RegisterScheduledTask(exePath);
                        try
                        {
                            using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                            runKey?.DeleteValue(RunValueName, false);
                        }
                        catch { }
                        SetFastStartupRegistry(true);
                    }
                    else
                    {
                        DeleteScheduledTask();
                        try
                        {
                            using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                            runKey?.SetValue(RunValueName, $"\"{exePath}\" --startup");
                        }
                        catch { }
                        SetFastStartupRegistry(false);
                    }
                }
                else
                {
                    DeleteScheduledTask();
                    try
                    {
                        using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                        runKey?.DeleteValue(RunValueName, false);
                    }
                    catch { }
                    SetFastStartupRegistry(false);
                }
            }
            catch { }
        }

        public void SetFastStartup(bool enable)
        {
            try
            {
                Settings.FastStartup = enable;
                SaveSettings();

                if (IsRunAtStartup())
                {
                    SetRunAtStartup(true);
                }
                else
                {
                    SetFastStartupRegistry(enable);
                }
            }
            catch { }
        }

        private void SetFastStartupRegistry(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SerializeRegistryKey, true);
                if (key != null)
                {
                    if (enable)
                    {
                        key.SetValue(SerializeValueName, 0, RegistryValueKind.DWord);
                    }
                    else
                    {
                        key.DeleteValue(SerializeValueName, false);
                    }
                }
            }
            catch { }
        }
    }
}
