// ============================================================================
//  RynthCore.Engine - UI/Data/Discord/ChatSpeech.cs
//  Player speech in a chat line: which channel it was said on (local, tell,
//  fellowship, the allegiance channels, the global chat channels), who said
//  it and what. The Discord relay uses it to pick lines finer than a chat tab
//  ("tells and fellowship only") and to print them as [Channel] Speaker: text.
//
//  Ported from RynthAi's translator (ChatTranslator.TryParseInbound) when the
//  relay moved into RynthChat (2026-10-11). Pure; covered by
//  tools/ChatDiscordTests.
// ============================================================================

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Engine.UI.Data.Discord;

/// <summary>Speech channels. Saved by name (<see cref="ChatSpeech.Name"/>), never by number.</summary>
internal enum SpeechChannel
{
    Local = 0,
    Tell,
    Fellowship,
    Allegiance,
    Patron,
    Vassals,
    Covassals,
    Monarch,
    General,
    Trade,
    Lfg,
    Roleplay,
    Society,
    Olthoi,
}

internal static class ChatSpeech
{
    public static readonly SpeechChannel[] All = Enum.GetValues<SpeechChannel>();

    /// <summary>AC's ChatMessageType for spell words ("Zojak Bor"): speech-shaped, never relayed as speech.</summary>
    public const int ChatTypeSpellcasting = 0x11;

    public static uint Bit(SpeechChannel ch) => 1u << (int)ch;

    public static string Name(SpeechChannel ch) => ch switch
    {
        SpeechChannel.Lfg => "LFG",
        _ => ch.ToString(),
    };

    /// <summary>Accepts channel names and the usual short forms (case-insensitive).</summary>
    public static bool TryParseName(string? text, out SpeechChannel ch)
    {
        ch = SpeechChannel.Local;
        if (string.IsNullOrWhiteSpace(text)) return false;
        switch (text.Trim().ToLowerInvariant())
        {
            case "local": case "say": case "area": ch = SpeechChannel.Local; return true;
            case "tell": case "tells": case "t": ch = SpeechChannel.Tell; return true;
            case "fellowship": case "fellow": case "f": ch = SpeechChannel.Fellowship; return true;
            case "allegiance": case "alleg": case "a": ch = SpeechChannel.Allegiance; return true;
            case "patron": case "p": ch = SpeechChannel.Patron; return true;
            case "vassals": case "vassal": case "v": ch = SpeechChannel.Vassals; return true;
            case "covassals": case "covassal": case "c": ch = SpeechChannel.Covassals; return true;
            case "monarch": case "m": ch = SpeechChannel.Monarch; return true;
            case "general": case "cg": case "g": ch = SpeechChannel.General; return true;
            case "trade": case "ct": ch = SpeechChannel.Trade; return true;
            case "lfg": case "looking for group": case "clfg": ch = SpeechChannel.Lfg; return true;
            case "roleplay": case "rp": case "crp": ch = SpeechChannel.Roleplay; return true;
            case "society": case "cs": ch = SpeechChannel.Society; return true;
            case "olthoi": case "co": ch = SpeechChannel.Olthoi; return true;
            default: return false;
        }
    }

    /// <summary>Channel implied by an inbound chat type; null for generic types (Channels / Social).</summary>
    public static SpeechChannel? FromChatType(int chatType) => chatType switch
    {
        0x02 => SpeechChannel.Local,
        0x03 => SpeechChannel.Tell,
        0x12 => SpeechChannel.Allegiance,
        0x13 => SpeechChannel.Fellowship,
        0x1B => SpeechChannel.General,
        0x1C => SpeechChannel.Trade,
        0x1D => SpeechChannel.Lfg,
        0x1E => SpeechChannel.Roleplay,
        0x20 => SpeechChannel.Olthoi,
        0x21 => SpeechChannel.Society,
        _ => null,
    };

    // "[General] " style tab prefix some servers / chat options put in front of channel lines.
    private static readonly Regex RxBracket = new(@"^\[(?<b>[A-Za-z ]{1,24})\]\s*", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(20));

