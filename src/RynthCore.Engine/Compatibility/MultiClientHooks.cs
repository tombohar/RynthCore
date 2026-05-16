using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class MultiClientHooks
{
    private const int ExpectedImageSize = 0x56D000;
    private const int IsAlreadyRunningFallbackVa = 0x004122A0;
    private const int OpenDataFileFallbackVa = 0x00675920;
    // OpenDataFile's prologue tests bit 1 (value 0x2) of openFlags:
    //   and cl, 2
    //   cmp cl, 2
    // That's the bit AC's code dispatches on for "shared access" — the prior
    // value 0x4 was a misread of the byte sequence. Decal's coexisting clients
    // open with bit 1 set; matching that lets us share with their handles.
    private const uint OpenDataFileSharedAccessFlag = 0x2;

    private static readonly byte?[] IsAlreadyRunningSignature =
    [
        0x56, 0x68, 0x30, 0x58, 0x79, 0x00, 0x6A, 0x01,
        0x6A, 0x00, 0x6A, 0x00, 0x8B, 0xF1, 0xFF, 0x15
    ];

    private static readonly byte?[] OpenDataFileSignature =
    [
        0x8B, 0x44, 0x24, 0x10, 0x53, 0x55, 0x8B, 0x6C,
        0x24, 0x0C, 0x56, 0x57, 0x8B, 0xF1, 0x50, 0x8B,
        0xC8, 0x8B, 0x44, 0x24, 0x20, 0x6A, 0x00, 0x80,
        0xE1, 0x02, 0x80, 0xF9, 0x02, 0x50, 0x6A, 0x00
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte IsAlreadyRunningDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint OpenDataFileDelegate(
        IntPtr allocator,
        IntPtr fileInfo,
        IntPtr fileName,
        IntPtr pathToUse,
        uint openFlags,
        IntPtr transactionInfo);

    private static IsAlreadyRunningDelegate? _detour;
    private static IsAlreadyRunningDelegate? _originalIsAlreadyRunning;
    private static OpenDataFileDelegate? _openDataFileDetour;
    private static OpenDataFileDelegate? _originalOpenDataFile;
    private static IntPtr _targetAddress;
    private static IntPtr _openDataFileAddress;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsEnabled { get; private set; }
    public static bool IsAlreadyRunningInstalled { get; private set; }
    public static bool OpenDataFileInstalled { get; private set; }
    public static bool IsInstalled => IsAlreadyRunningInstalled && OpenDataFileInstalled;
    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        if (IsAlreadyRunningInstalled && OpenDataFileInstalled)
            return;

        bool allowMultipleClients = LauncherSettings.AllowMultipleClientsEnabled;
        IsEnabled = allowMultipleClients;
        _statusMessage = LauncherSettings.StatusMessage;

        if (!allowMultipleClients)
        {
            RynthLog.Verbose($"Compat: multi-client bypass skipped - {_statusMessage}");
            return;
        }

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            RynthLog.Compat($"Compat: multi-client hook failed - {_statusMessage}");
            return;
        }

        try
        {
            if (textSection.ImageSize != ExpectedImageSize)
                RynthLog.Verbose($"Compat: multi-client hook using unverified acclient image size 0x{textSection.ImageSize:X} (expected 0x{ExpectedImageSize:X}).");

            TryInstallIsAlreadyRunningHook(textSection);
            TryInstallOpenDataFileHook(textSection);

            if (!IsAlreadyRunningInstalled && !OpenDataFileInstalled)
            {
                _statusMessage = "No multi-client compatibility hooks installed.";
                RynthLog.Compat($"Compat: multi-client hook failed - {_statusMessage}");
                return;
            }

            if (IsInstalled)
                _statusMessage = $"Hooked Client::IsAlreadyRunning @ 0x{_targetAddress.ToInt32():X8} and CLBlockAllocator::OpenDataFile @ 0x{_openDataFileAddress.ToInt32():X8} (share flag 0x2).";
            else
                _statusMessage = $"Partial install. alreadyRunning={IsAlreadyRunningInstalled}, dataFile={OpenDataFileInstalled}.";
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: multi-client hook failed - {ex.Message}");
        }
    }

    private static void TryInstallIsAlreadyRunningHook(AcClientTextSection textSection)
    {
        var resolved = HookResolver.Resolve(textSection, "MultiClientHooks.Client::IsAlreadyRunning",
            IsAlreadyRunningSignature, IsAlreadyRunningFallbackVa);
        if (!resolved.Success)
            return;

        try
        {
            _targetAddress = resolved.Address;
            _detour = IsAlreadyRunningDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_detour);
            _originalIsAlreadyRunning = Marshal.GetDelegateForFunctionPointer<IsAlreadyRunningDelegate>(
                MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);
            IsAlreadyRunningInstalled = true;
            RynthLog.Compat($"MultiClientHooks: IsAlreadyRunning hook ready @ 0x{_targetAddress.ToInt32():X8}.");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"MultiClientHooks: IsAlreadyRunning install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryInstallOpenDataFileHook(AcClientTextSection textSection)
    {
        var resolved = HookResolver.Resolve(textSection, "MultiClientHooks.CLBlockAllocator::OpenDataFile",
            OpenDataFileSignature, OpenDataFileFallbackVa);
        if (!resolved.Success)
            return;

        try
        {
            _openDataFileAddress = resolved.Address;
            _openDataFileDetour = OpenDataFileDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_openDataFileDetour);
            _originalOpenDataFile = Marshal.GetDelegateForFunctionPointer<OpenDataFileDelegate>(
                MinHook.HookCreate(_openDataFileAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_openDataFileAddress);
            OpenDataFileInstalled = true;
            RynthLog.Compat($"MultiClientHooks: OpenDataFile hook ready @ 0x{_openDataFileAddress.ToInt32():X8}.");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"MultiClientHooks: OpenDataFile install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static byte IsAlreadyRunningDetour(IntPtr thisPtr)
    {
        // Skip the original entirely: it has a side-effect MessageBox path
        // that surfaces the "client is already running on this machine" dialog
        // even when we'd later override its return value, so calling it makes
        // things worse. Returning 0 unconditionally was the original design.
        return 0;
    }

    private static uint OpenDataFileDetour(
        IntPtr allocator,
        IntPtr fileInfo,
        IntPtr fileName,
        IntPtr pathToUse,
        uint openFlags,
        IntPtr transactionInfo)
    {
        return _originalOpenDataFile!(
            allocator,
            fileInfo,
            fileName,
            pathToUse,
            openFlags | OpenDataFileSharedAccessFlag,
            transactionInfo);
    }
}
