using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class LoginLifecycleHooks
{
    private const int ExpectedImageSize = 0x56D000;
    private static readonly byte?[] SendLoginCompletePattern =
    [
        0x6A, 0x01, 0xC6, 0x86, null, null, null, null,
        0x01, 0xE8, null, null, null, null, 0x8B, 0x0D,
        null, null, null, null, 0x83, 0xC4, 0x04, 0x6A,
        0x00, 0x6A, 0x0B, 0xE8
    ];

    private static readonly byte[] SendLoginCompletePrologue = [0x56, 0x8B, 0xF1];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SendLoginCompleteNotificationDelegate(IntPtr thisPtr);

    private static SendLoginCompleteNotificationDelegate? _originalSendLoginCompleteNotification;
    private static SendLoginCompleteNotificationDelegate? _sendLoginCompleteNotificationDetour;
    private static IntPtr _targetAddress;
    private static string _statusMessage = "Not probed yet.";
    private static long _nextStatusLogTick;
    private static bool _waitingLogged;

    public static bool IsInstalled { get; private set; }
    public static bool HasObservedLoginComplete { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static event Action? LoginComplete;

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        try
        {
            int imageSize = textSection.ImageSize;
            if (imageSize != ExpectedImageSize)
                RynthLog.Verbose($"Compat: login lifecycle hook using unverified acclient image size 0x{imageSize:X} (expected 0x{ExpectedImageSize:X}).");

            int callSiteOff = PatternScanner.FindPattern(textSection.Bytes, SendLoginCompletePattern);
            if (callSiteOff < 0)
            {
                _statusMessage = "SendLoginCompleteNotification call site pattern not found.";
                RynthLog.Compat($"Compat: login lifecycle hook failed - {_statusMessage}");
                return;
            }

            int funcOff = PatternScanner.FindPrologueBefore(
                textSection.Bytes,
                callSiteOff,
                SendLoginCompletePrologue,
                maxDistance: 0x100);

            if (funcOff < 0)
            {
                _statusMessage = "SendLoginCompleteNotification prologue not found.";
                RynthLog.Compat($"Compat: login lifecycle hook failed - {_statusMessage}");
                return;
            }

            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _sendLoginCompleteNotificationDetour = SendLoginCompleteNotificationDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_sendLoginCompleteNotificationDetour);
            _originalSendLoginCompleteNotification = Marshal.GetDelegateForFunctionPointer<SendLoginCompleteNotificationDelegate>(MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked SendLoginCompleteNotification @ 0x{_targetAddress.ToInt32():X8}.";
            _nextStatusLogTick = Environment.TickCount64 + 3000;
            RynthLog.Verbose(
                $"Compat: login lifecycle hook ready - SendLoginCompleteNotification=0x{_targetAddress.ToInt32():X8}, callSite=0x{textSection.TextBaseVa + callSiteOff:X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: login lifecycle hook failed - {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the one-shot observation flag so the next <c>SendLoginCompleteNotification</c>
    /// raises <see cref="LoginComplete"/> again. Called by the logout pipeline so the
    /// following login (e.g. swapping characters) re-runs login-gated startup.
    /// </summary>
    public static void ResetObservation()
    {
        HasObservedLoginComplete = false;
        _statusMessage = "Awaiting next SendLoginCompleteNotification.";
        _waitingLogged = false;
    }

    public static void Poll()
    {
        if (HasObservedLoginComplete || !IsInstalled)
            return;

        long now = Environment.TickCount64;
        if (now < _nextStatusLogTick)
            return;

        _nextStatusLogTick = now + 3000;
        if (!_waitingLogged)
        {
            _waitingLogged = true;
            RynthLog.Verbose("Compat: login lifecycle awaiting SendLoginCompleteNotification.");
        }
    }

    private static void SendLoginCompleteNotificationDetour(IntPtr thisPtr)
    {
        _originalSendLoginCompleteNotification!(thisPtr);
        SignalLoginComplete("CPlayerSystem::SendLoginCompleteNotification");
    }

    private static void SignalLoginComplete(string source)
    {
        if (HasObservedLoginComplete)
            return;

        HasObservedLoginComplete = true;
        _statusMessage = $"Login complete observed via {source}.";
        RynthLog.Verbose($"Compat: login complete observed via {source}.");
        LoginComplete?.Invoke();
    }
}
