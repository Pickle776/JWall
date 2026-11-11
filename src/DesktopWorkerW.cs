using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace JWall
{
    public static class DesktopWorkerW
    {
        private const int SMTO_NORMAL = 0x0000;
        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowExW(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hwnd, EnumWindowsProc func, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static IntPtr FindDesktopWorkerW()
        {
            IntPtr progman = FindWindowW("Progman", null);
            if (progman == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // 1. Search for the window hosting SHELLDLL_DefView (the actual desktop icons)
            IntPtr shellView = IntPtr.Zero;
            IntPtr shellParent = IntPtr.Zero;

            EnumWindows((tophandle, _) =>
            {
                IntPtr sv = FindWindowExW(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (sv != IntPtr.Zero)
                {
                    shellView = sv;
                    shellParent = tophandle;
                    return false; // Stop enumeration once found
                }
                return true;
            }, IntPtr.Zero);

            // Also check directly under Progman if not found at top level
            if (shellView == IntPtr.Zero)
            {
                shellView = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                {
                    shellParent = progman;
                }
            }

            // If SHELLDLL_DefView does not exist yet, Explorer is still starting up!
            // Never guess or attach to arbitrary WorkerW windows.
            if (shellView == IntPtr.Zero || shellParent == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // 2. Tell Explorer to spawn the WorkerW behind desktop icons
            SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);
            SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);

            // 3. Locate WorkerW:
            // Case A: On Windows 11 where SHELLDLL_DefView is inside Progman, WorkerW is a child inside Progman!
            if (shellParent == progman)
            {
                IntPtr childWorker = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
                if (childWorker != IntPtr.Zero)
                {
                    return childWorker;
                }
            }

            // Case B: SHELLDLL_DefView is in a top-level WorkerW window.
            // The wallpaper WorkerW is the top-level window directly behind shellParent in Z-order.
            IntPtr workerw = FindWindowExW(IntPtr.Zero, shellParent, "WorkerW", null);
            if (workerw != IntPtr.Zero && (shellParent != progman || IsWindowVisible(workerw)))
            {
                return workerw;
            }

            // Fallback: check child of Progman
            workerw = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
            if (workerw != IntPtr.Zero)
            {
                return workerw;
            }

            return IntPtr.Zero;
        }

        public static bool AttachToDesktop(IntPtr formHandle, Rectangle bounds, out IntPtr attachedWorkerW)
        {
            attachedWorkerW = IntPtr.Zero;
            IntPtr workerw = FindDesktopWorkerW();
            Program.Log($"FindDesktopWorkerW returned: {workerw}");
            if (workerw == IntPtr.Zero)
            {
                return false;
            }

            // Remove title, borders, caption, system menu and mark as child
            int style = GetWindowLong(formHandle, GWL_STYLE);
            style &= ~WS_CAPTION;
            style &= ~WS_THICKFRAME;
            style |= WS_CHILD;
            SetWindowLong(formHandle, GWL_STYLE, style);

            // Attach behind desktop icons
            IntPtr prevParent = SetParent(formHandle, workerw);
            Program.Log($"SetParent result: {prevParent}, LastError: {Marshal.GetLastWin32Error()}");

            // Resize to cover screen
            bool posResult = SetWindowPos(formHandle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Program.Log($"SetWindowPos result: {posResult}");

            attachedWorkerW = workerw;
            return true;
        }
    }
}
