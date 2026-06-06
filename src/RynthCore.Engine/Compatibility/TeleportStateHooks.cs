using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Reads portal/teleport state directly from CPlayerSystem::teleport_in_progress.
///
/// Ghidra analysis of CPlayerSystem::SetTeleportInProgress (0x0055E2A0):
///   MOV AL, [ESP+4]          ; bool arg
///   MOV [ESI + 0x238], AL   ; stored at this+0x238
///
/// So: isPortaling = *(CPlayerSystem + 0x238)
/// PlayerSystem singleton pointer: 0x0087119C (PlayerSystemVa, already known)
/// </summary>
internal static class TeleportStateHooks
{
    private const int PlayerSystemPtrVa      = 0x0087119C;
    private const int TeleportInProgressOffset = 0x238;

    // Phase B: resolve CPlayerSystem's address by code-xref (mov dword [CPlayerSystem],0 =
    // C7 05 <addr> 00 00 00 00), operand at offset 2; the VA stays as fallback.
    private static readonly byte?[] PatXrefPlayerSystem = [ 0xC7, 0x05, null, null, null, null, 0x00, 0x00, 0x00, 0x00, 0x83, 0xC6 ];
    private static int _playerSystemPtrAddr = PlayerSystemPtrVa;

    public static bool IsInstalled => true; // no hook needed — direct memory read

    /// <summary>Returns true while the player is in the teleport/portal transit state.</summary>
    public static unsafe bool IsPortaling
    {
        get
        {
            try
            {
                IntPtr playerSystem = *(IntPtr*)_playerSystemPtrAddr;
                if (playerSystem == IntPtr.Zero) return false;
                return *(byte*)(playerSystem + TeleportInProgressOffset) != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    // Called from EntryPoint.Initialize chain — nothing to install for a direct read.
    public static void Initialize()
    {
        if (AcClientModule.TryReadTextSection(out AcClientTextSection text))
            _playerSystemPtrAddr = HookResolver.ResolveData(text, "Teleport.CPlayerSystem", PatXrefPlayerSystem, 2, PlayerSystemPtrVa).Address.ToInt32();
        RynthLog.Verbose($"Compat: teleport-state ready — direct read CPlayerSystem+0x238 @ PlayerSystemPtr=0x{_playerSystemPtrAddr:X8}");
    }
}
