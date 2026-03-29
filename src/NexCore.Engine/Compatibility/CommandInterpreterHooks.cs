using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NexCore.Engine.Compatibility;

internal static class CommandInterpreterHooks
{
    private const int SmartBoxCmdInterpOffset = 0xB8;
    private const int SmartBoxPlayerIdOffset = 0xF4;
    private const int SmartBoxPlayerOffset = 0xF8;
    private const int VtblCommenceJumpIndex = 16;
    private const int VtblDoJumpIndex = 17;
    private const int VtblSetAutoRunIndex = 51;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void CommenceJumpDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void DoJumpDelegate(IntPtr thisPtr, int autonomous);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetAutoRunDelegate(IntPtr thisPtr, int val, int applyMovement);

    private static IntPtr _smartboxStaticAddr;
    private static IntPtr _boundCmdInterp;
    private static IntPtr _moduleBase;
    private static int _imageSize;
    private static CommenceJumpDelegate? _commenceJump;
    private static DoJumpDelegate? _doJump;
    private static SetAutoRunDelegate? _setAutoRun;
    private static readonly List<IntPtr> _smartboxStaticCandidates = [];
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInitialized { get; private set; }
    public static bool HasTapJump => _commenceJump != null && _doJump != null;
    public static bool HasSetAutoRun => _setAutoRun != null;
    public static string StatusMessage => _statusMessage;

