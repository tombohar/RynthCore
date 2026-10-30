// ============================================================================
//  RynthCore.Engine - UI/Data/ChatLineColors.cs
//  RynthChat's per-kind line colours (2026-10-08): the user can pick a colour
//  for each kind of line - say, tells in and out, allegiance, fellowship,
//  emotes, each Turbine chat channel by its "[Tag]" (General is /cg, Trade
//  /ct, LFG /clfg, Roleplay /crp, Society /soc, Olthoi /o; the client's own
//  @help text) - and for each channel tab.
//
//  A line's colour, first that applies:
//    1. a rule's line colour (ChatRoute.LineArgb),
//    2. the colour the user set for the line's kind,
//    3. the colour the user set for its tab,
//    4. with Retail colours on (2026-10-28, the default), AC's own colour for
//       the line's ChatMessageType: magic blue, combat red, tells yellow...;
//       not for the Rynth and Items tabs (RynthCore's own lines),
//    5. the tab's built-in colour (what every line looked like before).
//  With nothing set and Retail colours off, every line looks exactly as before.
//
//  Kept apart from ChatData.cs (no engine state) so tools/ChatFilterTests can
//  compile it in. The colours are an immutable snapshot, replaced on every
//  edit: the faces read it every frame from AC's thread, the settings popup
//  writes it on the same thread, and the loader writes it once at startup.
//  Saved as "lineColors" in rynthchat_settings.json ({ key: "#RRGGBB" }),
//  only the colours that were set; absent in every file before 2026-10-08.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Engine.UI.Data;

/// <summary>One colour setting: its key (in the settings file), label, a tooltip, and the tab its lines sit in.</summary>
internal readonly record struct ChatColorKind(string Key, string Label, string Hint, string Tab);

internal static class ChatLineColors
{
    public const string Say = "say", Tell = "tell", TellOut = "tellOut",
        Allegiance = "allegiance", Fellowship = "fellowship", Emote = "emote";
    public const string ChanPrefix = "chan:", TabPrefix = "tab:";
    /// <summary>Longest "[Tag]" taken as a channel; the same bound as ChatClassifier's sender regex.</summary>
    public const int MaxTagLength = 40;

    /// <summary>Channel tabs that have a colour (All has none); must match ChatModel.BaseTabs.</summary>
    public static readonly string[] Tabs = { "Chat", "Channels", "System", "Combat", "Rynth", "Items", "Other" };

    /// <summary>The player-to-player kinds (ChatMessageType).</summary>
    public static readonly ChatColorKind[] ChatKinds =
    {
        new(Say,        "Say",              "Local speech (0x02)",                          "Chat"),
        new(Tell,       "Tells to you",     "Tells you receive (0x03)",                     "Chat"),
        new(TellOut,    "Tells you send",   "\"You tell ...\" (0x04)",                      "Chat"),
        new(Allegiance, "Allegiance",       "/a, and [Allegiance] channel lines (0x12)",    "Chat"),
        new(Fellowship, "Fellowship",       "/f (0x13)",                                    "Chat"),
        new(Emote,      "Emotes",           "/e and soul emotes (0x0A-0x0C)",               "Chat"),
    };

    /// <summary>The Turbine chat channels, by the tag AC prints in front of their lines.</summary>
    public static readonly ChatColorKind[] ChannelKinds =
    {
        new(ChanPrefix + "General",  "General",  "[General]: /general, /cg",  "Channels"),
        new(ChanPrefix + "Trade",    "Trade",    "[Trade]: /trade, /ct",      "Channels"),
        new(ChanPrefix + "LFG",      "LFG",      "[LFG]: /lfg, /clfg",        "Channels"),
        new(ChanPrefix + "Roleplay", "Roleplay", "[Roleplay]: /roleplay, /crp", "Channels"),
        new(ChanPrefix + "Society",  "Society",  "[Society]: /society, /soc", "Channels"),
        new(ChanPrefix + "Olthoi",   "Olthoi",   "[Olthoi]: /olthoi, /o",     "Channels"),
    };

    /// <summary>The tab colours, for lines with no kind of their own (system, combat...).</summary>
    public static readonly ChatColorKind[] TabKinds = Array.ConvertAll(Tabs,
        t => new ChatColorKind(TabPrefix + t, t, $"Lines in the {t} tab with no colour of their own", t));

    /// <summary>A kind or tab's colour as it shows now: the user's, else its default.</summary>
    public static uint EffectiveArgb(ChatColorKind kind) =>
        Get(kind.Key) ?? (kind.Key.StartsWith(TabPrefix, StringComparison.Ordinal) ? BuiltInTabArgb(kind.Tab) : DefaultArgb(kind));

