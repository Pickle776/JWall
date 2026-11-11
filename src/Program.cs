using System;
using System.Threading;
using System.Windows.Forms;

namespace JWall
{
    internal static class Program
    {
        private const string MutexName = "Global\\JWall_SingleInstance_Mutex";
        public static bool DebugEnabled { get; private set; }
        private static string LogPath => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");

        public static void Log(string msg)
        {
            if (!DebugEnabled) return;
            try
            {
                System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            DebugEnabled = args != null && Array.Exists(args, a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));

            if (args != null && Array.Exists(args, a => a.Equals("--restart", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting("Global\\JWall_Restart_Event", out var restartEvt))
                    {
                        restartEvt.Set();
                        restartEvt.Dispose();
                        Log("CLI --restart: Signaled restart event to running instance.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log($"CLI --restart EventWaitHandle exception: {ex.Message}");
                }

                int restartMsg = DesktopWorkerW.RegisterWindowMessage("JWall_Restart");
                DesktopWorkerW.PostMessage(new IntPtr(0xFFFF), (uint)restartMsg, IntPtr.Zero, IntPtr.Zero);
                Log("CLI --restart: Broadcasted restart message to running instance.");
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log($"ThreadException: {e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log($"UnhandledException: {e.ExceptionObject}");
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Log("ProcessExit called!");
            Application.ApplicationExit += (s, e) => Log("ApplicationExit called!");

            Log("=== Application Starting ===");
            try
            {
                // Enforce single instance (allow up to 2.5s for previous instance to exit during restarts)
                using var mutex = new Mutex(true, MutexName, out bool createdNew);
                Log($"Mutex createdNew: {createdNew}");
                if (!createdNew)
                {
                    bool acquired = false;
                    try
                    {
                        acquired = mutex.WaitOne(2500, false);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                    {
                        Log("Another instance is already running. Exiting.");
                        return;
                    }
                    Log("Acquired mutex after previous instance exited.");
                }

                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var manager = new WallpaperManager();
                Log($"BaseDirectory: {manager.BaseDirectory}");
                Log($"WallpapersDirectory: {manager.WallpapersDirectory}");

                // Prioritize startup if FastStartup is enabled, otherwise yield to other startup apps
                try
                {
                    System.Diagnostics.Process.GetCurrentProcess().PriorityClass = manager.Settings.FastStartup
                        ? System.Diagnostics.ProcessPriorityClass.AboveNormal
                        : System.Diagnostics.ProcessPriorityClass.BelowNormal;
                }
                catch { }

                Log("Starting WallpaperForm...");
                var form = new WallpaperForm(manager);
                form.Show();
                Application.Run();
                Log("Application.Run exited cleanly.");
            }
            catch (Exception ex)
            {
                Log($"FATAL EXCEPTION: {ex}");
                if (DebugEnabled)
                {
                    try
                    {
                        string logFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                        System.IO.File.WriteAllText(logFile, ex.ToString());
                    }
                    catch { }
                }
            }
        }
    }
}