    public static bool Probe(Action<string>? log = null)
    {
        Reset();

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection, log))
        {
            _statusMessage = "acclient.exe not available.";
            return false;
        }

        try
        {
            _moduleBase = textSection.ModuleBase;
            _imageSize = textSection.ImageSize;

            CollectSmartBoxCandidates(textSection.Bytes, textSection.TextBaseVa, textSection.ModuleBase.ToInt32());
            if (_smartboxStaticCandidates.Count == 0)
            {
                _statusMessage = "SmartBox::smartbox static not found.";
                log?.Invoke("Compat: command interpreter probe failed - SmartBox::smartbox static not found.");
                return false;
            }

            _smartboxStaticAddr = _smartboxStaticCandidates[0];
            log?.Invoke($"Compat: command interpreter found {_smartboxStaticCandidates.Count} SmartBox candidate(s).");

            if (!TryBindDelegates(log))
            {
                if (string.IsNullOrEmpty(_statusMessage))
                    _statusMessage = "cmdinterp unavailable.";

                log?.Invoke($"Compat: command interpreter probe pending - {_statusMessage}");
                return false;
            }

            IsInitialized = true;
            _statusMessage = "Ready.";
            log?.Invoke($"Compat: command interpreter hooks ready - smartbox=0x{_smartboxStaticAddr.ToInt32():X8}, cmdinterp=0x{_boundCmdInterp.ToInt32():X8}");
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            log?.Invoke($"Compat: command interpreter probe failed - {ex.Message}");
            return false;
        }
    }

    public static bool SetAutoRun(bool enabled)
    {
        if (!TryBindDelegates(EntryPoint.Log))
            return false;

        try
        {
            _setAutoRun!(_boundCmdInterp, enabled ? 1 : 0, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TapJump()
    {
        if (!TryBindDelegates(EntryPoint.Log))
            return false;

        try
        {
            // Mirror the normal keyboard jump path: key-down starts charge,
            // key-up immediately executes an autonomous tap jump.
            _commenceJump!(_boundCmdInterp);
            _doJump!(_boundCmdInterp, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBindDelegates(Action<string>? log = null)
    {
        if (_smartboxStaticAddr == IntPtr.Zero && _smartboxStaticCandidates.Count == 0)
        {
            _statusMessage = "SmartBox::smartbox not located.";
            return false;
        }

        IntPtr selectedStaticAddr = IntPtr.Zero;
        IntPtr smartBox = IntPtr.Zero;
        IntPtr cmdInterp = IntPtr.Zero;
        IntPtr vtable = IntPtr.Zero;
        string? bestFailure = null;

        foreach (IntPtr candidate in EnumerateCandidates())
        {
            if (!TryResolveCandidate(candidate, out smartBox, out cmdInterp, out vtable, out string failure))
            {
                bestFailure ??= $"0x{candidate.ToInt32():X8}: {failure}";
                continue;
            }

            selectedStaticAddr = candidate;
            break;
        }

        if (selectedStaticAddr == IntPtr.Zero)
        {
            _statusMessage = bestFailure ?? "CommandInterpreter not ready.";
            log?.Invoke($"Compat: command interpreter bind pending - {_statusMessage}");
            return false;
        }

        if (cmdInterp == _boundCmdInterp && _commenceJump != null && _doJump != null && _setAutoRun != null)
        {
            _smartboxStaticAddr = selectedStaticAddr;
            IsInitialized = true;
            _statusMessage = "Ready.";
            return true;
        }

        IntPtr commenceJumpPtr = Marshal.ReadIntPtr(vtable, VtblCommenceJumpIndex * IntPtr.Size);
        IntPtr doJumpPtr = Marshal.ReadIntPtr(vtable, VtblDoJumpIndex * IntPtr.Size);
        IntPtr setAutoRunPtr = Marshal.ReadIntPtr(vtable, VtblSetAutoRunIndex * IntPtr.Size);

        if (!IsPointerInModule(commenceJumpPtr) || !IsPointerInModule(doJumpPtr) || !IsPointerInModule(setAutoRunPtr))
        {
            _statusMessage = $"CommandInterpreter vtable looks invalid (commence=0x{commenceJumpPtr.ToInt32():X8}, doJump=0x{doJumpPtr.ToInt32():X8}, autorun=0x{setAutoRunPtr.ToInt32():X8}).";
            log?.Invoke($"Compat: command interpreter bind failed - {_statusMessage}");
            return false;
        }

        _commenceJump = Marshal.GetDelegateForFunctionPointer<CommenceJumpDelegate>(commenceJumpPtr);
        _doJump = Marshal.GetDelegateForFunctionPointer<DoJumpDelegate>(doJumpPtr);
        _setAutoRun = Marshal.GetDelegateForFunctionPointer<SetAutoRunDelegate>(setAutoRunPtr);

        _smartboxStaticAddr = selectedStaticAddr;
        _boundCmdInterp = cmdInterp;
        IsInitialized = true;
        _statusMessage = "Ready.";
        log?.Invoke($"Compat: command interpreter bound - smartboxStatic=0x{selectedStaticAddr.ToInt32():X8}, smartbox=0x{smartBox.ToInt32():X8}, cmdinterp=0x{cmdInterp.ToInt32():X8}");
        return true;
    }

    private static IEnumerable<IntPtr> EnumerateCandidates()
    {
        if (_smartboxStaticAddr != IntPtr.Zero)
            yield return _smartboxStaticAddr;

        foreach (IntPtr candidate in _smartboxStaticCandidates)
        {
            if (candidate == _smartboxStaticAddr)
                continue;

            yield return candidate;
        }
    }

    private static bool TryResolveCandidate(
        IntPtr smartboxStaticAddr,
        out IntPtr smartBox,
        out IntPtr cmdInterp,
        out IntPtr vtable,
        out string failure)
    {
        smartBox = IntPtr.Zero;
        cmdInterp = IntPtr.Zero;
        vtable = IntPtr.Zero;
        failure = string.Empty;

        try
        {
            smartBox = Marshal.ReadIntPtr(smartboxStaticAddr);
            if (smartBox == IntPtr.Zero)
            {
                failure = "SmartBox instance not ready.";
                return false;
            }

            IntPtr smartBoxVtable = Marshal.ReadIntPtr(smartBox);
            if (!IsPointerInModule(smartBoxVtable))
            {
                failure = $"SmartBox vtable looks invalid (0x{smartBoxVtable.ToInt32():X8}).";
                return false;
            }

            cmdInterp = Marshal.ReadIntPtr(smartBox + SmartBoxCmdInterpOffset);
            if (cmdInterp == IntPtr.Zero)
            {
                int playerId = Marshal.ReadInt32(smartBox + SmartBoxPlayerIdOffset);
                IntPtr player = Marshal.ReadIntPtr(smartBox + SmartBoxPlayerOffset);
                failure = playerId != 0 || player != IntPtr.Zero
                    ? $"CommandInterpreter not ready (playerId=0x{playerId:X8}, player=0x{player.ToInt32():X8})."
                    : "SmartBox active but player not ready.";
                return false;
            }

            vtable = Marshal.ReadIntPtr(cmdInterp);
            if (!IsPointerInModule(vtable))
            {
                failure = $"CommandInterpreter vtable looks invalid (0x{vtable.ToInt32():X8}).";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    private static void CollectSmartBoxCandidates(byte[] text, int textBaseVa, int imageBase)
    {
        _smartboxStaticCandidates.Clear();

        HashSet<int> seen = [];

        AddCandidates(FindCmdInterpReferenceCandidates(text, imageBase), seen);
        AddCandidates(FindCleanupPatternCandidates(text, textBaseVa, imageBase), seen);
    }

    private static void AddCandidates(IEnumerable<IntPtr> candidates, HashSet<int> seen)
    {
        foreach (IntPtr candidate in candidates)
        {
            int value = candidate.ToInt32();
            if (value == 0 || !seen.Add(value))
                continue;

            _smartboxStaticCandidates.Add(candidate);
        }
    }

    private static IEnumerable<IntPtr> FindCleanupPatternCandidates(byte[] text, int textBaseVa, int imageBase)
    {
        int dataLo = imageBase + 0x400000;
        int dataHi = imageBase + 0x510000;

        for (int i = 0; i < text.Length - 12; i++)
        {
            if (text[i] != 0xC7 || text[i + 1] != 0x05)
                continue;

            int addr = BitConverter.ToInt32(text, i + 2);
            if (addr < dataLo || addr > dataHi)
                continue;

            if (BitConverter.ToInt32(text, i + 6) != 0 || text[i + 10] != 0xC3)
                continue;

            byte[] loadPat = new byte[5];
            loadPat[0] = 0xA1;
            BitConverter.GetBytes(addr).CopyTo(loadPat, 1);

            for (int back = 5; back < 40; back++)
            {
                int j = i - back;
                if (j < 0)
                    break;

                if (!PatternScanner.VerifyBytes(text, j, loadPat))
                    continue;

                for (int k = j + 5; k < j + 10 && k < text.Length - 1; k++)
                {
                    if (text[k] != 0x85 || text[k + 1] != 0xC0)
                        continue;

                    bool hasDtorCall = false;
                    for (int m = k; m < i - 2 && m < text.Length - 3; m++)
                    {
                        if (text[m] == 0x6A && text[m + 1] == 0x01 &&
                            text[m + 2] == 0xFF && text[m + 3] == 0x10)
                        {
                            hasDtorCall = true;
                            break;
                        }
                    }

                    if (hasDtorCall)
                    {
                        yield return new IntPtr(addr);
                        break;
                    }
                }
            }
        }
    }

    private static IEnumerable<IntPtr> FindCmdInterpReferenceCandidates(byte[] text, int imageBase)
    {
        int dataLo = imageBase + 0x400000;
        int dataHi = imageBase + 0x510000;

        for (int i = 0; i < text.Length - 24; i++)
        {
            if (text[i] != 0xA1)
                continue;

            int addr = BitConverter.ToInt32(text, i + 1);
            if (addr < dataLo || addr > dataHi)
                continue;

            bool hasNullCheck = false;
            for (int k = i + 5; k < i + 10 && k < text.Length - 1; k++)
            {
                if (text[k] == 0x85 && text[k + 1] == 0xC0)
                {
                    hasNullCheck = true;
                    break;
                }
            }

            if (!hasNullCheck)
                continue;

            for (int k = i + 5; k < i + 24 && k < text.Length - 6; k++)
            {
                if (text[k] != 0x8B)
                    continue;

                byte modrm = text[k + 1];
                if ((modrm & 0xC7) != 0x80)
                    continue;

                if (BitConverter.ToInt32(text, k + 2) != SmartBoxCmdInterpOffset)
                    continue;

                yield return new IntPtr(addr);
                break;
            }
        }
    }

    private static bool IsPointerInModule(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero || _moduleBase == IntPtr.Zero || _imageSize <= 0)
            return false;

        int value = ptr.ToInt32();
        int start = _moduleBase.ToInt32();
        int end = start + _imageSize;
        return value >= start && value < end;
    }

    private static void Reset()
    {
        _smartboxStaticAddr = IntPtr.Zero;
        _boundCmdInterp = IntPtr.Zero;
        _moduleBase = IntPtr.Zero;
        _imageSize = 0;
        _commenceJump = null;
        _doJump = null;
        _setAutoRun = null;
        _smartboxStaticCandidates.Clear();
        IsInitialized = false;
        _statusMessage = "Not probed yet.";
    }
}
