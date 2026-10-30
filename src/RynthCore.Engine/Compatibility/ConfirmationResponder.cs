// ============================================================================
//  RynthCore.Engine - Compatibility/ConfirmationResponder.cs
//
//  Plugin API v85: answer the server's yes/no prompts (craft chance, augmentation,
//  ...) for a plugin, the way UtilityBelt's helper clicks "Yes" on the tinkering
//  dialog, without the client ever opening that dialog.
//
//  INBOUND  ConfirmationRequest (GameEvent 0x0274: u32 type, u32 contextId,
//         string16 text) reaches UIQueueManager::ProcessNetBlobData, which
//         SmartBoxHooks already detours. Its case calls
//         CM_Character::DispatchUI_ConfirmationRequest (0x006A3BB0), which hands the
//         prompt to the UI handler (0x00564E40) and returns that handler's result;
//         the handler returns 0 on every path. So when the request is answered here,
//         the detour skips the original and returns 0: nothing else in the client
//         sees the prompt, and no dialog is left on screen (ACE never sends
//         ConfirmationDone after a response, so an answered dialog would stay open).
//
//  OUTBOUND CM_Character::Event_ConfirmationResponse(uint type, uint contextId,
//         int accept), 0x006A2030 on the live client: the generated CM_* sender
//         template (see Event_GiveObjectRequest in ClientHelperHooks), arg block
//         +0x10 (three args), opcode 0x0275. Cdecl, returns bool. Verified unique
//         with tools/pe_pattern.py on the live acclient.
//
//  ARMING  A plugin arms one answer: the confirmation type, a minimum percent and
//         a timeout. The next matching request inside the timeout is answered
//         Yes when the server's own "N percent chance" is at least the minimum
//         (or the minimum is 0), else No, and the arm is spent. An unarmed or
//         expired request is left to the client, as before. The last request is
//         always recorded so the plugin can see what happened.
//
//  Threading: the detour runs on AC's main thread and only takes a short lock;
//         the sender is called there, outside the lock. Arm/Get run on any thread.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class ConfirmationResponder
{
    /// <summary>GameEvent ConfirmationRequest.</summary>
    public const uint EventConfirmationRequest = 0x0274;

    // ABI outcome codes (ConfirmationInfoNative.Outcome). Never renumber.
    public const int OutcomeNone = 0;      // no request seen this session
    public const int OutcomeShown = 1;     // not armed (or expired / other type): the client showed it
    public const int OutcomeAccepted = 2;  // answered Yes for the plugin
    public const int OutcomeDeclined = 3;  // answered No: the server's percent was below the minimum
    public const int OutcomeSendFailed = 4; // armed, but the sender failed; the client showed it

    private const int EventConfirmationResponseVa = 0x006A2030;

    // CM_Character::Event_ConfirmationResponse: the shared CM_* sender template with the
    // three-argument block (+0x10) and the opcode immediate 0x0275 at the end.
    private static readonly byte?[] PatEventConfirmationResponse = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x10, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x75, 0x02, 0x00, 0x00 ];

    private static delegate* unmanaged[Cdecl]<uint, uint, int, byte> _eventConfirmationResponse;

    private static readonly object _lock = new();
    // The armed answer (one-shot).
    private static bool _armed;
    private static uint _armedType;
    private static int _armedMinPercent;
    private static long _armedUntil;
    // The last request seen.
    private static int _seq;
    private static uint _lastType, _lastContext;
    private static int _lastPercent = -1, _lastOutcome;
    private static long _lastAt;
    private static int _logCount;

    /// <summary>True when the response sender is bound on this client.</summary>
    public static bool HasSender => _eventConfirmationResponse != null;

    /// <summary>Bind the sender. Called from ClientHelperHooks.Probe (engine init).</summary>
    public static void Probe(AcClientTextSection text)
    {
        HookResolver.ResolveResult r = HookResolver.Resolve(text, "Confirmation.Event_ConfirmationResponse",
            PatEventConfirmationResponse, EventConfirmationResponseVa);
        _eventConfirmationResponse = r.Success && r.Source == HookResolver.ResolveSource.PatternScan
            ? (delegate* unmanaged[Cdecl]<uint, uint, int, byte>)r.Address
            : null;
        RynthLog.Compat($"Compat: confirmation response sender {(HasSender ? $"bound @0x{r.Address.ToInt32():X8}" : "not found")}");
    }

    /// <summary>
    /// Arm one answer for the next request of <paramref name="type"/> within
    /// <paramref name="timeoutMs"/>. timeoutMs &lt;= 0 disarms. 1 armed (or disarmed),
    /// 0 the sender or the game-event hook isn't available, -3 bad arguments.
    /// </summary>
    public static int Arm(uint type, int minPercent, int timeoutMs)
    {
        if (timeoutMs <= 0)
        {
            lock (_lock) _armed = false;
            return 1;
        }
        if (type == 0 || minPercent < 0 || minPercent > 100 || timeoutMs > 120_000)
            return -3;
        if (!HasSender || !SmartBoxHooks.IsGameEventHookInstalled)
            return 0;
        lock (_lock)
        {
            _armed = true;
            _armedType = type;
            _armedMinPercent = minPercent;
            _armedUntil = Environment.TickCount64 + timeoutMs;
        }
        return 1;
    }

    /// <summary>The last request: its sequence number (0 = none yet), type, context, the
    /// server's percent (-1 when the text has none), outcome and age.</summary>
    public static int GetLast(out uint type, out uint contextId, out int percent, out int outcome, out int ageMs)
    {
        lock (_lock)
        {
            type = _lastType;
            contextId = _lastContext;
            percent = _lastPercent;
            outcome = _lastOutcome;
            long age = _seq == 0 ? 0 : Environment.TickCount64 - _lastAt;
            ageMs = age > int.MaxValue ? int.MaxValue : (int)age;
            return _seq;
        }
    }

    /// <summary>
    /// The game-event detour's hook for 0x0274 (AC's main thread). <paramref name="data"/>
    /// is the event blob (type at +0). True = answered here: the caller must skip AC's
    /// handler so no dialog opens.
    /// </summary>
    public static bool OnRequest(IntPtr data, uint size)
    {
        if (data == IntPtr.Zero || size < 12)
            return false;
        uint type = unchecked((uint)Marshal.ReadInt32(data, 4));
        uint context = unchecked((uint)Marshal.ReadInt32(data, 8));
        int percent = ParsePercent(data, size);

        bool answer = false, accept = false;
        lock (_lock)
        {
            _seq++;
            _lastType = type;
            _lastContext = context;
            _lastPercent = percent;
            _lastAt = Environment.TickCount64;
            _lastOutcome = OutcomeShown;
            if (_armed && _armedType == type && Environment.TickCount64 <= _armedUntil)
            {
                _armed = false;
                answer = true;
                accept = _armedMinPercent <= 0 || (percent >= 0 && percent >= _armedMinPercent);
            }
        }
        if (!answer)
            return false;

        bool sent = false;
        try
        {
            sent = _eventConfirmationResponse != null && _eventConfirmationResponse(type, context, accept ? 1 : 0) != 0;
        }
        catch { sent = false; }

        lock (_lock)
            _lastOutcome = !sent ? OutcomeSendFailed : accept ? OutcomeAccepted : OutcomeDeclined;
        if (_logCount < 40)
        {
            _logCount++;
            RynthLog.Compat($"Confirmation type={type} ctx=0x{context:X8} percent={percent} -> {(sent ? (accept ? "yes" : "no") : "send failed, shown")}");
        }
        return sent;
    }

    // "You determine that you have a 38 percent chance to succeed." -> 38. The text is a
    // string16 (u16 length, cp1252 bytes) at +12. -1 when there is no "N percent".
    private static int ParsePercent(IntPtr data, uint size)
    {
        const int textOff = 12;
        if (size < textOff + 2)
            return -1;
        int len = (ushort)Marshal.ReadInt16(data, textOff);
        if (len <= 0 || len > 512 || textOff + 2 + len > size)
            return -1;
        byte* p = (byte*)data + textOff + 2;
        ReadOnlySpan<byte> text = new(p, len);
        int at = text.IndexOf(" percent"u8);
        if (at <= 0)
            return -1;
        int end = at, start = at;
        while (start > 0 && text[start - 1] >= (byte)'0' && text[start - 1] <= (byte)'9')
            start--;
        if (start == end || end - start > 3)
            return -1;
        int value = 0;
        for (int i = start; i < end; i++)
            value = value * 10 + (text[i] - '0');
        return value > 100 ? 100 : value;
    }
}
