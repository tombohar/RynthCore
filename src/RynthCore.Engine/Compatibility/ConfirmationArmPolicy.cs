// ============================================================================
//  RynthCore.Engine - Compatibility/ConfirmationArmPolicy.cs
//
//  The pure half of ConfirmationResponder (plugin API v85, v97): what the armed answers
//  do with a server yes/no prompt, and the "N percent" the prompt's text carries.
//  No client memory, no hooks, no clock of its own (the caller passes the time), so
//  tools\ConfirmationResponderTests compiles it as it is.
//
//  Two slots, one of each kind:
//
//  ONE-SHOT (v85, unchanged): ArmConfirmation(type, minPercent 0-100, timeoutMs 1-120000).
//         The next request of that type inside the timeout is answered Yes when the
//         server's percent is at least the minimum (or the minimum is 0), else No, and
//         the arm is spent. A new one-shot replaces the old one; timeoutMs <= 0 (without
//         the flag) disarms it. A one-shot always goes first: a plugin's own automated
//         step is answered by its own arm, whatever sticky arm is standing.
//
//  STICKY, LEAVE BELOW (v97): the same call with ArmFlagLeaveBelow (0x100) OR'ed into
//         minPercent, and a minimum of 1-100. A request at or above the minimum is answered
//         Yes; one below it, or one whose text has no percent, is LEFT TO THE CLIENT (its
//         dialog opens as if nothing were armed); it is never answered No. The arm stays
//         armed for every later request until it times out, is disarmed (the flag with
//         timeoutMs <= 0) or another sticky arm replaces it. A one-shot arm or disarm never
//         touches it. The timeout is capped at 120 s as for one-shots, so an arm left
//         behind by a plugin that went away lasts two minutes at most; a plugin that wants
//         it longer re-arms before it runs out.
//
//  An engine before v97 refuses the flag as bad arguments (-3: minPercent over 100), so
//  a plugin built for v97 fails cleanly on an older engine instead of arming a one-shot
//  that would answer No. (An old engine's disarm ignores the arguments and clears its
//  only slot: a plugin disarms its sticky arm only on a v97 engine.)
//
//  Every other bit of minPercent is refused (-3), so later flags can be added the same way.
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

/// <summary>One armed answer (ConfirmationResponder keeps a one-shot and a sticky one, under its lock).</summary>
internal struct ConfirmationArm
{
    public bool Armed;
    public uint Type;
    public int MinPercent;
    public long Until;
}

internal static class ConfirmationArmPolicy
{
    /// <summary>v97: OR'ed into ArmConfirmation's minPercent: the sticky arm (leave below-minimum prompts to the client).</summary>
    public const int ArmFlagLeaveBelow = 0x100;

    /// <summary>The longest timeout an arm takes (both kinds).</summary>
    public const int MaxTimeoutMs = 120_000;

    // ABI results of ArmConfirmation. Never renumber.
    public const int ArmOk = 1;
    public const int ArmUnavailable = 0;
    public const int ArmBadArgs = -3;

    /// <summary>What the responder does with one request.</summary>
    public enum Verdict
    {
        /// <summary>Nothing armed for it, expired, another type, or below the sticky minimum: AC shows its dialog.</summary>
        LeaveToClient,
        /// <summary>The one-shot arm said Yes (and is spent).</summary>
        Accept,
        /// <summary>The one-shot arm said No (and is spent).</summary>
        Decline,
        /// <summary>The sticky arm said Yes (and stays).</summary>
        AcceptSticky,
    }

    /// <summary>True when an arm call (any timeout) is for the sticky slot.</summary>
    public static bool IsSticky(int minPercentArg) => (minPercentArg & ArmFlagLeaveBelow) != 0;

    /// <summary>
    /// Checks an arm call's arguments (timeoutMs &gt; 0; a disarm never gets here).
    /// <see cref="ArmOk"/> with the minimum and the slot, or <see cref="ArmBadArgs"/>.
    /// </summary>
    public static int Validate(uint type, int minPercentArg, int timeoutMs, out int minPercent, out bool sticky)
    {
        sticky = IsSticky(minPercentArg);
        minPercent = minPercentArg & ~ArmFlagLeaveBelow;
        if (type == 0 || timeoutMs <= 0 || timeoutMs > MaxTimeoutMs)
            return ArmBadArgs;
        if (minPercentArg < 0 || minPercent < 0 || minPercent > 100)
            return ArmBadArgs;   // negative, an unknown flag bit, or over 100
        if (sticky && minPercent < 1)
            return ArmBadArgs;   // a sticky arm with no minimum would say Yes to every prompt
        return ArmOk;
    }

    /// <summary>A new arm from validated arguments.</summary>
    public static ConfirmationArm Make(uint type, int minPercent, int timeoutMs, long nowMs)
        => new() { Armed = true, Type = type, MinPercent = minPercent, Until = nowMs + timeoutMs };

    /// <summary>
    /// One ArmConfirmation call on the two slots: timeoutMs &lt;= 0 disarms the call's slot (the flag
    /// picks the sticky one), otherwise the arguments are checked and the call's slot is replaced.
    /// <paramref name="available"/> false (no sender or no game-event hook) arms nothing.
    /// Returns <see cref="ArmOk"/>, <see cref="ArmUnavailable"/> or <see cref="ArmBadArgs"/>.
    /// </summary>
    public static int Apply(ref ConfirmationArm oneShot, ref ConfirmationArm sticky, uint type, int minPercentArg,
        int timeoutMs, long nowMs, bool available)
    {
        bool toSticky = IsSticky(minPercentArg);
        if (timeoutMs <= 0)
        {
            if (toSticky) sticky.Armed = false;
            else oneShot.Armed = false;
            return ArmOk;
        }
        int ok = Validate(type, minPercentArg, timeoutMs, out int min, out _);
        if (ok != ArmOk)
            return ok;
        if (!available)
            return ArmUnavailable;
        if (toSticky) sticky = Make(type, min, timeoutMs, nowMs);
        else oneShot = Make(type, min, timeoutMs, nowMs);
        return ArmOk;
    }

    /// <summary>
    /// What to do with a request of <paramref name="type"/> whose text says <paramref name="percent"/>
    /// (-1 = no percent), at <paramref name="nowMs"/>. The one-shot goes first and is spent when it
    /// answers; otherwise the sticky arm answers Yes at its minimum and stays. Expired arms are dropped.
    /// </summary>
    public static Verdict Decide(ref ConfirmationArm oneShot, ref ConfirmationArm sticky, uint type, int percent, long nowMs)
    {
        if (oneShot.Armed && nowMs > oneShot.Until)
            oneShot.Armed = false;
        if (sticky.Armed && nowMs > sticky.Until)
            sticky.Armed = false;

        if (oneShot.Armed && oneShot.Type == type)
        {
            oneShot.Armed = false;
            return oneShot.MinPercent <= 0 || (percent >= 0 && percent >= oneShot.MinPercent) ? Verdict.Accept : Verdict.Decline;
        }
        if (sticky.Armed && sticky.Type == type && percent >= 0 && percent >= sticky.MinPercent)
            return Verdict.AcceptSticky;
        return Verdict.LeaveToClient;
    }

    /// <summary>
    /// "You determine that you have a 38 percent chance to succeed." -> 38 (cp1252 bytes, the
    /// string16's body). -1 when there is no "N percent"; over 100 reads as 100.
    /// </summary>
    public static int ParsePercent(ReadOnlySpan<byte> text)
    {
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