    // ── Retail colours (2026-10-28) ──────────────────────────────────────

    /// <summary>
    /// AC's own chat colour for each ChatMessageType 0x00-0x21, as 0xAARRGGBB: the retail client's
    /// table, taken from OpenAC (AcDream.Runtime.Chat.RuntimeChatColors, MIT licence). 0x06/0x15
    /// combat red, 0x16 your hits on you light red, 0x07/0x11 magic and spellcasting light blue,
    /// 0x03 tells yellow, 0x05 system purple, 0x12 allegiance orange, 0x08/0x09 channels pink.
    /// </summary>
    private static readonly uint[] RetailArgbByType =
    {
        0xFF80FF7F, 0xFF80FF7F, 0xFFFFFFFF, 0xFFFFFF3F,   // 0x00 default, 0x01 all, 0x02 speech, 0x03 tell
        0xFFD2D264, 0xFFFF7FFF, 0xFFFF3F3F, 0xFF3FBFFF,   // 0x04 tell sent, 0x05 system, 0x06 combat, 0x07 magic
        0xFFFF9696, 0xFFFF9696, 0xFFFFFF3F, 0xFFD2D264,   // 0x08 channel, 0x09 channel sent, 0x0A social, 0x0B social sent
        0xFFD2D2C8, 0xFF3FDCDC, 0xFFB4DCF0, 0xFFFF3F3F,   // 0x0C emote, 0x0D advancement, 0x0E abuse, 0x0F help
        0xFF80FF7F, 0xFF3FBFFF, 0xFFEE921E, 0xFFFFFF3F,   // 0x10 appraisal, 0x11 spellcasting, 0x12 allegiance, 0x13 fellowship
        0xFF80FF7F, 0xFFFF3F3F, 0xFFF57572, 0xFF80FF7F,   // 0x14 world broadcast, 0x15 combat enemy, 0x16 combat self, 0x17 recall
        0xFF80FF7F, 0xFF80FF7F, 0xFFFF0000, 0xFFB4DCF0,   // 0x18 craft, 0x19 salvaging, 0x1A, 0x1B
        0xFFB4DCF0, 0xFFB4DCF0, 0xFFB4DCF0, 0xFFFFFF3F,   // 0x1C, 0x1D, 0x1E, 0x1F admin tell
        0xFFB4DCF0, 0xFFEE921E,                           // 0x20, 0x21
    };

    private static volatile bool _retail = true;

    /// <summary>Lines take AC's own colour for their kind (on unless the settings file says off).</summary>
    public static bool RetailColors { get => _retail; set => _retail = value; }

    /// <summary>AC's own colour for <paramref name="chatType"/>, or null for a type it has none for (-1: unknown).</summary>
    public static uint? RetailArgb(int chatType) =>
        chatType >= 0 && chatType < RetailArgbByType.Length ? RetailArgbByType[chatType] : null;

    /// <summary>The tabs of RynthCore's own lines (plugins' "[RynthAi] ..." and the item info), which keep their colour.</summary>
    private static bool IsOwnTab(string tab) =>
        string.Equals(tab, "Rynth", StringComparison.OrdinalIgnoreCase) || string.Equals(tab, "Items", StringComparison.OrdinalIgnoreCase);

    private static uint? RetailFor(int chatType, string tab) => _retail && !IsOwnTab(tab) ? RetailArgb(chatType) : null;

    /// <summary>The ChatMessageType a kind's lines come as, for showing its unset colour (the emotes: 0x0C, channels: 0x08).</summary>
    private static int TypeOfKind(string key) => key switch
    {
        Say => 0x02, Tell => 0x03, TellOut => 0x04, Allegiance => 0x12, Fellowship => 0x13, Emote => 0x0C,
        _ => key.StartsWith(ChanPrefix, StringComparison.Ordinal) ? 0x08 : -1,
    };

    /// <summary>A tab's colour before anything is set, as 0xAARRGGBB (ChatModel.ChannelArgb until 2026-10-08).</summary>
    public static uint BuiltInTabArgb(string tab) => tab switch
    {
        "Chat"     => 0xFFE0E0E0,
        "Channels" => 0xFF7AB8F5,
        "System"   => 0xFF8CA6BF,
        "Combat"   => 0xFFD93333,
        "Rynth"    => 0xFFE6B450,   // amber, distinct from the blue/grey channels
        "Items"    => 0xFF9FD8C8,   // pale teal: RynthAi's item info (2026-10-09)
        _          => 0xFFAAAAAA,
    };

    // ── Which kind a line is ─────────────────────────────────────────────

