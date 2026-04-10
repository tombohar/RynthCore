using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RynthCore.App;

internal sealed class AcClientLoginAutomationService
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const nuint MkLButton = 0x0001;
    private const int LogoBypassX = 350;
    private const int LogoBypassY = 100;
    private const int ClickAttempts = 4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    public async Task TryBypassLoginLogosAsync(int processId, Action<string>? log = null)
    {
        try
        {
            nint hwnd = await WaitForMainWindowAsync(processId).ConfigureAwait(false);
            if (hwnd == nint.Zero)
            {
                log?.Invoke($"Skip Login Logos: no main window found for PID {processId}.");
                return;
            }

            await Task.Delay(2500).ConfigureAwait(false);
            for (int i = 0; i < ClickAttempts; i++)
            {
                if (!TrySendClick(hwnd, LogoBypassX, LogoBypassY))
                {
                    log?.Invoke($"Skip Login Logos: click {i + 1}/{ClickAttempts} failed for PID {processId}.");
                    return;
                }

                log?.Invoke($"Skip Login Logos: sent click {i + 1}/{ClickAttempts} to PID {processId}.");
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Skip Login Logos failed for PID {processId}: {ex.Message}");
        }
    }

    private static async Task<nint> WaitForMainWindowAsync(int processId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return nint.Zero;

                process.Refresh();
                if (process.MainWindowHandle != nint.Zero)
                    return process.MainWindowHandle;
            }
            catch
            {
                return nint.Zero;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        return nint.Zero;
    }

    private static bool TrySendClick(nint hwnd, int x, int y)
    {
        if (hwnd == nint.Zero)
            return false;

        nint lParam = (nint)((y << 16) | (x & 0xFFFF));
        nint move = PostMessageW(hwnd, WmMouseMove, 0, lParam);
        nint down = PostMessageW(hwnd, WmLButtonDown, MkLButton, lParam);
        nint up = PostMessageW(hwnd, WmLButtonUp, 0, lParam);
        return move != 0 && down != 0 && up != 0;
    }
}
