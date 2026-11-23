// ============================================================================
//  RynthCore.Engine - D3D9/AcGameDevicePolicy.cs
//  The pure part of AcGameDevice (finding AC's own IDirect3DDevice9 so the
//  EndScene hook needs no device of its own): where in RenderDeviceD3D the
//  device pointer sits, read from SetFFAmbientColor32's code, and whether a
//  device found there is one to hook. No AC, no Win32; tools/AcGameDeviceTests.
// ============================================================================
using System;

namespace RynthCore.Engine.D3D9;

internal static class AcGameDevicePolicy
{
    /// <summary>Where the device offset's 4 bytes sit in RenderDeviceD3D::SetFFAmbientColor32
    /// (mov eax,[esi+disp32] at +0x1A: 8B 86 disp32).</summary>
    public const int DeviceOperandInSetAmbient = 28;

    /// <summary>The offset on the 4,841,472-byte client (RenderDeviceD3D+0x468).</summary>
    public const int DeviceOffsetFallback = 0x468;

    /// <summary>A RenderDeviceD3D is a few KB: an offset outside this is not the device field.</summary>
    public const int MinDeviceOffset = 0x10, MaxDeviceOffset = 0x4000;

    /// <summary>
    /// The device field's offset from the bytes of SetFFAmbientColor32 at <paramref name="at"/>:
    /// "mov eax,[esi+disp32]; mov ecx,[eax]; push edi; push 8Bh (D3DRS_AMBIENT)" must be where the
    /// pattern puts it, and the offset plausible and 4-aligned. -1 when the code isn't that.
    /// </summary>
    public static int DeviceOffsetFromSetAmbient(ReadOnlySpan<byte> code, int at)
    {
        int op = at + DeviceOperandInSetAmbient;
        if (at < 0 || op + 9 > code.Length)
            return -1;
        if (code[op - 2] != 0x8B || code[op - 1] != 0x86)                 // mov eax,[esi+disp32]
            return -1;
        if (code[op + 4] != 0x8B || code[op + 5] != 0x08 || code[op + 6] != 0x57   // mov ecx,[eax]; push edi
            || code[op + 7] != 0x68 || code[op + 8] != 0x8B)               // push 8Bh (D3DRS_AMBIENT)
            return -1;
        int offset = code[op] | code[op + 1] << 8 | code[op + 2] << 16 | code[op + 3] << 24;
        return IsPlausibleOffset(offset) ? offset : -1;
    }

    public static bool IsPlausibleOffset(int offset) =>
        offset >= MinDeviceOffset && offset <= MaxDeviceOffset && (offset & 3) == 0;

    /// <summary>
    /// The rel32 call at <paramref name="callAt"/> (E8 rel32) lands on <paramref name="targetVa"/>.
    /// <paramref name="callVa"/> is the VA of the E8 byte.
    /// </summary>
    public static bool CallLandsOn(ReadOnlySpan<byte> code, int callAt, int callVa, int targetVa)
    {
        if (callAt < 0 || callAt + 5 > code.Length || code[callAt] != 0xE8)
            return false;
        int rel = code[callAt + 1] | code[callAt + 2] << 8 | code[callAt + 3] << 16 | code[callAt + 4] << 24;
        return unchecked(callVa + 5 + rel) == targetVa;
    }

    /// <summary>
    /// Whether a device AC holds is one the EndScene hook can be taken from: its EndScene, Reset and
    /// Present slots all in the loaded d3d9.dll (whichever d3d9.dll the process loaded: Windows' own,
    /// DXVK, a wrapper). Each argument is the module (HMODULE) that holds that address, 0 = none.
    /// Null = good; otherwise why not (logged, then the throwaway-device discovery is the last resort).
    /// </summary>
    public static string? JudgeDevice(long d3d9Module, long endSceneModule, long resetModule, long presentModule)
    {
        if (d3d9Module == 0)
            return "d3d9.dll is not loaded";
        if (endSceneModule != d3d9Module)
            return endSceneModule == 0 ? "its EndScene slot is in no module" : "its EndScene slot is outside d3d9.dll";
        if (resetModule != d3d9Module)
            return "its Reset slot is outside d3d9.dll";
        if (presentModule != d3d9Module)
            return "its Present slot is outside d3d9.dll";
        return null;
    }
}
