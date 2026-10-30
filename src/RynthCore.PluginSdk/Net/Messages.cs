// ============================================================================
//  RynthCore.PluginSdk - Net/Messages.cs
//  Typed, read-only parsers for server messages the engine doesn't already turn into
//  plugin events: chat with sender ids (speech, ranged speech, tells, emotes, channel
//  broadcasts), fellowship, friends, allegiance logins, confirmation prompts, and popup
//  and transient system text.
//
//  Layouts written for RynthCore from general knowledge of the AC wire format (field
//  order and sizes); no generated definitions or copied code. Each TryParse takes the
//  bytes after the opcode (top-level messages) or after the event type (game events),
//  never throws, and returns false for data that doesn't fit the layout. Consumed
//  says how many bytes the parse used, so a mismatch with the real server shows up as
//  leftover bytes (the engine's /rc netmsg log reports it).
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.PluginSdk.Net;

// ─── chat ───────────────────────────────────────────────────────────────────

/// <summary>0x02BB: local speech. Text, sender name, sender id, chat type.</summary>
public sealed record HearSpeech(string Text, string SenderName, uint SenderId, uint ChatType)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out HearSpeech msg) => TryParse(body, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, out HearSpeech msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        if (!r.String16L(out string text) || !r.String16L(out string name) ||
            !r.U32(out uint sender) || !r.U32(out uint type))
        {
            consumed = r.Position;
            return false;
        }
        msg = new HearSpeech(text, name, sender, type);
        consumed = r.Position;
        return true;
    }
}

/// <summary>0x02BC: ranged speech (shouts, NPC speech). Text, sender name, sender id, range, chat type.</summary>
public sealed record HearRangedSpeech(string Text, string SenderName, uint SenderId, float Range, uint ChatType)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out HearRangedSpeech msg) => TryParse(body, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, out HearRangedSpeech msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        if (!r.String16L(out string text) || !r.String16L(out string name) ||
            !r.U32(out uint sender) || !r.F32(out float range) || !r.U32(out uint type))
        {
            consumed = r.Position;
            return false;
        }
        msg = new HearRangedSpeech(text, name, sender, range, type);
        consumed = r.Position;
        return true;
    }
}

/// <summary>0x01E0 (emote) and 0x01E2 (soul emote): sender id, sender name, text.</summary>
public sealed record HearEmote(uint SenderId, string SenderName, string Text, bool Soul)
{
    public static bool TryParse(ReadOnlySpan<byte> body, bool soul, out HearEmote msg) => TryParse(body, soul, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, bool soul, out HearEmote msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        if (!r.U32(out uint sender) || !r.String16L(out string name) || !r.String16L(out string text))
        {
            consumed = r.Position;
            return false;
        }
        msg = new HearEmote(sender, name, text, soul);
        consumed = r.Position;
        return true;
    }
}

/// <summary>Game event 0x02BD: a tell. Text, sender name, sender id, target id, chat type, then a u32 of flags.</summary>
public sealed record HearDirectSpeech(string Text, string SenderName, uint SenderId, uint TargetId, uint ChatType, uint Flags)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out HearDirectSpeech msg) => TryParse(body, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, out HearDirectSpeech msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        if (!r.String16L(out string text) || !r.String16L(out string name) || !r.U32(out uint sender) ||
            !r.U32(out uint target) || !r.U32(out uint type) || !r.U32(out uint flags))
        {
            consumed = r.Position;
            return false;
        }
        msg = new HearDirectSpeech(text, name, sender, target, type, flags);
        consumed = r.Position;
        return true;
    }
}

/// <summary>Game event 0x0147: a chat channel line (allegiance, fellowship, ...). Channel id, sender name (empty for your own), text.</summary>
public sealed record ChannelBroadcast(uint Channel, string SenderName, string Text)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out ChannelBroadcast msg) => TryParse(body, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, out ChannelBroadcast msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        if (!r.U32(out uint channel) || !r.String16L(out string name) || !r.String16L(out string text))
        {
            consumed = r.Position;
            return false;
        }
        msg = new ChannelBroadcast(channel, name, text);
        consumed = r.Position;
        return true;
    }
}