    // "You say ...", "You tell Bob, ..." - the server echo of your own lines.
    private static readonly Regex RxOwn = new(@"^You\s+(?:say|tell)\b(?<mid>[^""]*)""(?<msg>.*)""\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

    // "Bob says, "...""  /  "Bob says on the General channel, "...""  /  "Bob tells you, "...""
    private static readonly Regex RxSpeech = new(@"^(?<name>.+?)\s+(?<verb>says|tells)\b(?<mid>[^""]*)""(?<msg>.*)""\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

    private static readonly Regex RxOnChannel = new(@"on the (?<ch>[A-Za-z ]+?) channel",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

    private static readonly Regex RxEscape = new(@"<([0-9a-fA-F]{4})>", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(20));

    /// <summary>
    /// Splits a chat line (AC's name links already stripped) into channel, speaker and message.
    /// The channel comes from, in order: a "[Name]" prefix, "on the X channel", "tells you", the
    /// chat type, then words like "patron" / "vassal". False for anything that isn't player speech,
    /// and for spell words.
    /// </summary>
    public static bool TryParse(string? text, int chatType, out SpeechChannel channel,
        out string speaker, out string message, out bool own)
    {
        channel = SpeechChannel.Local;
        speaker = message = string.Empty;
        own = false;
        if (chatType == ChatTypeSpellcasting || string.IsNullOrEmpty(text)) return false;

        string t = text.Trim();
        if (t.Length < 6 || t.IndexOf('"') < 0) return false;
        try
        {
            SpeechChannel? hint = null;
            var b = RxBracket.Match(t);
            if (b.Success && TryParseName(b.Groups["b"].Value, out var bc))
            {
                hint = bc;
                t = t.Substring(b.Length);
            }

            string mid;
            bool tellsYou;
            var mo = RxOwn.Match(t);
            if (mo.Success)
            {
                own = true;
                speaker = "You";
                mid = mo.Groups["mid"].Value;
                message = mo.Groups["msg"].Value;
                tellsYou = t.StartsWith("You tell", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var m = RxSpeech.Match(t);
                if (!m.Success) return false;
                speaker = m.Groups["name"].Value.Trim().TrimStart('+');
                mid = m.Groups["mid"].Value;
                message = m.Groups["msg"].Value;
                tellsYou = m.Groups["verb"].Value == "tells" && mid.TrimStart().StartsWith("you", StringComparison.OrdinalIgnoreCase);
                if (speaker.Length == 0 || speaker.Length > 40) return false;
            }

            SpeechChannel? ch = hint;
            if (ch == null)
            {
                var oc = RxOnChannel.Match(mid);
                if (oc.Success && TryParseName(oc.Groups["ch"].Value, out var named)) ch = named;
            }
            if (ch == null && tellsYou) ch = SpeechChannel.Tell;
            ch ??= FromChatType(chatType);
            ch ??= FromWords(mid);
            if (ch == null) return false;

            channel = ch.Value;
            message = Decode(message).Trim();
            return message.Length > 0;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static SpeechChannel? FromWords(string mid)
    {
        string m = mid.ToLowerInvariant();
        if (m.Contains("covassal")) return SpeechChannel.Covassals;
        if (m.Contains("vassal")) return SpeechChannel.Vassals;
        if (m.Contains("patron")) return SpeechChannel.Patron;
        if (m.Contains("monarch")) return SpeechChannel.Monarch;
        if (m.Contains("fellowship")) return SpeechChannel.Fellowship;
        if (m.Contains("allegiance")) return SpeechChannel.Allegiance;
        return null;
    }

    /// <summary>
    /// Expands AC's "&lt;c2dc&gt;" escapes (characters above Latin-1 travel as UTF-16 hex in chat)
    /// into the characters they stand for.
    /// </summary>
    public static string Decode(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text ?? string.Empty;
        try
        {
            return RxEscape.Replace(text, m =>
            {
                int cp = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return cp is > 0 and <= 0xFFFF ? ((char)cp).ToString() : m.Value;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }
}
