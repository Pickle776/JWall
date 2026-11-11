using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace JWall
{
    public class MouseTracker : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const uint GA_ROOT = 2;

        private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelProc? _mouseProc;
        private LowLevelProc? _kbProc;
        private IntPtr _mouseHookId = IntPtr.Zero;
        private IntPtr _kbHookId = IntPtr.Zero;

        private DateTime _lastClickTime = DateTime.MinValue;
        private Point _lastClickPos;
        private int _clickCount = 0;
        private bool _isLeftDown = false;
        private DateTime _lastMoveTime = DateTime.MinValue;

        public bool Enabled { get; set; } = true;
        public bool CaptureKeyboardForInput { get; set; } = false;

        public event Action<int, int, int>? DesktopMouseDown; // screenX, screenY, clickCount
        public event Action<int, int, int>? DesktopMouseUp;   // screenX, screenY, clickCount
        public event Action<int, int, bool>? DesktopMouseMove; // screenX, screenY, isLeftDown
        public event Action<int, int, int>? DesktopMouseWheel; // screenX, screenY, delta
        public event Action? DesktopBlurred;
        public event Action<int, bool, bool>? DesktopKeyDown; // vkCode, ctrl, shift
        public event Action<string>? DesktopTextInput;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags);

        public void Start()
        {
            if (_mouseHookId != IntPtr.Zero) return;
            _mouseProc = MouseHookCallback;
            _kbProc = KeyboardHookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr hMod = GetModuleHandle(curModule?.ModuleName);
            _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
            _kbHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
        }

        public void Stop()
        {
            if (_mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
            if (_kbHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_kbHookId);
                _kbHookId = IntPtr.Zero;
            }
        }

        private bool IsCursorOverDesktop(POINT pt)
        {
            IntPtr hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;

            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;

            var sb = new StringBuilder(256);
            GetClassNameW(root, sb, 256);
            string rootCls = sb.ToString();

            if (rootCls == "Progman" || rootCls == "WorkerW")
            {
                return true;
            }

            GetClassNameW(hwnd, sb, 256);
            string cls = sb.ToString();
            return cls == "SHELLDLL_DefView" || cls == "SysListView32" || cls == "DirectUIHWND";
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Enabled)
            {
                int msg = wParam.ToInt32();
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                Point curPt = new Point(hookStruct.pt.x, hookStruct.pt.y);

                if (msg == WM_LBUTTONDOWN)
                {
                    if (IsCursorOverDesktop(hookStruct.pt))
                    {
                        _isLeftDown = true;
                        var now = DateTime.UtcNow;
                        double elapsed = (now - _lastClickTime).TotalMilliseconds;
                        int distSq = (curPt.X - _lastClickPos.X) * (curPt.X - _lastClickPos.X) +
                                     (curPt.Y - _lastClickPos.Y) * (curPt.Y - _lastClickPos.Y);

                        if (elapsed <= GetDoubleClickTime() && distSq <= 400)
                        {
                            _clickCount = (_clickCount % 2) + 1;
                        }
                        else
                        {
                            _clickCount = 1;
                        }

                        _lastClickTime = now;
                        _lastClickPos = curPt;

                        try { DesktopMouseDown?.Invoke(curPt.X, curPt.Y, _clickCount); } catch { }
                    }
                    else
                    {
                        _isLeftDown = false;
                        CaptureKeyboardForInput = false;
                        try { DesktopBlurred?.Invoke(); } catch { }
                    }
                }
                else if (msg == WM_LBUTTONUP)
                {
                    bool wasDown = _isLeftDown;
                    _isLeftDown = false;
                    if (wasDown || IsCursorOverDesktop(hookStruct.pt))
                    {
                        int count = _clickCount > 0 ? _clickCount : 1;
                        try { DesktopMouseUp?.Invoke(curPt.X, curPt.Y, count); } catch { }
                    }
                }
                else if (msg == WM_MOUSEWHEEL)
                {
                    if (IsCursorOverDesktop(hookStruct.pt))
                    {
                        short delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                        try { DesktopMouseWheel?.Invoke(curPt.X, curPt.Y, delta); } catch { }
                    }
                }
                else if (msg == WM_MOUSEMOVE)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastMoveTime).TotalMilliseconds >= 14) // ~70fps throttle
                    {
                        if (_isLeftDown || IsCursorOverDesktop(hookStruct.pt))
                        {
                            _lastMoveTime = now;
                            try { DesktopMouseMove?.Invoke(curPt.X, curPt.Y, _isLeftDown); } catch { }
                        }
                    }
                }
            }

            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Enabled && CaptureKeyboardForInput && WallpaperForm.IsDesktopForeground())
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)kb.vkCode;
                    bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
                    bool shift = (GetKeyState(0x10) & 0x8000) != 0;
                    bool alt = (GetKeyState(0x12) & 0x8000) != 0;

                    // Allow system shortcuts like Alt+Tab or Win key to pass through
                    if (alt || vk == 0x5B || vk == 0x5C)
                    {
                        return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
                    }

                    // Special navigation/editing keys or Ctrl shortcuts
                    if (ctrl || vk == 0x08 || vk == 0x09 || vk == 0x0D || vk == 0x1B ||
                        (vk >= 0x21 && vk <= 0x2E))
                    {
                        try { DesktopKeyDown?.Invoke(vk, ctrl, shift); } catch { }
                        return (IntPtr)1;
                    }

                    // Translate printable character
                    byte[] keyState = new byte[256];
                    if (shift) keyState[0x10] = 0x80;
                    if ((GetKeyState(0x14) & 0x0001) != 0) keyState[0x14] = 0x01; // CapsLock

                    var sb = new StringBuilder(8);
                    int rc = ToUnicode(kb.vkCode, kb.scanCode, keyState, sb, sb.Capacity, 0);
                    if (rc > 0)
                    {
                        string text = sb.ToString(0, rc);
                        if (!char.IsControl(text[0]))
                        {
                            try { DesktopTextInput?.Invoke(text); } catch { }
                            return (IntPtr)1;
                        }
                    }
                }
                else if (msg == WM_KEYUP)
                {
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
