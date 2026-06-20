using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ControlTimeService
{
    internal static class LockScreenHelper
    {
        private const int SW_HIDE = 0;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;
        private const byte VK_ESCAPE = 0x1B;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;

        public static void DismissStartMenuAndOverlays()
        {
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            EnumWindows(CloseOverlayWindow, IntPtr.Zero);
        }

        public static void ForceLockForeground(Window window)
        {
            if (window == null)
                return;

            var helper = new WindowInteropHelper(window);
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var foreground = GetForegroundWindow();
            if (foreground != hwnd && foreground != IntPtr.Zero)
            {
                uint fgThread = GetWindowThreadProcessId(foreground, out _);
                uint curThread = GetCurrentThreadId();

                if (fgThread != 0 && fgThread != curThread)
                {
                    AttachThreadInput(fgThread, curThread, true);
                    SetForegroundWindow(hwnd);
                    AttachThreadInput(fgThread, curThread, false);
                }
                else
                {
                    SetForegroundWindow(hwnd);
                }
            }
            else
            {
                SetForegroundWindow(hwnd);
            }

            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            window.Topmost = true;
            window.Activate();
            window.Focus();
        }

        private static bool CloseOverlayWindow(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd))
                return true;

            var className = GetClassNameSafe(hWnd);
            if (string.IsNullOrEmpty(className))
                return true;

            if (IsStartMenuOrSearchWindow(className))
            {
                ShowWindow(hWnd, SW_HIDE);
                PostMessage(hWnd, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
            }

            return true;
        }

        private static bool IsStartMenuOrSearchWindow(string className)
        {
            if (className.Contains("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
                return false;

            return className.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("Start", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetClassNameSafe(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    }
}
