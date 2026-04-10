using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Decal.Adapter.Wrappers;

namespace NexSuite.Plugins.RynthAi.Utility
{
    public class BackgroundFpsUnlocker : IDisposable
    {
        // --- THE FIX: Tell Windows to use the correct C++ memory standard ---
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8) return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            return SetWindowLong32(hWnd, nIndex, dwNewLong);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const int GWLP_WNDPROC = -4;
        private const uint WM_ACTIVATEAPP = 0x001C;

        private WndProcDelegate _newWndProc;
        private IntPtr _oldWndProc = IntPtr.Zero;
        private IntPtr _acWindowHandle;
        private PluginHost _host;
        private bool _isUnlocked = false;

        public void Start(PluginHost host)
        {
            _host = host;
            if (_isUnlocked) return;

            try
            {
                _acWindowHandle = Process.GetCurrentProcess().MainWindowHandle;

                _newWndProc = new WndProcDelegate(CustomWndProc);
                IntPtr pNewWndProc = Marshal.GetFunctionPointerForDelegate(_newWndProc);

                _oldWndProc = SetWindowLongPtr(_acWindowHandle, GWLP_WNDPROC, pNewWndProc);

                _isUnlocked = true;
                _host.Actions.AddChatText("[RynthAi] Native Background FPS Unlocked.", 5);
            }
            catch (Exception ex)
            {
                _host.Actions.AddChatText("[RynthAi] Failed to unlock FPS: " + ex.Message, 1);
            }
        }

        public void Dispose()
        {
            if (_isUnlocked && _oldWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_acWindowHandle, GWLP_WNDPROC, _oldWndProc);
                _isUnlocked = false;
            }
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // When the game is told to sleep, we intercept it and tell it to stay awake
            if (msg == WM_ACTIVATEAPP)
            {
                return CallWindowProc(_oldWndProc, hWnd, msg, (IntPtr)1, lParam);
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }
    }
}