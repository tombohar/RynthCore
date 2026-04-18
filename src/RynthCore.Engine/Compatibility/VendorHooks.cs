using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Hooks gmVendorUI::RecvNotice_OpenVendor and RecvNotice_CloseVendor to detect vendor open/close.
///
/// VAs from acclient.map (Chorizite) + 0x00401000 base:
///   RecvNotice_OpenVendor real impl  = 0x000C52E0 + 0x00401000 = 0x004C52E0 (thunk at 0x004C62E0 → jumps here)
///   RecvNotice_CloseVendor           = 0x000BFF40 + 0x00401000 = 0x004C0F40
///
/// We hook the real impl (0x004C5790, called by the thunk) and the close entry directly.
/// </summary>
internal static class VendorHooks
{
    // gmVendorUI::RecvNotice_OpenVendor — actual implementation (thunk at 0x004C62E0 jumps here)
    private const int RecvNoticeOpenVendorVa  = 0x004C5790;
    // gmVendorUI::RecvNotice_CloseVendor
    private const int RecvNoticeCloseVendorVa = 0x004C0F40;

    // Verified entry bytes (from Ghidra)
    private static readonly byte[] OpenVendorSignature =
    [
        0x83, 0xEC, 0x2C, 0x53, 0x55, 0x56, 0x57   // SUB ESP,2C; PUSH EBX; PUSH EBP; PUSH ESI; PUSH EDI
    ];
    private static readonly byte[] CloseVendorSignature =
    [
        0x8A, 0x44, 0x24, 0x04, 0x84, 0xC0, 0x75    // MOV AL,[ESP+4]; TEST AL,AL; JNZ ...
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RecvNoticeOpenVendorDelegate(IntPtr thisPtr, uint vendorId, IntPtr vpRef, IntPtr itemsRef, int shopMode);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RecvNoticeCloseVendorDelegate(IntPtr thisPtr, int updating);

    private static RecvNoticeOpenVendorDelegate?  _originalOpen;
    private static RecvNoticeOpenVendorDelegate?  _openDetour;
    private static RecvNoticeCloseVendorDelegate? _originalClose;
    private static RecvNoticeCloseVendorDelegate? _closeDetour;

    private static IntPtr _openAddress;
    private static IntPtr _closeAddress;
    private static string _statusMessage = "Not probed yet.";
    private static uint   _currentVendorId;

    public static bool   IsInstalled     { get; private set; }
    public static string StatusMessage   => _statusMessage;

    public static void Initialize()
    {
        if (IsInstalled) return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int openOff  = RecvNoticeOpenVendorVa  - textSection.TextBaseVa;
        int closeOff = RecvNoticeCloseVendorVa - textSection.TextBaseVa;

        if (!PatternScanner.VerifyBytes(textSection.Bytes, openOff, OpenVendorSignature))
        {
            _statusMessage = $"RecvNotice_OpenVendor signature mismatch @ 0x{RecvNoticeOpenVendorVa:X8}.";
            RynthLog.Compat($"Compat: vendor hooks failed - {_statusMessage}");
            return;
        }

        if (!PatternScanner.VerifyBytes(textSection.Bytes, closeOff, CloseVendorSignature))
        {
            _statusMessage = $"RecvNotice_CloseVendor signature mismatch @ 0x{RecvNoticeCloseVendorVa:X8}.";
            RynthLog.Compat($"Compat: vendor hooks failed - {_statusMessage}");
            return;
        }

        try
        {
            _openAddress   = new IntPtr(textSection.TextBaseVa + openOff);
            _openDetour    = OpenVendorDetour;
            IntPtr openPtr = Marshal.GetFunctionPointerForDelegate(_openDetour);
            _originalOpen  = Marshal.GetDelegateForFunctionPointer<RecvNoticeOpenVendorDelegate>(
                MinHook.HookCreate(_openAddress, openPtr));

            _closeAddress   = new IntPtr(textSection.TextBaseVa + closeOff);
            _closeDetour    = CloseVendorDetour;
            IntPtr closePtr = Marshal.GetFunctionPointerForDelegate(_closeDetour);
            _originalClose  = Marshal.GetDelegateForFunctionPointer<RecvNoticeCloseVendorDelegate>(
                MinHook.HookCreate(_closeAddress, closePtr));

            Thread.MemoryBarrier();
            MinHook.Enable(_openAddress);
            MinHook.Enable(_closeAddress);

            IsInstalled    = true;
            _statusMessage = $"Hooked vendor @ open=0x{_openAddress.ToInt32():X8} close=0x{_closeAddress.ToInt32():X8}.";
            RynthLog.Verbose($"Compat: vendor hooks ready - {_statusMessage}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: vendor hooks failed - {ex.Message}");
        }
    }

    private static void OpenVendorDetour(IntPtr thisPtr, uint vendorId, IntPtr vpRef, IntPtr itemsRef, int shopMode)
    {
        _originalOpen!(thisPtr, vendorId, vpRef, itemsRef, shopMode);
        if (vendorId == 0) return;
        _currentVendorId = vendorId;
        PluginManager.QueueVendorOpen(vendorId);
    }

    private static void CloseVendorDetour(IntPtr thisPtr, int updating)
    {
        _originalClose!(thisPtr, updating);
        uint vid = _currentVendorId;
        _currentVendorId = 0;
        if (vid != 0)
            PluginManager.QueueVendorClose(vid);
    }
}
