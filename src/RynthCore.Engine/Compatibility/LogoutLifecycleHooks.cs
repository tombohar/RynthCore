using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Hooks the moments when the AC client commits to ending the in-world session.
///
///   - <c>CPlayerSystem::ExecuteLogOff(void)</c> — fires when the player has confirmed
///     the logoff prompt and the actual logoff begins.
///   - <c>gmGamePlayUI::RecvNotice_Logoff(void)</c> — server-driven full logoff notice.
///
/// Either one raises <see cref="LogoutComplete"/> once per logout cycle.
///
/// Both addresses are now resolved via HookResolver — pattern-scan first,
/// fallback VA second, UNAVAILABLE if neither works.
/// </summary>
internal static class LogoutLifecycleHooks
{
    // Fallback VAs (4,841,472-byte client). Pattern-scan is the source of truth.
    private const int ExecuteLogOffFallbackVa    = 0x0055E4A0;
    private const int RecvNoticeLogoffFallbackVa = 0x004ECBA0;

    // CPlayerSystem::ExecuteLogOff(void)
    // Two consecutive 'mov byte ptr [esi+0x213],bl' / 'mov byte ptr [esi+0x221],bl'
    // — those CPlayerSystem flags clears are unique anchor.
    private static readonly byte?[] ExecuteLogOffPattern =
    [
        0x53, 0x56, 0x8B, 0xF1, 0x33, 0xDB, 0x88, 0x9E,
        0x13, 0x02, 0x00, 0x00, 0x88, 0x9E, 0x21, 0x02,
        0x00, 0x00,
        0x8B, 0x0D, null, null, null, null,   // mov ecx, ds:[imm32]
        0xA1, null, null, null, null,         // mov eax, ds:[imm32]
        0x89, 0x86, 0x00, 0x02, 0x00, 0x00
    ];

    // gmGamePlayUI::RecvNotice_Logoff(void)
    // 0x90 stack frame, sets two booleans at +0x2D / +0x2E, then E8 to a helper.
    private static readonly byte?[] RecvNoticeLogoffPattern =
    [
        0x81, 0xEC, 0x90, 0x00, 0x00, 0x00, 0x56, 0x8B,
        0xF1, 0xB0, 0x01, 0x8D, 0x4C, 0x24, 0x04, 0x88,
        0x46, 0x2D, 0x88, 0x46, 0x2E,
        0xE8, null, null, null, null,         // call rel32
        0xA1, null, null, null, null,         // mov eax, ds:[imm32]
        0x68, 0x01, 0x00, 0x00, 0x10
    ];

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

        bool anyHooked = false;
        anyHooked |= TryInstall(textSection, "LogoutLifecycle.ExecuteLogOff",
            ExecuteLogOffPattern, ExecuteLogOffFallbackVa, ExecuteLogOffDetour,
            out _executeLogOffAddress, out _executeLogOffDetour, out _originalExecuteLogOff);
        anyHooked |= TryInstall(textSection, "LogoutLifecycle.RecvNotice_Logoff",
            RecvNoticeLogoffPattern, RecvNoticeLogoffFallbackVa, RecvNoticeLogoffDetour,
            out _logoffAddress, out _recvNoticeLogoffDetour, out _originalRecvNoticeLogoff);

        if (anyHooked)
        {
            IsInstalled = true;
            RynthLog.Compat($"LogoutLifecycleHooks: ready (ExecuteLogOff=0x{_executeLogOffAddress.ToInt32():X8}, RecvNotice_Logoff=0x{_logoffAddress.ToInt32():X8}).");
        }
    }

    private static bool TryInstall(
        AcClientTextSection textSection, string name,
        byte?[] pattern, int fallbackVa, ThisCallVoidDelegate detour,
        out IntPtr address, out ThisCallVoidDelegate detourField, out ThisCallVoidDelegate? original)
    {
        address = IntPtr.Zero;
        detourField = detour;
        original = null;

        var resolved = HookResolver.Resolve(textSection, name, pattern, fallbackVa);
        if (!resolved.Success)
            return false;

        try
        {
            address = resolved.Address;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(detourField);
            original = Marshal.GetDelegateForFunctionPointer<ThisCallVoidDelegate>(
                MinHook.HookCreate(address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(address);
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"LogoutLifecycleHooks: {name} install threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static void ResetObservation()
    {
        HasObservedLogout = false;
    }

    private static void ExecuteLogOffDetour(IntPtr thisPtr)
    {
        RecursionGuard.Tick("LogoutLifecycleHooks.ExecuteLogOff");
        if (!HasObservedLogout)
            RynthLog.Compat("LogoutLifecycleHooks: ExecuteLogOff detour entered.");

        try { _originalExecuteLogOff!(thisPtr); }
        catch (Exception ex) { try { RynthLog.Compat($"LogoutLifecycleHooks: ExecuteLogOff original threw {ex.GetType().Name}: {ex.Message}"); } catch { } }

        RaiseLogoutCompleteOnce("CPlayerSystem::ExecuteLogOff");
    }

    private static void RecvNoticeLogoffDetour(IntPtr thisPtr)
    {
        RecursionGuard.Tick("LogoutLifecycleHooks.RecvNoticeLogoff");
        if (!HasObservedLogout)
            RynthLog.Compat("LogoutLifecycleHooks: RecvNotice_Logoff detour entered.");

        try { _originalRecvNoticeLogoff!(thisPtr); }
        catch (Exception ex) { try { RynthLog.Compat($"LogoutLifecycleHooks: RecvNotice_Logoff original threw {ex.GetType().Name}: {ex.Message}"); } catch { } }

        RaiseLogoutCompleteOnce("gmGamePlayUI::RecvNotice_Logoff");
    }

    private static void RaiseLogoutCompleteOnce(string source)
    {
        if (HasObservedLogout)
            return;

        HasObservedLogout = true;
        _statusMessage = $"Logout observed via {source}.";
        RynthLog.Compat($"LogoutLifecycleHooks: logout observed via {source} — raising LogoutComplete.");

        try { LogoutComplete?.Invoke(); }
        catch (Exception ex)
        {
            RynthLog.Compat($"LogoutLifecycleHooks: handler threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
