using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Hooks the moments when the AC client commits to ending the in-world session, so the
/// engine can close its UI gate before AC starts rebuilding its UIElement tree —
/// touching world data while AC is mid-teardown crashes the client.
///
/// We hook both:
///   - <c>CPlayerSystem::ExecuteLogOff(void)</c> — fires when the player has confirmed
///     the logoff prompt and the actual logoff begins. This is the earliest reliable
///     "we're committed to leaving the world" signal. (NOT
///     <c>gmGamePlayUI::RecvNotice_EndCharacterSession</c> — that fires when the user
///     clicks the Exit-to-Character-Selection menu item, BEFORE the confirmation
///     prompt; if we close the UI there and the user clicks "No" on the prompt, the
///     overlay is hidden while gameplay continues.)
///   - <c>gmGamePlayUI::RecvNotice_Logoff(void)</c> — server-driven full logoff
///     notice. Belt-and-braces in case ExecuteLogOff is bypassed in some flow.
///
/// Either one raises <see cref="LogoutComplete"/> once per logout cycle.
///
/// Public symbol addresses taken from Chorizite's acclient.map:
///   - CPlayerSystem::ExecuteLogOff:    file offset 0x0015D4A0, VA 0x0055E4A0
///   - RecvNotice_Logoff:               file offset 0x000EBBA0, VA 0x004ECBA0
/// (Standard 0x56D000 acclient.exe build.)
/// </summary>
internal static class LogoutLifecycleHooks
{
    private const int ExpectedImageSize = 0x56D000;
    private const int ExecuteLogOffFileOffset    = 0x0015D4A0;
    private const int RecvNoticeLogoffFileOffset = 0x000EBBA0;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ThisCallVoidDelegate(IntPtr thisPtr);

    private static ThisCallVoidDelegate? _originalExecuteLogOff;
    private static ThisCallVoidDelegate? _executeLogOffDetour;
    private static IntPtr _executeLogOffAddress;

    private static ThisCallVoidDelegate? _originalRecvNoticeLogoff;
    private static ThisCallVoidDelegate? _recvNoticeLogoffDetour;
    private static IntPtr _logoffAddress;

    private static string _statusMessage = "Not probed yet.";

    public static bool IsInstalled { get; private set; }
    public static bool HasObservedLogout { get; private set; }
    public static string StatusMessage => _statusMessage;

    /// <summary>Fires once per logout cycle, after the first observed logout signal returns from its original.</summary>
    public static event Action? LogoutComplete;

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int imageSize = textSection.ImageSize;
        if (imageSize != ExpectedImageSize)
            RynthLog.Compat($"Compat: logout lifecycle hook using unverified acclient image size 0x{imageSize:X} (expected 0x{ExpectedImageSize:X}).");

        bool anyHooked = false;
        anyHooked |= TryInstallHook(textSection, ExecuteLogOffFileOffset, ExecuteLogOffDetour,
            out _executeLogOffAddress, out _executeLogOffDetour, out _originalExecuteLogOff,
            "CPlayerSystem::ExecuteLogOff");
        anyHooked |= TryInstallHook(textSection, RecvNoticeLogoffFileOffset, RecvNoticeLogoffDetour,
            out _logoffAddress, out _recvNoticeLogoffDetour, out _originalRecvNoticeLogoff,
            "gmGamePlayUI::RecvNotice_Logoff");

        if (anyHooked)
        {
            IsInstalled = true;
            RynthLog.Compat($"Compat: logout lifecycle hook ready - ExecuteLogOff=0x{_executeLogOffAddress.ToInt32():X8} Logoff=0x{_logoffAddress.ToInt32():X8}");
        }
    }

    private static bool TryInstallHook(
        AcClientTextSection textSection, int fileOffset, ThisCallVoidDelegate detour,
        out IntPtr address, out ThisCallVoidDelegate detourField, out ThisCallVoidDelegate? original,
        string name)
    {
        try
        {
            address = new IntPtr(textSection.TextBaseVa + fileOffset);
            detourField = detour;

            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(detourField);
            original = Marshal.GetDelegateForFunctionPointer<ThisCallVoidDelegate>(MinHook.HookCreate(address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(address);

            _statusMessage = $"Hooked {name} @ 0x{address.ToInt32():X8}.";
            return true;
        }
        catch (Exception ex)
        {
            address = IntPtr.Zero;
            detourField = detour;
            original = null;
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: {name} hook failed - {ex.Message}");
            return false;
        }
    }

    /// <summary>Resets the observation flag so the next logout-class notice fires <see cref="LogoutComplete"/> again.</summary>
    public static void ResetObservation()
    {
        HasObservedLogout = false;
    }

    private static void ExecuteLogOffDetour(IntPtr thisPtr)
    {
        if (!HasObservedLogout)
            RynthLog.Compat("Compat: CPlayerSystem::ExecuteLogOff detour entered.");

        try { _originalExecuteLogOff!(thisPtr); }
        catch { /* original must run */ }

        RaiseLogoutCompleteOnce("CPlayerSystem::ExecuteLogOff");
    }

    private static void RecvNoticeLogoffDetour(IntPtr thisPtr)
    {
        if (!HasObservedLogout)
            RynthLog.Compat("Compat: RecvNotice_Logoff detour entered.");

        try { _originalRecvNoticeLogoff!(thisPtr); }
        catch { /* original must run */ }

        RaiseLogoutCompleteOnce("gmGamePlayUI::RecvNotice_Logoff");
    }

    /// <summary>
    /// Either notice may fire first (a session may receive both — ExecuteLogOff at
    /// commit, RecvNotice_Logoff later from the server). Only the first one in a
    /// logout cycle should drive the dispatch — subsequent ones are no-ops until
    /// <see cref="ResetObservation"/> runs after the next login.
    /// </summary>
    private static void RaiseLogoutCompleteOnce(string source)
    {
        if (HasObservedLogout)
            return;

        HasObservedLogout = true;
        _statusMessage = $"Logout observed via {source}.";
        RynthLog.Compat($"Compat: logout observed via {source} — raising LogoutComplete.");

        // Subscribers MUST keep their handler short — this fires on AC's UI thread.
        // Long work (plugin teardown, disposing world data, etc.) must be deferred to
        // the EndScene queue.
        try { LogoutComplete?.Invoke(); }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: LogoutComplete handler threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
