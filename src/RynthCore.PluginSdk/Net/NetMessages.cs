// ============================================================================
//  RynthCore.PluginSdk - Net/NetMessages.cs
//  One entry point over the typed parsers: which message is this, did it parse, how
//  many bytes were left over, and a one-line summary. Used by the engine's
//  /rc netmsg log (to check the layouts against a live server) and by the replay runner.
// ============================================================================

using System;
using System.Globalization;

namespace RynthCore.PluginSdk.Net;

/// <summary>The result of <see cref="NetMessages.Inspect"/>.</summary>
public readonly record struct NetMessageInfo(string Kind, bool Known, bool Parsed, int Leftover, string Summary, object? Value);

public static class NetMessages
{
    /// <summary>
    /// Parses <paramref name="m"/> if the SDK knows it. Leftover is the bytes the layout
    /// didn't account for (non-zero means the layout and the server disagree).
    /// </summary>
    public static NetMessageInfo Inspect(ServerMessage m)
    {
        if (m.Opcode == ServerMessage.GameEventOpcode)
        {
            if (!m.IsGameEvent)
                return new("GameEvent(short)", true, false, m.Body.Length, "", null);
            return InspectEvent(m.EventType, m.EventBody);
        }

        ReadOnlySpan<byte> b = m.Body;
        switch (m.Opcode)
        {
            case ServerOpcode.HearSpeech:
            {
                bool ok = HearSpeech.TryParse(b, out var v, out int used);
                return Done("HearSpeech", ok, b, used, ok ? $"{v.SenderName} (0x{v.SenderId:X8}) type {v.ChatType}: {v.Text}" : "", v);
            }
            case ServerOpcode.HearRangedSpeech:
            {
                bool ok = HearRangedSpeech.TryParse(b, out var v, out int used);
                return Done("HearRangedSpeech", ok, b, used, ok ? $"{v.SenderName} (0x{v.SenderId:X8}) range {v.Range.ToString("0.#", CultureInfo.InvariantCulture)} type {v.ChatType}: {v.Text}" : "", v);
            }
            case ServerOpcode.HearEmote:
            case ServerOpcode.HearSoulEmote:
            {
                bool soul = m.Opcode == ServerOpcode.HearSoulEmote;
                bool ok = HearEmote.TryParse(b, soul, out var v, out int used);
                return Done(soul ? "HearSoulEmote" : "HearEmote", ok, b, used, ok ? $"{v.SenderName} (0x{v.SenderId:X8}): {v.Text}" : "", v);
            }
        }
        return new($"0x{m.Opcode:X4}", false, false, 0, "", null);
    }

    private static NetMessageInfo InspectEvent(uint type, ReadOnlySpan<byte> b)
    {
        switch (type)
        {
            case GameEventType.HearDirectSpeech:
            {
                bool ok = HearDirectSpeech.TryParse(b, out var v, out int used);
                return Done("HearDirectSpeech", ok, b, used, ok ? $"{v.SenderName} (0x{v.SenderId:X8}) -> 0x{v.TargetId:X8} type {v.ChatType}: {v.Text}" : "", v);
            }
            case GameEventType.ChannelBroadcast:
            {
                bool ok = ChannelBroadcast.TryParse(b, out var v, out int used);
                return Done("ChannelBroadcast", ok, b, used, ok ? $"channel {v.Channel} {v.SenderName}: {v.Text}" : "", v);
            }
            case GameEventType.PopUpString:
            case GameEventType.TransientString:
            {
                bool transient = type == GameEventType.TransientString;
                bool ok = SystemText.TryParse(b, transient, out var v, out int used);
                return Done(transient ? "TransientString" : "PopUpString", ok, b, used, ok ? v.Text : "", v);
            }
            case GameEventType.ConfirmationRequest:
            {
                bool ok = ConfirmationRequest.TryParse(b, out var v, out int used);
                return Done("ConfirmationRequest", ok, b, used, ok ? $"type {v.ConfirmationType} context 0x{v.ContextId:X8}: {v.Text}" : "", v);
            }
            case GameEventType.AllegianceLoginNotification:
            {
                bool ok = AllegianceLoginNotification.TryParse(b, out var v, out int used);
                return Done("AllegianceLoginNotification", ok, b, used, ok ? $"0x{v.CharacterId:X8} {(v.LoggedIn ? "logged in" : "logged out")}" : "", v);
            }
            case GameEventType.FriendsUpdate:
            {
                bool ok = FriendsUpdate.TryParse(b, out var v, out int used);
                return Done("FriendsUpdate", ok, b, used, ok ? $"type {v.UpdateType}, {v.Friends.Count} friend(s){FirstFriend(v)}" : "", v);
            }
            case GameEventType.FellowshipFullUpdate:
            {
                bool ok = FellowshipFullUpdate.TryParse(b, out var v, out int used);
                return Done("FellowshipFullUpdate", ok, b, used,
                    ok ? $"'{v.Name}' leader 0x{v.LeaderId:X8}, {v.Fellows.Count} member(s), shareXp={v.ShareXp} even={v.EvenXpSplit} open={v.Open} locked={v.Locked} tail={(v.TailParsed ? "ok" : "unread")}" : "", v);
            }
            case GameEventType.FellowshipUpdateFellow:
            {
                bool ok = FellowshipUpdateFellow.TryParse(b, out var v, out int used);
                return Done("FellowshipUpdateFellow", ok, b, used,
                    ok ? $"{v.Fellow.Name} (0x{v.Fellow.Id:X8}) L{v.Fellow.Level} hp {v.Fellow.Health}/{v.Fellow.MaxHealth} update {v.UpdateType}" : "", v);
            }
            case GameEventType.FellowshipQuit:
            case GameEventType.FellowshipDismiss:
            {
                bool dismissed = type == GameEventType.FellowshipDismiss;
                bool ok = FellowshipMemberLeft.TryParse(b, dismissed, out var v, out int used);
                return Done(dismissed ? "FellowshipDismiss" : "FellowshipQuit", ok, b, used, ok ? $"0x{v.FellowId:X8}" : "", v);
            }
            case GameEventType.FellowshipDisband:
                return Done("FellowshipDisband", true, b, 0, "", FellowshipDisband.Instance);
        }
        return new($"GameEvent 0x{type:X4}", false, false, 0, "", null);
    }

    private static string FirstFriend(FriendsUpdate v)
        => v.Friends.Count == 0 ? "" : $", first {v.Friends[0].Name} online={v.Friends[0].Online}";

    private static NetMessageInfo Done(string kind, bool ok, ReadOnlySpan<byte> body, int used, string summary, object? value)
        => new(kind, true, ok, ok ? body.Length - used : body.Length, summary, ok ? value : null);
}
