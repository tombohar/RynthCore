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

    public static bool IsInstalled => true; // no hook needed — direct memory read

    /// <summary>Returns true while the player is in the teleport/portal transit state.</summary>
    public static unsafe bool IsPortaling
    {
        get
        {
            try
            {
                IntPtr playerSystem = *(IntPtr*)PlayerSystemPtrVa;
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
        RynthLog.Verbose($"Compat: teleport-state ready — direct read CPlayerSystem+0x238 @ PlayerSystemPtr=0x{PlayerSystemPtrVa:X8}");
    }
}
