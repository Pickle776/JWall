using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace JWall
{
    public class WallpaperForm : Form
    {
        private readonly WallpaperManager _manager;
        private readonly MouseTracker _mouseTracker;
        private WebView2 _webView;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly System.Windows.Forms.Timer _focusCheckTimer;
        private readonly System.Windows.Forms.Timer _attachRetryTimer;

        private int _taskbarCreatedMsg = 0;
        private int _restartMsg = 0;
        private EventWaitHandle? _restartEvent;
        private RegisteredWaitHandle? _registeredRestartWait;
        private WallpaperInfo? _currentWallpaper;
        private IntPtr _currentWorkerW = IntPtr.Zero;
        private bool _isAttached = false;
        private bool _isWebViewInitialized = false;
        private bool _isAutoPaused = false;
        private bool _isDisposed = false;
        private DateTime _lastDesktopInteractionUtc = DateTime.UtcNow;

        private bool _isMoveInFlight = false;
        private (int x, int y, bool leftDown)? _pendingMove = null;

        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_APMSUSPEND = 0x0004;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public WallpaperForm(WallpaperManager manager)
        {
            _manager = manager;

            // Form setup
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            BackColor = Color.Black;

            // Register custom window messages and restart event
            _taskbarCreatedMsg = DesktopWorkerW.RegisterWindowMessage("TaskbarCreated");
            _restartMsg = DesktopWorkerW.RegisterWindowMessage("JWall_Restart");

            try
            {
                _restartEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\JWall_Restart_Event");
                _registeredRestartWait = ThreadPool.RegisterWaitForSingleObject(_restartEvent, (state, timedOut) =>
                {
                    if (!timedOut && !_isDisposed)
                    {
                        try
                        {
                            BeginInvoke(new Action(RestartApplication));
                        }
                        catch { }
                    }
                }, null, -1, false);
            }
            catch (Exception ex)
            {
                Program.Log($"Restart event registration error: {ex.Message}");
            }

            // WebView2 control
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Black
            };
            Controls.Add(_webView);

            // Tray Context Menu
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Opening += (s, e) => UpdateTrayMenu();
            _trayIcon = new NotifyIcon
            {
                Text = "JWall",
                Icon = SystemIcons.Application,
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            // Mouse & Keyboard Tracker setup
            _mouseTracker = new MouseTracker
            {
                Enabled = _manager.Settings.MouseInteraction
            };
            _mouseTracker.DesktopMouseDown += OnDesktopMouseDown;
            _mouseTracker.DesktopMouseUp += OnDesktopMouseUp;
            _mouseTracker.DesktopMouseMove += OnDesktopMouseMove;
            _mouseTracker.DesktopMouseWheel += OnDesktopMouseWheel;
            _mouseTracker.DesktopBlurred += OnDesktopBlurred;
            _mouseTracker.DesktopKeyDown += OnDesktopKeyDown;
            _mouseTracker.DesktopTextInput += OnDesktopTextInput;

            // Focus Check Timer (Checks desktop focus every 200ms with negligible CPU)
            _focusCheckTimer = new System.Windows.Forms.Timer
            {
                Interval = 200
            };
            _focusCheckTimer.Tick += (s, e) => CheckFocusState();

            // Attach Retry Timer (ensures attachment succeeds during startup/boot when Explorer is slow)
            _attachRetryTimer = new System.Windows.Forms.Timer
            {
                Interval = _manager.Settings.FastStartup ? 200 : 1000
            };
            _attachRetryTimer.Tick += async (s, e) =>
            {
                if (!_isAttached)
                {
                    _isAttached = AttachToDesktopWorker();
                    if (_isAttached)
                    {
                        Program.Log("Explorer desktop is now ready! Successfully attached to WorkerW on retry.");
                        _attachRetryTimer.Stop();
                        if (!_isWebViewInitialized)
                        {
                            await InitializeWebViewAsync();
                        }
                    }
                }
                else
                {
                    _attachRetryTimer.Stop();
                }
            };

            // Form Shown event (fires after window handle is active)
            Shown += async (s, e) =>
            {
                Program.Log("WallpaperForm.Shown started.");
                try
                {
                    _isAttached = AttachToDesktopWorker();
                    if (_isAttached)
                    {
                        await InitializeWebViewAsync();
                    }
                    else
                    {
                        Program.Log("Desktop icons (SHELLDLL_DefView) not ready yet (boot/startup in progress). Waiting for Explorer...");
                        _attachRetryTimer.Start();
                    }

                    _mouseTracker.Start();
                    _focusCheckTimer.Start();
                    UpdateTrayMenu();
                    try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal; } catch { }
                    Program.Log("WallpaperForm setup completed successfully.");
                }
                catch (Exception ex)
                {
                    Program.Log($"Exception in WallpaperForm.Shown: {ex}");
                }
            };
        }

        private bool AttachToDesktopWorker()
        {
            if (_isDisposed || Disposing || !IsHandleCreated) return false;
            try
            {
                Rectangle screenBounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                IntPtr workerw;
                bool ok = DesktopWorkerW.AttachToDesktop(Handle, screenBounds, out workerw);
                if (ok)
                {
                    _currentWorkerW = workerw;
                }
                return ok;
            }
            catch (Exception ex)
            {
                Program.Log($"AttachToDesktopWorker exception: {ex.Message}");
                return false;
            }
        }

        private void RecreateWebView()
        {
            try
            {
                if (_webView != null)
                {
                    Controls.Remove(_webView);
                    _webView.Dispose();
                }
            }
            catch { }

            _isWebViewInitialized = false;
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Black
            };
            Controls.Add(_webView);
        }

        private void RestartApplication()
        {
            try
            {
                Program.Log("RestartApplication requested by user.");
                _manager.SaveSettings();

                string exePath = _manager.GetExecutablePath();
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = _manager.RootDirectory
                };

                Process.Start(startInfo);

                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }

                Application.Exit();
            }
            catch (Exception ex)
            {
                Program.Log($"RestartApplication exception: {ex.Message}. Falling back to internal engine reset.");
                ResetAndReloadWallpaper();
            }
        }

        private void ResetAndReloadWallpaper()
        {
            try
            {
                Program.Log("ResetAndReloadWallpaper started.");
                _isAttached = false;
                RecreateWebView();
                _isAttached = AttachToDesktopWorker();
                if (_isAttached)
                {
                    _ = InitializeWebViewAsync();
                }
                else
                {
                    _attachRetryTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Program.Log($"ResetAndReloadWallpaper exception: {ex}");
            }
        }

        private async Task InitializeWebViewAsync()
        {
            if (_isWebViewInitialized) return;
            _isWebViewInitialized = true;
            try
            {
                Program.Log("Initializing WebView2...");
                string userDataFolder = Path.Combine(_manager.AppDataDirectory, "WebView2");
                var envOptions = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions);
                await _webView.EnsureCoreWebView2Async(env);
                Program.Log("EnsureCoreWebView2Async completed successfully!");

                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.CoreWebView2.Settings.IsScriptEnabled = true;

                // Standard desktop Chrome User-Agent
                _webView.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

                // Emulate active tab focus so hover, menus, inputs, and fullscreen work behind desktop icons
                try
                {
                    await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", @"{""enabled"":true}");
                }
                catch { }

                // Handle external links or popups by navigating in-place
                _webView.CoreWebView2.NewWindowRequested += (s, args) =>
                {
                    args.Handled = true;
                    if (!string.IsNullOrEmpty(args.Uri))
                    {
                        _webView.CoreWebView2.Navigate(args.Uri);
                    }
                };

                // Universal pause/resume RAF hook + generic W3C Fullscreen API support for child HWNDs
                string initScript = @"
                (function() {
                    window.__isPaused = false;
                    const _rafQueue = [];
                    const _origRAF = window.requestAnimationFrame;

                    window.__pauseWallpaper = function() {
                        window.__isPaused = true;
                    };

                    window.__resumeWallpaper = function() {
                        if (window.__isPaused) {
                            window.__isPaused = false;
                            const pending = _rafQueue.splice(0);
                            for (const cb of pending) {
                                try { _origRAF(cb); } catch (e) {}
                            }
                        }
                    };

                    window.requestAnimationFrame = function(callback) {
                        if (window.__isPaused) {
                            _rafQueue.push(callback);
                            return _rafQueue.length;
                        }
                        return _origRAF(callback);
                    };

                    // Generic W3C Fullscreen API fallback when hosted as a WS_CHILD desktop window
                    let fsEl = null;
                    let savedScrollX = 0;
                    let savedScrollY = 0;
                    const origReq = Element.prototype.requestFullscreen;
                    const origExit = Document.prototype.exitFullscreen;
                    const origFsDesc = Object.getOwnPropertyDescriptor(Document.prototype, 'fullscreenElement')
                                    || Object.getOwnPropertyDescriptor(Document.prototype, 'webkitFullscreenElement');

                    function ensureFsStyle() {
                        if (!document.getElementById('__lw_fs_style')) {
                            const s = document.createElement('style');
                            s.id = '__lw_fs_style';
                            s.textContent = `
                                .__lw_fullscreen_el {
                                    position: fixed !important;
                                    inset: 0 !important;
                                    width: 100vw !important;
                                    height: 100vh !important;
                                    max-width: 100vw !important;
                                    max-height: 100vh !important;
                                    z-index: 2147483647 !important;
                                    background: #000 !important;
                                }
                                html.__lw_fullscreen_doc, html.__lw_fullscreen_doc body {
                                    overflow: hidden !important;
                                }
                            `;
                            (document.head || document.documentElement).appendChild(s);
                        }
                    }

                    function enterVirtualFs(el) {
                        ensureFsStyle();
                        savedScrollX = window.scrollX;
                        savedScrollY = window.scrollY;
                        if (fsEl && fsEl !== el) fsEl.classList.remove('__lw_fullscreen_el');
                        fsEl = el;
                        document.documentElement.classList.add('__lw_fullscreen_doc');
                        if (el !== document.documentElement && el !== document.body) {
                            el.classList.add('__lw_fullscreen_el');
                        }
                        window.scrollTo(0, 0);
                        document.dispatchEvent(new Event('fullscreenchange', { bubbles: true }));
                        document.dispatchEvent(new Event('webkitfullscreenchange', { bubbles: true }));
                        window.dispatchEvent(new Event('resize'));
                    }

                    function exitVirtualFs() {
                        if (fsEl) {
                            fsEl.classList.remove('__lw_fullscreen_el');
                            fsEl = null;
                            document.documentElement.classList.remove('__lw_fullscreen_doc');
                            document.dispatchEvent(new Event('fullscreenchange', { bubbles: true }));
                            document.dispatchEvent(new Event('webkitfullscreenchange', { bubbles: true }));
                            window.dispatchEvent(new Event('resize'));
                            window.scrollTo(savedScrollX, savedScrollY);
                        }
                    }

                    Object.defineProperty(Document.prototype, 'fullscreenElement', {
                        configurable: true,
                        get: function() {
                            const nativeEl = origFsDesc ? origFsDesc.get.call(this) : null;
                            return nativeEl || fsEl;
                        }
                    });
                    Object.defineProperty(Document.prototype, 'webkitFullscreenElement', {
                        configurable: true,
                        get: function() { return this.fullscreenElement; }
                    });
                    Object.defineProperty(Document.prototype, 'fullscreenEnabled', {
                        configurable: true,
                        get: function() { return true; }
                    });

                    Element.prototype.requestFullscreen = function(opts) {
                        const self = this;
                        if (origReq) {
                            return origReq.call(this, opts).catch(() => {
                                enterVirtualFs(self);
                                return Promise.resolve();
                            });
                        }
                        enterVirtualFs(self);
                        return Promise.resolve();
                    };
                    Element.prototype.webkitRequestFullscreen = Element.prototype.requestFullscreen;

                    Document.prototype.exitFullscreen = function() {
                        if (fsEl) {
                            exitVirtualFs();
                            return Promise.resolve();
                        }
                        return origExit ? origExit.call(this).catch(() => {}) : Promise.resolve();
                    };
                    Document.prototype.webkitExitFullscreen = Document.prototype.exitFullscreen;
                })();";
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(initScript);

                _webView.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    Program.Log($"NavigationCompleted: Success={e.IsSuccess}, WebErrorStatus={e.WebErrorStatus}");
                    try
                    {
                        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", @"{""enabled"":true}");
                    }
                    catch { }
                };

                _webView.CoreWebView2.ContainsFullScreenElementChanged += (s, e) =>
                {
                    try
                    {
                        _webView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
                    }
                    catch { }
                };

                // Ensure Live Earth is downloaded from GitHub on first run if missing
                if (!_manager.IsLiveEarthInstalled())
                {
                    _trayIcon.Text = "JWall: Downloading Live Earth...";
                    await _manager.EnsureLiveEarthInstalledAsync();
                }

                // Load initial wallpaper
                var wallpapers = _manager.DiscoverWallpapers();
                if (wallpapers.Count > 0)
                {
                    var selected = wallpapers.Find(w => w.Name.Equals(_manager.Settings.LastWallpaper, StringComparison.OrdinalIgnoreCase))
                                   ?? wallpapers[0];
                    LoadWallpaper(selected);
                }
            }
            catch (Exception ex)
            {
                Program.Log($"EXCEPTION in InitializeWebViewAsync: {ex}");
            }
        }

        public void LoadWallpaper(WallpaperInfo wallpaper)
        {
            if (_isDisposed || Disposing) return;
            try
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;

                _currentWallpaper = wallpaper;
                _mouseTracker.CaptureKeyboardForInput = false;
                _manager.Settings.LastWallpaper = wallpaper.Name;
                _manager.SaveSettings();

                try
                {
                    _webView.CoreWebView2.ClearVirtualHostNameToFolderMapping("wallpaper.local");
                }
                catch { }

                if (wallpaper.IsWebUrl)
                {
                    Program.Log($"Navigating to Web Wallpaper URL: {wallpaper.Url}");
                    _webView.CoreWebView2.IsMuted = true;
                    _webView.CoreWebView2.Navigate(wallpaper.Url);
                }
                else
                {
                    Program.Log($"Loading local HTML Wallpaper: {wallpaper.HtmlPath}");
                    _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "wallpaper.local",
                        wallpaper.DirectoryPath,
                        CoreWebView2HostResourceAccessKind.Allow
                    );

                    string entryFile = Path.GetFileName(wallpaper.HtmlPath);
                    _webView.CoreWebView2.Navigate($"https://wallpaper.local/{entryFile}");
                }

                _trayIcon.Text = $"JWall: {wallpaper.Name}";
                UpdateTrayMenu();
            }
            catch (Exception ex)
            {
                Program.Log($"LoadWallpaper exception: {ex.Message}. Recovering WebView2...");
                RecreateWebView();
                _ = InitializeWebViewAsync();
            }
        }

        private void CheckFocusState()
        {
            if (!_isAttached) return;

            // Health check: ensure our form is still parented to an existing WorkerW
            IntPtr curParent = DesktopWorkerW.GetParent(Handle);
            if (curParent == IntPtr.Zero || !DesktopWorkerW.IsWindow(curParent) || curParent != _currentWorkerW)
            {
                Program.Log($"WorkerW parent changed or lost (curParent={curParent}, _currentWorkerW={_currentWorkerW}). Re-attaching...");
                _isAttached = false;
                RecreateWebView();
                if (!AttachToDesktopWorker())
                {
                    _attachRetryTimer.Start();
                }
                else
                {
                    _isAttached = true;
                    _ = InitializeWebViewAsync();
                }
                return;
            }

            if (!_manager.Settings.AutoPauseOnUnfocus) return;

            bool recentlyInteracted = (DateTime.UtcNow - _lastDesktopInteractionUtc).TotalSeconds < 2.5;
            bool isDesktop = IsDesktopForeground() || recentlyInteracted;

            if (isDesktop && _isAutoPaused)
            {
                _isAutoPaused = false;
                ResumeRendering();
            }
            else if (!isDesktop && !_isAutoPaused)
            {
                _isAutoPaused = true;
                PauseRendering();
            }
        }

        public static bool IsDesktopForeground()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            IntPtr root = GetAncestor(fg, GA_ROOT);
            if (root == IntPtr.Zero) root = fg;

            var sb = new StringBuilder(256);
            GetClassNameW(root, sb, 256);
            string rootCls = sb.ToString();

            if (rootCls == "Progman" || rootCls == "WorkerW")
            {
                return true;
            }

            GetClassNameW(fg, sb, 256);
            string fgCls = sb.ToString();
            return fgCls == "SHELLDLL_DefView" || fgCls == "SysListView32";
        }

        private void PauseRendering()
        {
            try
            {
                // Do not freeze active websites/streams; let video players and web UIs run normally
                if (_currentWallpaper?.IsWebUrl == true) return;

                _webView?.CoreWebView2?.ExecuteScriptAsync("if (window.__pauseWallpaper) window.__pauseWallpaper();");
                if (_webView?.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                }
            }
            catch { }
        }

        private void ResumeRendering()
        {
            try
            {
                _webView?.CoreWebView2?.ExecuteScriptAsync("if (window.__resumeWallpaper) window.__resumeWallpaper();");
                if (_webView?.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                }
            }
            catch { }
        }

        private void UpdateTrayMenu()
        {
            if (_isDisposed || Disposing || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { Invoke(new Action(UpdateTrayMenu)); } catch { }
                return;
            }

            _trayMenu.Items.Clear();

            // 1. Wallpapers directly at top of menu with radio checkmarks
            var wallpapers = _manager.DiscoverWallpapers();
            foreach (var wp in wallpapers)
            {
                bool isCurrent = (_currentWallpaper != null && wp.Name.Equals(_currentWallpaper.Name, StringComparison.OrdinalIgnoreCase));
                var wpItem = new ToolStripMenuItem(wp.Name)
                {
                    Checked = isCurrent
                };
                wpItem.Click += (s, e) => LoadWallpaper(wp);
                _trayMenu.Items.Add(wpItem);
            }

            _trayMenu.Items.Add(new ToolStripSeparator());

            // 2. Single Options submenu containing all settings
            var optionsMenu = new ToolStripMenuItem("Options");

            // Mouse Interaction toggle
            var mouseItem = new ToolStripMenuItem("Mouse Interaction")
            {
                Checked = _mouseTracker.Enabled
            };
            mouseItem.Click += (s, e) =>
            {
                _mouseTracker.Enabled = !_mouseTracker.Enabled;
                _manager.Settings.MouseInteraction = _mouseTracker.Enabled;
                _manager.SaveSettings();
                UpdateTrayMenu();
            };
            optionsMenu.DropDownItems.Add(mouseItem);

            // Auto-pause toggle
            var autoPauseItem = new ToolStripMenuItem("Auto-pause when desktop unfocused")
            {
                Checked = _manager.Settings.AutoPauseOnUnfocus
            };
            autoPauseItem.Click += (s, e) =>
            {
                _manager.Settings.AutoPauseOnUnfocus = !_manager.Settings.AutoPauseOnUnfocus;
                _manager.SaveSettings();
                if (!_manager.Settings.AutoPauseOnUnfocus && _isAutoPaused)
                {
                    _isAutoPaused = false;
                    ResumeRendering();
                }
                UpdateTrayMenu();
            };
            optionsMenu.DropDownItems.Add(autoPauseItem);

            optionsMenu.DropDownItems.Add(new ToolStripSeparator());

            // Restart item
            var restartItem = new ToolStripMenuItem("Restart Wallpaper");
            restartItem.Click += (s, e) => RestartApplication();
            optionsMenu.DropDownItems.Add(restartItem);

            optionsMenu.DropDownItems.Add(new ToolStripSeparator());

            // Start with Windows toggle
            bool runAtStartup = _manager.IsRunAtStartup();
            var startupItem = new ToolStripMenuItem("Start with Windows")
            {
                Checked = runAtStartup
            };
            startupItem.Click += (s, e) =>
            {
                _manager.SetRunAtStartup(!runAtStartup);
                UpdateTrayMenu();
            };
            optionsMenu.DropDownItems.Add(startupItem);

            // Fast startup toggle
            var fastStartupItem = new ToolStripMenuItem("Launch quicker at startup")
            {
                Checked = _manager.Settings.FastStartup
            };
            fastStartupItem.Click += (s, e) =>
            {
                bool newState = !_manager.Settings.FastStartup;
                _manager.SetFastStartup(newState);
                _attachRetryTimer.Interval = newState ? 200 : 1000;
                UpdateTrayMenu();
            };
            optionsMenu.DropDownItems.Add(fastStartupItem);

            // Open Wallpapers folder
            var openFolderItem = new ToolStripMenuItem("Open Wallpapers Folder");
            openFolderItem.Click += (s, e) =>
            {
                try
                {
                    Process.Start("explorer.exe", _manager.WallpapersDirectory);
                }
                catch { }
            };
            optionsMenu.DropDownItems.Add(openFolderItem);

            _trayMenu.Items.Add(optionsMenu);
            _trayMenu.Items.Add(new ToolStripSeparator());

            // 3. Exit
            var exitItem = new ToolStripMenuItem("Exit JWall");
            exitItem.Click += (s, e) => Application.Exit();
            _trayMenu.Items.Add(exitItem);
        }

        private (string x, string y) ScreenToCssStrings(int screenX, int screenY)
        {
            double scale = (DeviceDpi > 0 ? DeviceDpi : 96.0) / 96.0;
            double cssX = (screenX - Left) / scale;
            double cssY = (screenY - Top) / scale;
            return (
                Math.Round(cssX, 2).ToString(CultureInfo.InvariantCulture),
                Math.Round(cssY, 2).ToString(CultureInfo.InvariantCulture)
            );
        }

        private void WakeOnDesktopActivity()
        {
            _lastDesktopInteractionUtc = DateTime.UtcNow;
            if (_isAutoPaused)
            {
                _isAutoPaused = false;
                ResumeRendering();
            }
        }

        private void OnDesktopMouseMove(int screenX, int screenY, bool isLeftDown)
        {
            if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
                    WakeOnDesktopActivity();

                    _pendingMove = (screenX, screenY, isLeftDown);
                    if (_isMoveInFlight) return;

                    _isMoveInFlight = true;
                    try
                    {
                        while (_pendingMove.HasValue && !_isDisposed && !Disposing && _webView?.CoreWebView2 != null)
                        {
                            var move = _pendingMove.Value;
                            _pendingMove = null;
                            var (cx, cy) = ScreenToCssStrings(move.x, move.y);
                            string btn = move.leftDown ? "left" : "none";
                            int buttons = move.leftDown ? 1 : 0;
                            string json = $"{{\"type\":\"mouseMoved\",\"x\":{cx},\"y\":{cy},\"button\":\"{btn}\",\"buttons\":{buttons}}}";
                            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json);
                        }
                    }
                    catch { }
                    finally
                    {
                        _isMoveInFlight = false;
                    }
                }));
            }
            catch { }
        }

        private void OnDesktopMouseDown(int screenX, int screenY, int clickCount)
        {
            if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
                    WakeOnDesktopActivity();

                    var (cx, cy) = ScreenToCssStrings(screenX, screenY);
                    try
                    {
                        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mouseMoved\",\"x\":{cx},\"y\":{cy},\"button\":\"none\",\"buttons\":0}}");
                        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mousePressed\",\"x\":{cx},\"y\":{cy},\"button\":\"left\",\"buttons\":1,\"clickCount\":{clickCount}}}");
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void OnDesktopMouseUp(int screenX, int screenY, int clickCount)
        {
            if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
                    WakeOnDesktopActivity();

                    var (cx, cy) = ScreenToCssStrings(screenX, screenY);
                    try
                    {
                        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mouseReleased\",\"x\":{cx},\"y\":{cy},\"button\":\"left\",\"buttons\":0,\"clickCount\":{clickCount}}}");

                        // Check if an input/editable element is now focused so keyboard typing can be routed to it
                        string isEditable = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                            (function() {
                                const a = document.activeElement;
                                if (!a) return false;
                                const t = a.tagName;
                                return t === 'INPUT' || t === 'TEXTAREA' || a.isContentEditable === true;
                            })()");
                        _mouseTracker.CaptureKeyboardForInput = (isEditable == "true");
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void OnDesktopMouseWheel(int screenX, int screenY, int delta)
        {
            if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
                    WakeOnDesktopActivity();

                    var (cx, cy) = ScreenToCssStrings(screenX, screenY);
                    int deltaY = -delta;
                    try
                    {
                        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mouseWheel\",\"x\":{cx},\"y\":{cy},\"deltaX\":0,\"deltaY\":{deltaY}}}");
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void OnDesktopTextInput(string text)
        {
            if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
                    try
                    {
                        string payload = JsonSerializer.Serialize(new { text });
                        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", payload);
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void OnDesktopKeyDown(int vk, bool ctrl, bool shift)
        {
            if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
                    try
                    {
                        if (ctrl)
                        {
                            if (vk == 0x56) // Ctrl+V Paste
                            {
                                if (Clipboard.ContainsText())
                                {
                                    string clip = Clipboard.GetText();
                                    string payload = JsonSerializer.Serialize(new { text = clip });
                                    await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", payload);
                                }
                                return;
                            }
                            if (vk == 0x41) // Ctrl+A Select All
                            {
                                await _webView.CoreWebView2.ExecuteScriptAsync("if (document.activeElement && document.activeElement.select) document.activeElement.select();");
                                return;
                            }
                        }

                        var (key, code, text) = vk switch
                        {
                            0x08 => ("Backspace", "Backspace", ""),
                            0x09 => ("Tab", "Tab", ""),
                            0x0D => ("Enter", "Enter", "\\r"),
                            0x1B => ("Escape", "Escape", ""),
                            0x20 => (" ", "Space", " "),
                            0x23 => ("End", "End", ""),
                            0x24 => ("Home", "Home", ""),
                            0x25 => ("ArrowLeft", "ArrowLeft", ""),
                            0x26 => ("ArrowUp", "ArrowUp", ""),
                            0x27 => ("ArrowRight", "ArrowRight", ""),
                            0x28 => ("ArrowDown", "ArrowDown", ""),
                            0x2E => ("Delete", "Delete", ""),
                            _ => ("", "", "")
                        };

                        if (string.IsNullOrEmpty(key)) return;

                        if (vk == 0x1B) // Escape blurs input or exits fullscreen
                        {
                            _mouseTracker.CaptureKeyboardForInput = false;
                            await _webView.CoreWebView2.ExecuteScriptAsync(@"
                                if (document.fullscreenElement) document.exitFullscreen();
                                if (document.activeElement) document.activeElement.blur();
                            ");
                            return;
                        }

                        int modifiers = (ctrl ? 2 : 0) | (shift ? 8 : 0);
                        string textProp = !string.IsNullOrEmpty(text) ? $",\"text\":\"{text}\",\"unmodifiedText\":\"{text}\"" : "";
                        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                            $"{{\"type\":\"rawKeyDown\",\"windowsVirtualKeyCode\":{vk},\"nativeVirtualKeyCode\":{vk},\"key\":\"{key}\",\"code\":\"{code}\",\"modifiers\":{modifiers}{textProp}}}");
                        if (!string.IsNullOrEmpty(text))
                        {
                            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                                $"{{\"type\":\"char\",\"windowsVirtualKeyCode\":{vk},\"key\":\"{key}\",\"code\":\"{code}\",\"modifiers\":{modifiers}{textProp}}}");
                        }
                        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                            $"{{\"type\":\"keyUp\",\"windowsVirtualKeyCode\":{vk},\"nativeVirtualKeyCode\":{vk},\"key\":\"{key}\",\"code\":\"{code}\",\"modifiers\":{modifiers}}}");
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void OnDesktopBlurred()
        {
            if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (_isDisposed || Disposing || !IsHandleCreated || _webView?.CoreWebView2 == null) return;
                    string js = @"
                    (function() {
                        const target = document.querySelector('canvas') || document.body || window;
                        const evt = new Event('blur', { bubbles: true });
                        target.dispatchEvent(evt);
                        window.dispatchEvent(evt);
                    })();";
                    try { _webView.CoreWebView2?.ExecuteScriptAsync(js); } catch { }
                }));
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            // Programmatic restart request (e.g. from tray click or CLI --restart)
            if (_restartMsg != 0 && m.Msg == _restartMsg)
            {
                Program.Log("JWall_Restart message received. Restarting application...");
                RestartApplication();
                return;
            }

            // Explorer restarted (Taskbar recreated) -> re-attach to new WorkerW
            if (_taskbarCreatedMsg != 0 && m.Msg == _taskbarCreatedMsg)
            {
                Program.Log("TaskbarCreated message received. Re-attaching to WorkerW...");
                _isAttached = false;
                if (!AttachToDesktopWorker())
                {
                    _attachRetryTimer.Start();
                }
                else
                {
                    _isAttached = true;
                }
                return;
            }

            // Display resolution or monitor change -> resize to primary screen and re-attach
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                Program.Log("WM_DISPLAYCHANGE received. Updating bounds and re-attaching...");
                Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                _isAttached = AttachToDesktopWorker();
                if (!_isAttached) _attachRetryTimer.Start();
            }

            // System sleep / wake transitions
            if (m.Msg == WM_POWERBROADCAST)
            {
                int evt = m.WParam.ToInt32();
                if (evt == PBT_APMSUSPEND)
                {
                    PauseRendering();
                }
                else if (evt == PBT_APMRESUMESUSPEND || evt == PBT_APMRESUMEAUTOMATIC)
                {
                    _isAttached = AttachToDesktopWorker();
                    if (!_isAttached) _attachRetryTimer.Start();
                    if (!_isAutoPaused)
                    {
                        ResumeRendering();
                    }
                }
            }

            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            _isDisposed = true;
            if (disposing)
            {
                try { _registeredRestartWait?.Unregister(null); } catch { }
                try { _restartEvent?.Dispose(); } catch { }
                _focusCheckTimer?.Stop();
                _focusCheckTimer?.Dispose();
                _attachRetryTimer?.Stop();
                _attachRetryTimer?.Dispose();
                _mouseTracker?.Dispose();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                _trayMenu?.Dispose();
                _webView?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