    /// <summary>
    /// The channel tag a line starts with: "[General] Bob says, ..." gives
    /// "General". Null when there is none, it's empty, too long, not followed
    /// by a space, or is a Rynth plugin's own "[Rynth…]" prefix.
    /// </summary>
    public static string? TagOf(string? text)
    {
        if (text == null || text.Length < 4 || text[0] != '[') return null;
        int end = text.IndexOf(']', 1);
        if (end <= 1 || end - 1 > MaxTagLength || end + 1 >= text.Length || text[end + 1] != ' ') return null;
        string tag = text.Substring(1, end - 1).Trim();
        if (tag.Length == 0 || tag.IndexOf('[') >= 0) return null;
        if (tag.StartsWith("Rynth", StringComparison.OrdinalIgnoreCase)) return null;
        return tag;
    }

    /// <summary>
    /// The colour key of a line (one of the ChatKinds keys or "chan:Tag"), or
    /// null when it has no kind of its own and takes its tab's colour.
    /// <paramref name="chatType"/> is AC's ChatMessageType, -1 from RynthChat before 0.2.0.
    /// </summary>
    public static string? KindOf(int chatType, string channel, string text)
    {
        switch (chatType)
        {
            case 0x02: return Say;
            case 0x03: return Tell;
            case 0x04: return TellOut;
            case 0x0A or 0x0B or 0x0C: return Emote;
            case 0x13: return Fellowship;
            case 0x12: return TagKind(TagOf(text)) ?? Allegiance;
            case 0x08 or 0x09: return TagKind(TagOf(text));
            case -1 when string.Equals(channel, "Channels", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(channel, "Chat", StringComparison.OrdinalIgnoreCase):
                return TagKind(TagOf(text));
            default: return null;
        }
    }

    /// <summary>"Allegiance" is the allegiance kind (the /a lines, tagged or not); the
    /// three societies' rooms share Society; any other tag is its own channel.</summary>
    private static string? TagKind(string? tag)
    {
        if (tag == null) return null;
        if (tag.Equals("Allegiance", StringComparison.OrdinalIgnoreCase)) return Allegiance;
        if (tag.Equals("Celestial Hand", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("Eldrytch Web", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("Radiant Blood", StringComparison.OrdinalIgnoreCase)) return ChanPrefix + "Society";
        foreach (ChatColorKind k in ChannelKinds)   // the listed spelling, whatever case AC used
            if (string.Equals(k.Key, ChanPrefix + tag, StringComparison.OrdinalIgnoreCase)) return k.Key;
        return ChanPrefix + tag;
    }

    /// <summary>True for "chan:Tag" keys that aren't one of the listed channels.</summary>
    public static bool IsOtherChannel(string? kind)
    {
        if (kind == null || !kind.StartsWith(ChanPrefix, StringComparison.Ordinal)) return false;
        foreach (ChatColorKind k in ChannelKinds)
            if (string.Equals(k.Key, kind, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // ── Other channel tags seen this session (offered in the settings) ───

    private const int MaxSeenTags = 24;
    private static readonly object SeenLock = new();
    private static string[] _seenTags = Array.Empty<string>();

    /// <summary>Tags other than the listed channels seen on channel lines this session, oldest first.</summary>
    public static string[] SeenTags => Volatile.Read(ref _seenTags);

    /// <summary>A real chat line's kind (ChatLine): an unlisted channel's tag is remembered for the settings.</summary>
    public static void NoteKind(string? kind)
    {
        if (IsOtherChannel(kind)) NoteTag(kind![ChanPrefix.Length..]);
    }

    private static void NoteTag(string tag)
    {
        string[] seen = Volatile.Read(ref _seenTags);
        foreach (string s in seen)
            if (string.Equals(s, tag, StringComparison.OrdinalIgnoreCase)) return;
        lock (SeenLock)
        {
            seen = _seenTags;
            if (seen.Length >= MaxSeenTags) return;
            foreach (string s in seen)
                if (string.Equals(s, tag, StringComparison.OrdinalIgnoreCase)) return;
            var next = new string[seen.Length + 1];
            seen.CopyTo(next, 0);
            next[^1] = tag;
            Volatile.Write(ref _seenTags, next);
        }
    }

    // ── The colours the user set ─────────────────────────────────────────

    private static readonly object EditLock = new();
    private static Dictionary<string, uint> _set = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The colour set for <paramref name="key"/> (a kind, "chan:Tag" or "tab:Name"), or null.</summary>
    public static uint? Get(string? key)
    {
        if (key == null) return null;
        return Volatile.Read(ref _set).TryGetValue(key, out uint v) ? v : null;
    }

    public static bool AnySet => Volatile.Read(ref _set).Count > 0;

    /// <summary>The keys that have a colour, e.g. so other channels' colours are listed after a relaunch.</summary>
    public static IReadOnlyCollection<string> SetKeys => Volatile.Read(ref _set).Keys;

    public static void Set(string key, uint argb) => Edit(d => d[key] = argb | 0xFF000000);

    public static void Reset(string key) => Edit(d => d.Remove(key));

    public static void ResetAll() => Edit(d => d.Clear());

    private static void Edit(Action<Dictionary<string, uint>> change)
    {
        lock (EditLock)
        {
            var next = new Dictionary<string, uint>(_set, StringComparer.OrdinalIgnoreCase);
            change(next);
            Volatile.Write(ref _set, next);
        }
    }

    /// <summary>A tab's colour: the user's, else the built-in one. 0xAARRGGBB.</summary>
    public static uint TabArgb(string tab) => Get(TabPrefix + tab) ?? BuiltInTabArgb(tab);

    /// <summary>What a kind looks like while it has no colour of its own: its tab's colour if the user
    /// set one, else AC's own (Retail colours on), else the tab's built-in colour.</summary>
    public static uint DefaultArgb(ChatColorKind kind) =>
        Get(TabPrefix + kind.Tab) ?? RetailFor(TypeOfKind(kind.Key), kind.Tab) ?? BuiltInTabArgb(kind.Tab);

    /// <summary>A line's colour; see the header for the order. <paramref name="chatType"/> is AC's
    /// ChatMessageType (-1 unknown: no retail colour).</summary>
    public static uint Resolve(uint? ruleArgb, string? kind, string channel, int chatType = -1) =>
        ruleArgb ?? Get(kind) ?? Get(TabPrefix + channel) ?? RetailFor(chatType, channel) ?? BuiltInTabArgb(channel);

    /// <summary>A key worth keeping from the settings file: a listed kind, a base tab, or a sane channel tag.</summary>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        foreach (ChatColorKind k in ChatKinds)
            if (string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
        if (key.StartsWith(TabPrefix, StringComparison.OrdinalIgnoreCase))
            return Array.Exists(Tabs, t => string.Equals(TabPrefix + t, key, StringComparison.OrdinalIgnoreCase));
        if (key.StartsWith(ChanPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string tag = key[ChanPrefix.Length..];
            return tag.Length > 0 && tag.Length <= MaxTagLength && tag.Trim() == tag
                && tag.IndexOf('[') < 0 && tag.IndexOf(']') < 0;
        }
        return false;
    }

    // ── Settings file ────────────────────────────────────────────────────

    /// <summary>The colours set, for "lineColors"; null when none (the key is then left out of the file).</summary>
    public static Dictionary<string, string>? ToJson()
    {
        Dictionary<string, uint> set = Volatile.Read(ref _set);
        if (set.Count == 0) return null;
        var json = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in set) json[kv.Key] = FormatColor(kv.Value);
        return json;
    }

    /// <summary>Replaces the colours with the file's; null (an older file) clears them. Bad entries are skipped.</summary>
    public static void Load(Dictionary<string, string>? json)
    {
        var next = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (json != null)
            foreach (var kv in json)
            {
                if (!IsValidKey(kv.Key)) continue;
                uint? argb = ParseColor(kv.Value);
                if (argb != null) next[CanonicalKey(kv.Key)] = argb.Value | 0xFF000000;
            }
        lock (EditLock) Volatile.Write(ref _set, next);
    }

    /// <summary>A listed kind or tab in its listed spelling ("Say" -> "say", "tab:chat" -> "tab:Chat").</summary>
    private static string CanonicalKey(string key)
    {
        foreach (ChatColorKind k in ChatKinds)
            if (string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)) return k.Key;
        foreach (ChatColorKind k in ChannelKinds)
            if (string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)) return k.Key;
        foreach (string t in Tabs)
            if (string.Equals(TabPrefix + t, key, StringComparison.OrdinalIgnoreCase)) return TabPrefix + t;
        return key;
    }

    /// <summary>
    /// "#RRGGBB" or "#AARRGGBB" to 0xAARRGGBB (opaque when no alpha is given).
    /// An alpha of 00 is taken as opaque: a colour that makes the text invisible
    /// is never what a rule wants (hiding is the Hide action).
    /// </summary>
    public static uint? ParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('#');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length is not (6 or 8)) return null;
        if (!uint.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint v)) return null;
        if (s.Length == 6 || (v >> 24) == 0) v |= 0xFF000000;
        return v;
    }

    /// <summary>0xAARRGGBB as "#RRGGBB", or "#AARRGGBB" when it isn't opaque (an imported colour keeps its alpha).</summary>
    public static string FormatColor(uint argb) =>
        (argb >> 24) == 0xFF ? $"#{argb & 0xFFFFFF:X6}" : $"#{argb:X8}";
}