/// <summary>Game events 0x0004 (popup box) and 0x02EB (transient text): one string.</summary>
public sealed record SystemText(string Text, bool Transient)
{
    public static bool TryParse(ReadOnlySpan<byte> body, bool transient, out SystemText msg) => TryParse(body, transient, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, bool transient, out SystemText msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        if (!r.String16L(out string text))
        {
            consumed = r.Position;
            return false;
        }
        msg = new SystemText(text, transient);
        consumed = r.Position;
        return true;
    }
}

/// <summary>Game event 0x0274: a yes/no prompt (fellowship invite, swear allegiance, crafting...). Type, context id, text.</summary>
public sealed record ConfirmationRequest(uint ConfirmationType, uint ContextId, string Text)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out ConfirmationRequest msg) => TryParse(body, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, out ConfirmationRequest msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        if (!r.U32(out uint type) || !r.U32(out uint context) || !r.String16L(out string text))
        {
            consumed = r.Position;
            return false;
        }
        msg = new ConfirmationRequest(type, context, text);
        consumed = r.Position;
        return true;
    }
}

// ─── allegiance, friends ────────────────────────────────────────────────────

/// <summary>Game event 0x027A: an allegiance member logged in or out. Character id, logged in (u32 flag).</summary>
public sealed record AllegianceLoginNotification(uint CharacterId, bool LoggedIn)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out AllegianceLoginNotification msg) => TryParse(body, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, out AllegianceLoginNotification msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        if (!r.U32(out uint id) || !r.Bool32(out bool on))
        {
            consumed = r.Position;
            return false;
        }
        msg = new AllegianceLoginNotification(id, on);
        consumed = r.Position;
        return true;
    }
}

/// <summary>One friends-list entry: id, online, appearing offline, name, and the two id lists the server keeps with it.</summary>
public sealed record FriendData(uint Id, bool Online, bool AppearOffline, string Name, uint[] OutFriends, uint[] InFriends);

/// <summary>Game event 0x0021: the friends list, whole or changed. A packed list of entries, then the update type.</summary>
public sealed record FriendsUpdate(IReadOnlyList<FriendData> Friends, uint UpdateType)
{
    public const uint Full = 0, Added = 1, Removed = 2, LoginChange = 4;

    public static bool TryParse(ReadOnlySpan<byte> body, out FriendsUpdate msg) => TryParse(body, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, out FriendsUpdate msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        bool ok = r.ListCount(out int n, 4 + 4 + 4 + 4 + 4 + 4);
        var list = new List<FriendData>(ok ? n : 0);
        for (int i = 0; ok && i < n; i++)
        {
            if (r.U32(out uint id) && r.Bool32(out bool online) && r.Bool32(out bool appearOffline) &&
                r.String16L(out string name) && r.U32List(out uint[] outs) && r.U32List(out uint[] ins))
                list.Add(new FriendData(id, online, appearOffline, name, outs, ins));
            else
                ok = false;
        }
        uint type = 0;
        ok = ok && r.U32(out type);
        consumed = r.Position;
        if (!ok) return false;
        msg = new FriendsUpdate(list, type);
        return true;
    }
}

// ─── fellowship ─────────────────────────────────────────────────────────────

/// <summary>A fellowship member: cached XP and luminance shares, level, max and current vitals, share-loot flag, name.</summary>
public sealed record Fellow(uint Id, string Name, uint Level, uint XpCached, uint LumCached,
    uint MaxHealth, uint MaxStamina, uint MaxMana, uint Health, uint Stamina, uint Mana, bool ShareLoot)
{
    /// <summary>Fellow body: 9 u32 (xp, lum, level, max h/s/m, current h/s/m), u32 share-loot flag, name.</summary>
    internal static bool Read(ref NetReader r, uint id, out Fellow fellow)
    {
        fellow = null!;
        if (!r.U32(out uint xp) || !r.U32(out uint lum) || !r.U32(out uint level) ||
            !r.U32(out uint maxH) || !r.U32(out uint maxS) || !r.U32(out uint maxM) ||
            !r.U32(out uint h) || !r.U32(out uint s) || !r.U32(out uint m) ||
            !r.Bool32(out bool shareLoot) || !r.String16L(out string name))
            return false;
        fellow = new Fellow(id, name, level, xp, lum, maxH, maxS, maxM, h, s, m, shareLoot);
        return true;
    }

    internal const int MinSize = 10 * 4 + 4;
}

