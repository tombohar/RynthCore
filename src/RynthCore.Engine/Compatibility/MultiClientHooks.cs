using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class MultiClientHooks
{
    private const int ExpectedImageSize = 0x56D000;
    private const int IsAlreadyRunningVa = 0x004122A0;
    private const int OpenDataFileVa = 0x00675920;
    private const uint OpenDataFileSharedAccessFlag = 0x4;

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

            int funcOff = IsAlreadyRunningVa - textSection.TextBaseVa;
            if (funcOff < 0 || funcOff + IsAlreadyRunningSignature.Length > textSection.Bytes.Length)
            {
                _statusMessage = $"Client::IsAlreadyRunning VA 0x{IsAlreadyRunningVa:X8} is outside the readable text window.";
                RynthLog.Compat($"Compat: multi-client hook failed - {_statusMessage}");
                return;
            }

            for (int i = 0; i < IsAlreadyRunningSignature.Length; i++)
            {
                byte? expected = IsAlreadyRunningSignature[i];
                if (expected.HasValue && textSection.Bytes[funcOff + i] != expected.Value)
                {
                    _statusMessage = $"Client::IsAlreadyRunning signature mismatch at 0x{IsAlreadyRunningVa + i:X8}.";
                    RynthLog.Compat($"Compat: multi-client hook failed - {_statusMessage}");
                    return;
                }
            }

            TryInstallIsAlreadyRunningHook(textSection);
            TryInstallOpenDataFileHook(textSection);

            if (!IsAlreadyRunningInstalled && !OpenDataFileInstalled)
            {
                _statusMessage = "No multi-client compatibility hooks installed.";
                RynthLog.Compat($"Compat: multi-client hook failed - {_statusMessage}");
                return;
            }

            if (IsInstalled)
                _statusMessage = $"Hooked Client::IsAlreadyRunning @ 0x{IsAlreadyRunningVa:X8} and CLBlockAllocator::OpenDataFile @ 0x{OpenDataFileVa:X8}.";
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
        int funcOff = IsAlreadyRunningVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff + IsAlreadyRunningSignature.Length > textSection.Bytes.Length)
        {
            RynthLog.Compat($"Compat: multi-client hook unavailable - Client::IsAlreadyRunning VA 0x{IsAlreadyRunningVa:X8} is outside the readable text window.");
            return;
        }

        for (int i = 0; i < IsAlreadyRunningSignature.Length; i++)
        {
            byte? expected = IsAlreadyRunningSignature[i];
            if (expected.HasValue && textSection.Bytes[funcOff + i] != expected.Value)
            {
                RynthLog.Compat($"Compat: multi-client hook unavailable - Client::IsAlreadyRunning signature mismatch at 0x{IsAlreadyRunningVa + i:X8}.");
                return;
            }
        }

        _targetAddress = new IntPtr(IsAlreadyRunningVa);
        _detour = IsAlreadyRunningDetour;
        IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_detour);
        _originalIsAlreadyRunning = Marshal.GetDelegateForFunctionPointer<IsAlreadyRunningDelegate>(MinHook.HookCreate(_targetAddress, detourPtr));
        Thread.MemoryBarrier();
        MinHook.Enable(_targetAddress);

        IsAlreadyRunningInstalled = true;
        RynthLog.Verbose($"Compat: multi-client hook ready - IsAlreadyRunning=0x{IsAlreadyRunningVa:X8}");
    }

    private static void TryInstallOpenDataFileHook(AcClientTextSection textSection)
    {
        int funcOff = OpenDataFileVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff + OpenDataFileSignature.Length > textSection.Bytes.Length)
        {
            RynthLog.Compat($"Compat: multi-client data-file hook unavailable - CLBlockAllocator::OpenDataFile VA 0x{OpenDataFileVa:X8} is outside the readable text window.");
            return;
        }

        for (int i = 0; i < OpenDataFileSignature.Length; i++)
        {
            byte? expected = OpenDataFileSignature[i];
            if (expected.HasValue && textSection.Bytes[funcOff + i] != expected.Value)
            {
                RynthLog.Compat($"Compat: multi-client data-file hook unavailable - CLBlockAllocator::OpenDataFile signature mismatch at 0x{OpenDataFileVa + i:X8}.");
                return;
            }
        }

        _openDataFileAddress = new IntPtr(OpenDataFileVa);
        _openDataFileDetour = OpenDataFileDetour;
        IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_openDataFileDetour);
        _originalOpenDataFile = Marshal.GetDelegateForFunctionPointer<OpenDataFileDelegate>(MinHook.HookCreate(_openDataFileAddress, detourPtr));
        Thread.MemoryBarrier();
        MinHook.Enable(_openDataFileAddress);

        OpenDataFileInstalled = true;
        RynthLog.Verbose($"Compat: multi-client data-file hook ready - CLBlockAllocator::OpenDataFile=0x{OpenDataFileVa:X8}");
    }

    private static byte IsAlreadyRunningDetour(IntPtr thisPtr)
    {
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
