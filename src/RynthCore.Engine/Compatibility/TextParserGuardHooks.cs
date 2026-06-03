using System;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Installs a NATIVE null-guard on AC's tagged-text parser consumer
/// <c>FUN_0067D3C0</c> (live VA 0x0067D3C0) to kill the text-parser singleton
/// race AV.
///
/// Crash this fixes (captured 2026-06-03, native-crash.log):
/// <code>
/// code=0xC0000005 exAddr=acclient.exe+0x0027D3DA  (live 0x0067D3DA)  eax=0
/// 0067d3da  MOV EDI,[EAX+0xC]   ; EAX = parser context = 0 -> AV READ [null+0xC]
/// </code>
/// The parser context comes from the unsynchronized singleton DAT_008F881C.
/// When our off-thread plugin pump triggers a text-generation path while AC's
/// main thread is mid-parse on incoming chat (heavy Olthoi-hive combat chat),
/// the singleton races to null and this consumer dereferences it.
///
/// ⚠ This guard MUST be native. A MANAGED MinHook detour here was deployed
/// 2026-05-25 and crashed pid 20852 within ~7s: the consumer is called by AC's
/// main thread for EVERY parsed tag, so a managed detour incurs a NativeAOT
/// reverse-P/Invoke transition per tag and fail-fasts. The detour body now lives
/// entirely in RynthCore.SehTrampoline.dll (RC_TagParserGuard — pure native, no
/// managed transition). This class just installs the MinHook redirect to that
/// native detour and hands it the trampoline (original) BEFORE enabling. If the
/// parser context is null the native guard drops one parse run (returns 0); the
/// caller (FUN_0067E150) discards the return value, so that is safe.
/// </summary>
internal static class TextParserGuardHooks
{
    // FUN_0067D3C0 in retail/ACE acclient.exe (byte-identical; verified at install).
    private const int TargetVa = 0x0067D3C0;

    // 29-byte prologue from the function start through the AV instruction. Includes
    // the literal data pointer 0x007FF308 (bytes 5-9) — stable across retail/ACE —
    // which makes this a strong validation signature.
    private static readonly byte[] FuncSignature =
    [
        0x83, 0xEC, 0x50,                 // SUB  ESP, 0x50
        0x53,                              // PUSH EBX
        0x55,                              // PUSH EBP
        0xB8, 0x08, 0xF3, 0x7F, 0x00,      // MOV  EAX, 0x7FF308
        0x56,                              // PUSH ESI
        0x33, 0xF6,                        // XOR  ESI, ESI
        0x89, 0x44, 0x24, 0x3C,            // MOV  [ESP+0x3C], EAX
        0x89, 0x44, 0x24, 0x4C,            // MOV  [ESP+0x4C], EAX
        0x8B, 0x44, 0x24, 0x60,            // MOV  EAX, [ESP+0x60]   ; param1 = parser ctx
        0x57,                              // PUSH EDI
        0x8B, 0x78, 0x0C                   // MOV  EDI, [EAX+0xC]    ; <- AV when EAX==0
    ];

    private static IntPtr _targetAddress;
    private static int _installed;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInstalled => Volatile.Read(ref _installed) != 0;
    public static string StatusMessage => _statusMessage;

    /// <summary>Null-context parse runs the native guard has dropped (served from the
    /// trampoline DLL; 0 if unavailable).</summary>
    public static uint NullParamSkipCount => SehTrampoline.TagParserSkipCount;

    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0)
            return;

        // The detour body lives in the trampoline DLL; without it we cannot install a
        // native guard, and we must NOT fall back to a managed detour (the 7s-crash).
        if (!SehTrampoline.IsAvailable)
        {
            Volatile.Write(ref _installed, 0);
            _statusMessage = "SEH trampoline unavailable.";
            RynthLog.Compat("Compat: text-parser guard NOT installed — SEH trampoline unavailable (no managed fallback by design).");
            return;
        }

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            Volatile.Write(ref _installed, 0);
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int funcOff = TargetVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, funcOff, FuncSignature))
        {
            Volatile.Write(ref _installed, 0);
            _statusMessage = $"signature mismatch @ 0x{TargetVa:X8}.";
            RynthLog.Compat($"Compat: text-parser guard NOT installed — {_statusMessage}");
            return;
        }

        try
        {
            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);

            IntPtr detour = SehTrampoline.GetTagParserGuardAddress();
            if (detour == IntPtr.Zero)
            {
                Volatile.Write(ref _installed, 0);
                _statusMessage = "native detour address was null.";
                RynthLog.Compat($"Compat: text-parser guard NOT installed — {_statusMessage}");
                return;
            }

            // Redirect FUN_0067D3C0 -> RC_TagParserGuard (native). HookCreate returns
            // the trampoline (original); hand it to the native guard BEFORE enabling so
            // the very first detoured call already has a valid original to call through.
            IntPtr trampoline = MinHook.HookCreate(_targetAddress, detour);
            SehTrampoline.SetTagParserOriginal(trampoline);
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);

            Volatile.Write(ref _installed, 1);
            _statusMessage = $"Native parser guard @ 0x{_targetAddress.ToInt32():X8}.";
            RynthLog.Compat(
                $"Compat: text-parser guard ready (NATIVE) — target=0x{_targetAddress.ToInt32():X8}, " +
                $"detour=0x{detour.ToInt32():X8}, trampoline=0x{trampoline.ToInt32():X8}.");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _installed, 0);
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: text-parser guard install threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