/// <summary>
/// Game event 0x02BE: the whole fellowship. Members (packed table id to fellow), name,
/// leader id, then four u32 flags: share XP, even XP split, open, locked. After that the
/// server sends the recently departed members and the fellowship locks; those are read
/// when they fit (<see cref="TailParsed"/>), and the members are returned either way.
/// </summary>
public sealed record FellowshipFullUpdate(string Name, uint LeaderId, bool ShareXp, bool EvenXpSplit, bool Open, bool Locked,
    IReadOnlyList<Fellow> Fellows, IReadOnlyDictionary<uint, int> RecentlyDeparted, IReadOnlyList<string> LockNames, bool TailParsed)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out FellowshipFullUpdate msg) => TryParse(body, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, out FellowshipFullUpdate msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        bool ok = r.TableCount(out int n, 4 + Fellow.MinSize);
        var fellows = new List<Fellow>(ok ? n : 0);
        for (int i = 0; ok && i < n; i++)
        {
            if (r.U32(out uint id) && Fellow.Read(ref r, id, out Fellow f))
                fellows.Add(f);
            else
                ok = false;
        }
        string name = string.Empty;
        uint leader = 0;
        bool shareXp = false, even = false, open = false, locked = false;
        ok = ok && r.String16L(out name) && r.U32(out leader) &&
             r.Bool32(out shareXp) && r.Bool32(out even) && r.Bool32(out open) && r.Bool32(out locked);
        if (!ok)
        {
            consumed = r.Position;
            return false;
        }

        // Tail: recently departed (id -> i32), then locks (name -> 5 x u32 of lock data).
        int mainEnd = r.Position;
        var departed = new Dictionary<uint, int>();
        var locks = new List<string>();
        bool tail = r.TableCount(out int d, 8);
        for (int i = 0; tail && i < d; i++)
        {
            if (r.U32(out uint id) && r.I32(out int t))
                departed[id] = t;
            else
                tail = false;
        }
        int l = 0;
        tail = tail && r.TableCount(out l, 4 + 20);
        for (int i = 0; tail && i < l; i++)
        {
            if (r.String16L(out string lockName) && r.Skip(20))
                locks.Add(lockName);
            else
                tail = false;
        }
        if (!tail)
        {
            departed.Clear();
            locks.Clear();
        }
        consumed = tail ? r.Position : mainEnd;
        msg = new FellowshipFullUpdate(name, leader, shareXp, even, open, locked, fellows, departed, locks, tail);
        return true;
    }
}

/// <summary>Game event 0x02C0: one member changed. Fellow id, the fellow, update type (1 full, 2 stats, 3 vitals).</summary>
public sealed record FellowshipUpdateFellow(Fellow Fellow, uint UpdateType)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out FellowshipUpdateFellow msg) => TryParse(body, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, out FellowshipUpdateFellow msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        Fellow f = null!;
        uint type = 0;
        bool ok = r.U32(out uint id) && Fellow.Read(ref r, id, out f) && r.U32(out type);
        consumed = r.Position;
        if (!ok) return false;
        msg = new FellowshipUpdateFellow(f, type);
        return true;
    }
}

/// <summary>Game events 0x00A3 (a member quit) and 0x00A4 (a member was dismissed): the member's id.</summary>
public sealed record FellowshipMemberLeft(uint FellowId, bool Dismissed)
{
    public static bool TryParse(ReadOnlySpan<byte> body, bool dismissed, out FellowshipMemberLeft msg) => TryParse(body, dismissed, out msg, out _);

    public static bool TryParse(ReadOnlySpan<byte> body, bool dismissed, out FellowshipMemberLeft msg, out int consumed)
    {
        msg = null!;
        var r = new NetReader(body);
        bool ok = r.U32(out uint id);
        consumed = r.Position;
        if (!ok) return false;
        msg = new FellowshipMemberLeft(id, dismissed);
        return true;
    }
}

/// <summary>Game event 0x02BF: the fellowship was disbanded (no body).</summary>
public sealed record FellowshipDisband
{
    public static readonly FellowshipDisband Instance = new();
}
