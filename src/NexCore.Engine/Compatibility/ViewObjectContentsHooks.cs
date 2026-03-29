using System;
using System.Runtime.InteropServices;
using System.Threading;
using NexCore.Engine.Hooking;
using NexCore.Engine.Plugins;

namespace NexCore.Engine.Compatibility;

internal static class ViewObjectContentsHooks
{
    private const int ViewObjectContentsVa = 0x005596B0;
    private const int StopViewingObjectContentsVa = 0x00559770;
    private static readonly byte[] ViewObjectContentsSignature =
    [
        0x53, 0x8B, 0x5C, 0x24, 0x08, 0x56, 0x57, 0x53,
        0x8B, 0xF9, 0xE8, 0x71, 0xF2, 0xFA, 0xFF, 0x8B,
        0xF0, 0x85, 0xF6, 0x75, 0x57
    ];
    private static readonly byte[] StopViewingObjectContentsSignature =
    [
        0x56, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x57, 0x8B,
        0xF1, 0xE8, 0x62, 0xF1, 0xFA, 0xFF, 0x85, 0xC0,
        0x74, 0x07, 0xC7, 0x40, 0x50, 0x00, 0x00, 0x00,
        0x00
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ViewObjectContentsDelegate(IntPtr thisPtr, uint objectId, IntPtr newContents);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void StopViewingObjectContentsDelegate(IntPtr thisPtr, uint objectId);

    private static ViewObjectContentsDelegate? _originalViewObjectContents;
    private static ViewObjectContentsDelegate? _viewObjectContentsDetour;
    private static StopViewingObjectContentsDelegate? _originalStopViewingObjectContents;
    private static StopViewingObjectContentsDelegate? _stopViewingObjectContentsDetour;
    private static IntPtr _viewTargetAddress;
    private static IntPtr _stopTargetAddress;
    private static string _statusMessage = "Not probed yet.";
    private static int _viewDispatchCount;
    private static int _stopDispatchCount;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static void Initialize(Action<string>? log = null)
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection, log))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int viewOff = ViewObjectContentsVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, viewOff, ViewObjectContentsSignature))
        {
            _statusMessage = $"ACCObjectMaint::ViewObjectContents signature mismatch @ 0x{ViewObjectContentsVa:X8}.";
            log?.Invoke($"Compat: view-object-contents hook failed - {_statusMessage}");
            return;
        }

        int stopOff = StopViewingObjectContentsVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, stopOff, StopViewingObjectContentsSignature))
        {
            _statusMessage = $"ACCObjectMaint::StopViewingObjectContents signature mismatch @ 0x{StopViewingObjectContentsVa:X8}.";
            log?.Invoke($"Compat: view-object-contents hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _viewTargetAddress = new IntPtr(textSection.TextBaseVa + viewOff);
            _viewObjectContentsDetour = ViewObjectContentsDetour;
            IntPtr viewDetourPtr = Marshal.GetFunctionPointerForDelegate(_viewObjectContentsDetour);
            IntPtr viewOriginalPtr = MinHook.Hook(_viewTargetAddress, viewDetourPtr);
            _originalViewObjectContents = Marshal.GetDelegateForFunctionPointer<ViewObjectContentsDelegate>(viewOriginalPtr);

            _stopTargetAddress = new IntPtr(textSection.TextBaseVa + stopOff);
            _stopViewingObjectContentsDetour = StopViewingObjectContentsDetour;
            IntPtr stopDetourPtr = Marshal.GetFunctionPointerForDelegate(_stopViewingObjectContentsDetour);
            IntPtr stopOriginalPtr = MinHook.Hook(_stopTargetAddress, stopDetourPtr);
            _originalStopViewingObjectContents = Marshal.GetDelegateForFunctionPointer<StopViewingObjectContentsDelegate>(stopOriginalPtr);

            IsInstalled = true;
            _statusMessage = $"Hooked contents view seams @ 0x{_viewTargetAddress.ToInt32():X8}/0x{_stopTargetAddress.ToInt32():X8}.";
            log?.Invoke(
                $"Compat: view-object-contents hooks ready - view=0x{_viewTargetAddress.ToInt32():X8}, stop=0x{_stopTargetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            log?.Invoke($"Compat: view-object-contents hook failed - {ex.Message}");
        }
    }

    private static void ViewObjectContentsDetour(IntPtr thisPtr, uint objectId, IntPtr newContents)
    {
        _originalViewObjectContents!(thisPtr, objectId, newContents);
        if (objectId == 0)
            return;

        int count = Interlocked.Increment(ref _viewDispatchCount);
        if (count <= 5)
            EntryPoint.Log($"Compat: view contents #{count} id=0x{objectId:X8} contents=0x{newContents.ToInt32():X8}");

        PluginManager.QueueViewObjectContents(objectId);
    }

    private static void StopViewingObjectContentsDetour(IntPtr thisPtr, uint objectId)
    {
        _originalStopViewingObjectContents!(thisPtr, objectId);
        if (objectId == 0)
            return;

        int count = Interlocked.Increment(ref _stopDispatchCount);
        if (count <= 5)
            EntryPoint.Log($"Compat: stop view contents #{count} id=0x{objectId:X8}");

        PluginManager.QueueStopViewingObjectContents(objectId);
    }
}
