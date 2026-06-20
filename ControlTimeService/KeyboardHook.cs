using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ControlTimeService
{
    public class KeyboardHook
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_TAB = 0x09;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_F4 = 0x73;
        private const int VK_SPACE = 0x20;

        private LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;

        public KeyboardHook() => _proc = HookCallback;

        public void Hook() => _hookID = SetHook(_proc);

        public void Unhook()
        {
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule!.ModuleName), 0);
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                if (isKeyDown || isKeyUp)
                {
                    if (ShouldBlockKey(vkCode, isKeyDown))
                        return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static bool ShouldBlockKey(int vkCode, bool isKeyDown)
        {
            bool winPressed = IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
            bool altPressed = IsKeyDown(0x12); // VK_MENU
            bool ctrlPressed = IsKeyDown(0x11); // VK_CONTROL
            bool shiftPressed = IsKeyDown(0x10); // VK_SHIFT

            // Win 键按下/抬起均拦截，防止开始菜单与快捷键
            if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                return true;

            // Win 组合键
            if (winPressed)
                return true;

            if (isKeyDown)
            {
                if (vkCode == VK_TAB && altPressed)
                    return true;

                if (vkCode == VK_F4 && altPressed)
                    return true;

                if (vkCode == VK_ESCAPE && (altPressed || (ctrlPressed && shiftPressed)))
                    return true;

                if (vkCode == VK_SPACE && altPressed)
                    return true;

                if (vkCode == VK_ESCAPE && ctrlPressed && !shiftPressed)
                    return true;
            }

            return false;
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
