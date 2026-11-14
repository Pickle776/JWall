using System;
using System.Collections.Generic;
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
            Bounds = GetVirtualScreenBounds();
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

        private static Rectangle GetVirtualScreenBounds()
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            if (vs.Width > 0 && vs.Height > 0)
            {
                return vs;
            }
            return Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        }

        private string GetDisplayConfigJson()
        {
            Rectangle vs = GetVirtualScreenBounds();
            Screen[] allScreens = Screen.AllScreens;
            Rectangle primary = Screen.PrimaryScreen?.Bounds ?? vs;

            var screensList = new List<object>(allScreens.Length);
            foreach (var s in allScreens)
            {
                Rectangle b = s.Bounds;
                screensList.Add(new
                {
                    primary = s.Primary,
                    nx = Math.Round((b.X - vs.X) / (double)vs.Width, 6),
                    ny = Math.Round((b.Y - vs.Y) / (double)vs.Height, 6),
                    nw = Math.Round(b.Width / (double)vs.Width, 6),
                    nh = Math.Round(b.Height / (double)vs.Height, 6)
                });
            }

            string mode = _manager.Settings.MultiMonitorMode;
            if (string.IsNullOrEmpty(mode) ||
                (!mode.Equals("Extend", StringComparison.OrdinalIgnoreCase) &&
                 !mode.Equals("Mirror", StringComparison.OrdinalIgnoreCase) &&
                 !mode.Equals("Stretch", StringComparison.OrdinalIgnoreCase)))
            {
                mode = "Extend";
            }

            var payload = new
            {
                mode,
                multiMonitor = allScreens.Length > 1,
                primary = new
                {
                    nx = Math.Round((primary.X - vs.X) / (double)vs.Width, 6),
                    ny = Math.Round((primary.Y - vs.Y) / (double)vs.Height, 6),
                    nw = Math.Round(primary.Width / (double)vs.Width, 6),
                    nh = Math.Round(primary.Height / (double)vs.Height, 6)
                },
                screens = screensList
            };

            return JsonSerializer.Serialize(payload);
        }

        private void PushDisplayConfigToWebView()
        {
            if (_isDisposed || Disposing || _webView?.CoreWebView2 == null) return;
            try
            {
                string json = GetDisplayConfigJson();
                _webView.CoreWebView2.ExecuteScriptAsync($"if (window.__jwallUpdateDisplay) window.__jwallUpdateDisplay({json});");
            }
            catch { }
        }

        private bool AttachToDesktopWorker()
        {
            if (_isDisposed || Disposing || !IsHandleCreated) return false;
            try
            {
                Rectangle screenBounds = GetVirtualScreenBounds();
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

                // Universal pause/resume RAF hook + generic W3C Fullscreen API + multi-monitor engine
                string displayConfigJson = GetDisplayConfigJson();
                string initScript = $"window.__jwallConfig = {displayConfigJson};\n" + @"
                (function() {
                    window.__isPaused = false;
                    window.__jwallHasWebGL = false;
                    window.__jwallFrameDrawn = false;
                    const _rafQueue = [];
                    const _origRAF = window.requestAnimationFrame;

                    const origInnerW = Object.getOwnPropertyDescriptor(window, 'innerWidth');
                    const origInnerH = Object.getOwnPropertyDescriptor(window, 'innerHeight');

                    function getRealWidth() {
                        return origInnerW && origInnerW.get ? origInnerW.get.call(window) : window.innerWidth;
                    }

                    function getRealHeight() {
                        return origInnerH && origInnerH.get ? origInnerH.get.call(window) : window.innerHeight;
                    }

                    if (origInnerW && origInnerW.get) {
                        Object.defineProperty(window, 'innerWidth', {
                            configurable: true,
                            get: function() {
                                const realW = origInnerW.get.call(this);
                                const cfg = window.__jwallConfig;
                                if (cfg && cfg.multiMonitor && cfg.mode === 'Mirror' && cfg.primary) {
                                    return Math.max(1, Math.round(realW * cfg.primary.nw));
                                }
                                return realW;
                            }
                        });
                    }

                    if (origInnerH && origInnerH.get) {
                        Object.defineProperty(window, 'innerHeight', {
                            configurable: true,
                            get: function() {
                                const realH = origInnerH.get.call(this);
                                const cfg = window.__jwallConfig;
                                if (cfg && cfg.multiMonitor && cfg.mode === 'Mirror' && cfg.primary) {
                                    return Math.max(1, Math.round(realH * cfg.primary.nh));
                                }
                                return realH;
                            }
                        });
                    }

                    let mirrorTargets = [];

                    function findMirrorSource() {
                        if (!document.body) return null;
                        const videos = document.body.querySelectorAll('video');
                        let best = null;
                        let bestArea = 0;
                        for (const v of videos) {
                            if (v.readyState >= 2 && v.videoWidth > 0 && v.videoHeight > 0) {
                                const r = v.getBoundingClientRect();
                                const area = r.width * r.height;
                                if (area > bestArea) {
                                    best = v;
                                    bestArea = area;
                                }
                            }
                        }
                        if (best) return best;

                        const canvases = document.body.querySelectorAll('canvas');
                        for (const c of canvases) {
                            if (c.width > 0 && c.height > 0) {
                                const area = c.width * c.height;
                                if (area > bestArea) {
                                    best = c;
                                    bestArea = area;
                                }
                            }
                        }
                        return best;
                    }

                    function blitMirrors(force) {
                        const cfg = window.__jwallConfig;
                        if (!cfg || !cfg.multiMonitor || cfg.mode !== 'Mirror' || mirrorTargets.length === 0) return;

                        const src = findMirrorSource();
                        if (!src) return;

                        if (src.tagName === 'CANVAS' && window.__jwallHasWebGL) {
                            if (!window.__jwallFrameDrawn && !force) return;
                            window.__jwallFrameDrawn = false;
                        }

                        const realW = getRealWidth();
                        const realH = getRealHeight();
                        const dpr = window.devicePixelRatio || 1;

                        for (const item of mirrorTargets) {
                            const targetW = Math.max(1, Math.round(realW * item.screen.nw * dpr));
                            const targetH = Math.max(1, Math.round(realH * item.screen.nh * dpr));
                            if (item.canvas.width !== targetW || item.canvas.height !== targetH) {
                                item.canvas.width = targetW;
                                item.canvas.height = targetH;
                            }
                            try {
                                item.ctx.globalCompositeOperation = 'copy';
                                item.ctx.drawImage(src, 0, 0, targetW, targetH);
                            } catch (e) {}
                        }
                    }

                    function ensureMmStyle() {
                        if (!document.getElementById('__jw_mm_style')) {
                            const s = document.createElement('style');
                            s.id = '__jw_mm_style';
                            s.textContent = `
                                html.__jw_mirror, html.__jw_extend:not(.__jw_webgl) {
                                    background: #000 !important;
                                    overflow: hidden !important;
                                    margin: 0 !important;
                                    padding: 0 !important;
                                }
                                html.__jw_mirror body,
                                html.__jw_extend:not(.__jw_webgl) body {
                                    position: fixed !important;
                                    left: var(--jw-px, 0%) !important;
                                    top: var(--jw-py, 0%) !important;
                                    width: var(--jw-pw, 100%) !important;
                                    height: var(--jw-ph, 100%) !important;
                                    margin: 0 !important;
                                    overflow: hidden !important;
                                }
                                html.__jw_mirror .__lw_fullscreen_el,
                                html.__jw_extend:not(.__jw_webgl) .__lw_fullscreen_el {
                                    position: fixed !important;
                                    left: var(--jw-px, 0%) !important;
                                    top: var(--jw-py, 0%) !important;
                                    right: auto !important;
                                    bottom: auto !important;
                                    width: var(--jw-pw, 100%) !important;
                                    height: var(--jw-ph, 100%) !important;
                                    max-width: var(--jw-pw, 100%) !important;
                                    max-height: var(--jw-ph, 100%) !important;
                                }
                                html.__jw_extend.__jw_webgl #intro-overlay {
                                    left: var(--jw-px, 0%) !important;
                                    top: var(--jw-py, 0%) !important;
                                    width: var(--jw-pw, 100%) !important;
                                    height: var(--jw-ph, 100%) !important;
                                }
                                #__jw_mirror_root {
                                    position: fixed;
                                    inset: 0;
                                    width: 100vw;
                                    height: 100vh;
                                    pointer-events: none;
                                    z-index: 2147483646;
                                }
                            `;
                            (document.head || document.documentElement).appendChild(s);
                        }
                    }

                    function syncMirrorRoot(cfg) {
                        const existing = document.getElementById('__jw_mirror_root');
                        if (!cfg || !cfg.multiMonitor || cfg.mode !== 'Mirror') {
                            if (existing) existing.remove();
                            mirrorTargets = [];
                            return;
                        }
                        if (!document.documentElement) return;

                        const root = existing || document.createElement('div');
                        if (!existing) {
                            root.id = '__jw_mirror_root';
                            document.documentElement.appendChild(root);
                        }
                        root.innerHTML = '';
                        mirrorTargets = [];

                        const realW = getRealWidth();
                        const realH = getRealHeight();
                        const dpr = window.devicePixelRatio || 1;
                        const screens = Array.isArray(cfg.screens) ? cfg.screens : [];
                        for (const sc of screens) {
                            if (sc.primary) continue;
                            const c = document.createElement('canvas');
                            c.style.position = 'fixed';
                            c.style.left = (sc.nx * 100) + '%';
                            c.style.top = (sc.ny * 100) + '%';
                            c.style.width = (sc.nw * 100) + '%';
                            c.style.height = (sc.nh * 100) + '%';
                            c.style.pointerEvents = 'none';
                            c.width = Math.max(1, Math.round(realW * sc.nw * dpr));
                            c.height = Math.max(1, Math.round(realH * sc.nh * dpr));
                            root.appendChild(c);
                            const ctx = c.getContext('2d', { alpha: false });
                            if (ctx) {
                                mirrorTargets.push({ screen: sc, canvas: c, ctx: ctx });
                            }
                        }
                    }

                    function applyDisplayConfig(cfg) {
                        if (!cfg) return;
                        window.__jwallConfig = cfg;
                        const root = document.documentElement;
                        if (!root) return;

                        ensureMmStyle();
                        if (window.__jwallHasWebGL) {
                            root.classList.add('__jw_webgl');
                        }

                        if (cfg.primary) {
                            root.style.setProperty('--jw-px', (cfg.primary.nx * 100) + '%');
                            root.style.setProperty('--jw-py', (cfg.primary.ny * 100) + '%');
                            root.style.setProperty('--jw-pw', (cfg.primary.nw * 100) + '%');
                            root.style.setProperty('--jw-ph', (cfg.primary.nh * 100) + '%');
                        }

                        root.classList.remove('__jw_extend', '__jw_mirror', '__jw_stretch');
                        if (cfg.multiMonitor) {
                            if (cfg.mode === 'Mirror') root.classList.add('__jw_mirror');
                            else if (cfg.mode === 'Stretch') root.classList.add('__jw_stretch');
                            else root.classList.add('__jw_extend');
                        }

                        syncMirrorRoot(cfg);
                    }

                    window.__jwallUpdateDisplay = function(cfg) {
                        applyDisplayConfig(cfg);
                        window.dispatchEvent(new Event('resize'));
                        _origRAF(function() {
                            if (cfg && cfg.multiMonitor && cfg.mode === 'Mirror') {
                                blitMirrors(true);
                            }
                        });
                    };

                    // WebGL frustum & Sun flare hooks for Extend + preserveDrawingBuffer for Mirror
                    const projMatrixLocs = new WeakSet();
                    const sunPosLocs = new WeakSet();
                    const tempProjMatrix = new Float32Array(16);
                    const tempSunPos = new Float32Array(2);

                    const origGetContext = HTMLCanvasElement.prototype.getContext;
                    HTMLCanvasElement.prototype.getContext = function(type, attrs) {
                        const isWebGL = (type === 'webgl' || type === 'webgl2' || type === 'experimental-webgl');
                        let finalAttrs = attrs;
                        if (isWebGL && (!this.closest || !this.closest('#__jw_mirror_root'))) {
                            finalAttrs = Object.assign({}, attrs || {}, { preserveDrawingBuffer: true });
                        }
                        const ctx = origGetContext.call(this, type, finalAttrs);
                        if (ctx && isWebGL && (!this.closest || !this.closest('#__jw_mirror_root'))) {
                            window.__jwallHasWebGL = true;
                            if (document.documentElement) {
                                document.documentElement.classList.add('__jw_webgl');
                            }
                        }
                        return ctx;
                    };

                    function patchWebGLProto(proto) {
                        if (!proto || proto.__jwallPatched) return;
                        proto.__jwallPatched = true;

                        const origGetUniformLocation = proto.getUniformLocation;
                        proto.getUniformLocation = function(program, name) {
                            const loc = origGetUniformLocation.apply(this, arguments);
                            if (loc) {
                                if (name === 'projectionMatrix') projMatrixLocs.add(loc);
                                else if (name === 'uSunPos') sunPosLocs.add(loc);
                            }
                            return loc;
                        };

                        const origUniformMatrix4fv = proto.uniformMatrix4fv;
                        proto.uniformMatrix4fv = function(location, transpose, value) {
                            const cfg = window.__jwallConfig;
                            if (cfg && cfg.multiMonitor && cfg.mode === 'Extend' && cfg.primary && !transpose && value && value.length >= 16) {
                                const isPerspective = Math.abs(value[11] + 1.0) < 1e-4 && value[15] === 0 && value[1] === 0 && value[4] === 0;
                                if (projMatrixLocs.has(location) && isPerspective) {
                                    tempProjMatrix.set(value.length === 16 ? value : value.subarray(0, 16));
                                    const nh = cfg.primary.nh;
                                    const offX = (cfg.primary.nx + cfg.primary.nw * 0.5) * 2.0 - 1.0;
                                    const offY = 1.0 - (cfg.primary.ny + cfg.primary.nh * 0.5) * 2.0;
                                    tempProjMatrix[0] *= nh;
                                    tempProjMatrix[5] *= nh;
                                    tempProjMatrix[8] -= offX;
                                    tempProjMatrix[9] -= offY;
                                    return origUniformMatrix4fv.call(this, location, transpose, tempProjMatrix);
                                }
                            }
                            return origUniformMatrix4fv.apply(this, arguments);
                        };

                        const origUniform2f = proto.uniform2f;
                        proto.uniform2f = function(location, x, y) {
                            const cfg = window.__jwallConfig;
                            if (cfg && cfg.multiMonitor && cfg.mode === 'Extend' && cfg.primary && sunPosLocs.has(location)) {
                                const nh = cfg.primary.nh;
                                const offX = (cfg.primary.nx + cfg.primary.nw * 0.5) * 2.0 - 1.0;
                                const offY = 1.0 - (cfg.primary.ny + cfg.primary.nh * 0.5) * 2.0;
                                return origUniform2f.call(this, location, x * nh + offX, y * nh + offY);
                            }
                            return origUniform2f.apply(this, arguments);
                        };

                        const origUniform2fv = proto.uniform2fv;
                        proto.uniform2fv = function(location, value) {
                            const cfg = window.__jwallConfig;
                            if (cfg && cfg.multiMonitor && cfg.mode === 'Extend' && cfg.primary && sunPosLocs.has(location) && value && value.length >= 2) {
                                const nh = cfg.primary.nh;
                                const offX = (cfg.primary.nx + cfg.primary.nw * 0.5) * 2.0 - 1.0;
                                const offY = 1.0 - (cfg.primary.ny + cfg.primary.nh * 0.5) * 2.0;
                                tempSunPos[0] = value[0] * nh + offX;
                                tempSunPos[1] = value[1] * nh + offY;
                                return origUniform2fv.call(this, location, tempSunPos);
                            }
                            return origUniform2fv.apply(this, arguments);
                        };

                        for (const drawFn of ['drawArrays', 'drawElements', 'drawArraysInstanced', 'drawElementsInstanced', 'drawRangeElements']) {
                            if (typeof proto[drawFn] === 'function') {
                                const origDraw = proto[drawFn];
                                proto[drawFn] = function() {
                                    window.__jwallFrameDrawn = true;
                                    return origDraw.apply(this, arguments);
                                };
                            }
                        }
                    }

                    if (typeof WebGLRenderingContext !== 'undefined') patchWebGLProto(WebGLRenderingContext.prototype);
                    if (typeof WebGL2RenderingContext !== 'undefined') patchWebGLProto(WebGL2RenderingContext.prototype);

                    window.__pauseWallpaper = function() {
                        window.__isPaused = true;
                    };

                    window.__resumeWallpaper = function() {
                        if (window.__isPaused) {
                            window.__isPaused = false;
                            const pending = _rafQueue.splice(0);
                            for (const cb of pending) {
                                try { window.requestAnimationFrame(cb); } catch (e) {}
                            }
                        }
                    };

                    window.requestAnimationFrame = function(callback) {
                        if (window.__isPaused) {
                            _rafQueue.push(callback);
                            return _rafQueue.length;
                        }
                        return _origRAF(function(ts) {
                            try {
                                callback(ts);
                            } finally {
                                const cfg = window.__jwallConfig;
                                if (cfg && cfg.multiMonitor && cfg.mode === 'Mirror') {
                                    blitMirrors(false);
                                }
                            }
                        });
                    };

                    setInterval(function() {
                        const cfg = window.__jwallConfig;
                        if (!window.__isPaused && cfg && cfg.multiMonitor && cfg.mode === 'Mirror') {
                            if (document.body && document.body.querySelector('video')) {
                                blitMirrors(false);
                            }
                        }
                    }, 33);

                    applyDisplayConfig(window.__jwallConfig);
                    window.addEventListener('DOMContentLoaded', function() {
                        applyDisplayConfig(window.__jwallConfig);
                    });

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
                    PushDisplayConfigToWebView();
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

            // 2. Multi-Monitor submenu (only visible when 2+ screens are detected)
            if (Screen.AllScreens.Length > 1)
            {
                var multiMonitorMenu = new ToolStripMenuItem("Multi-Monitor");
                string currentMode = _manager.Settings.MultiMonitorMode;
                if (string.IsNullOrEmpty(currentMode)) currentMode = "Extend";

                foreach (string modeOption in new[] { "Extend", "Mirror", "Stretch" })
                {
                    var modeItem = new ToolStripMenuItem(modeOption)
                    {
                        Checked = currentMode.Equals(modeOption, StringComparison.OrdinalIgnoreCase)
                    };
                    modeItem.Click += (s, e) =>
                    {
                        _manager.Settings.MultiMonitorMode = modeOption;
                        _manager.SaveSettings();
                        WakeOnDesktopActivity();
                        PushDisplayConfigToWebView();
                        UpdateTrayMenu();
                    };
                    multiMonitorMenu.DropDownItems.Add(modeItem);
                }

                _trayMenu.Items.Add(multiMonitorMenu);
            }

            // 3. Single Options submenu containing all settings
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
            int targetX = screenX;
            int targetY = screenY;
            if (Screen.AllScreens.Length > 1 &&
                string.Equals(_manager.Settings.MultiMonitorMode, "Mirror", StringComparison.OrdinalIgnoreCase))
            {
                Screen curScreen = Screen.FromPoint(new Point(screenX, screenY));
                Screen? primary = Screen.PrimaryScreen;
                if (primary != null && !curScreen.Primary && curScreen.Bounds.Width > 0 && curScreen.Bounds.Height > 0)
                {
                    double rx = (screenX - curScreen.Bounds.X) / (double)curScreen.Bounds.Width;
                    double ry = (screenY - curScreen.Bounds.Y) / (double)curScreen.Bounds.Height;
                    targetX = primary.Bounds.X + (int)Math.Round(rx * primary.Bounds.Width);
                    targetY = primary.Bounds.Y + (int)Math.Round(ry * primary.Bounds.Height);
                }
            }

            Point clientPt = IsHandleCreated
                ? PointToClient(new Point(targetX, targetY))
                : new Point(targetX - Left, targetY - Top);
            double scale = (DeviceDpi > 0 ? DeviceDpi : 96.0) / 96.0;
            double cssX = clientPt.X / scale;
            double cssY = clientPt.Y / scale;
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

            // Display resolution or monitor change -> resize to virtual screen and re-attach
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                Program.Log("WM_DISPLAYCHANGE received. Updating bounds and re-attaching...");
                Bounds = GetVirtualScreenBounds();
                _isAttached = AttachToDesktopWorker();
                if (!_isAttached) _attachRetryTimer.Start();
                PushDisplayConfigToWebView();
                UpdateTrayMenu();
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
