// ============================================================================
//  RynthCore.Engine - UI/Data/Discord/ChatDiscordSettings.cs
//  RynthChat's Discord relay settings, shared by every client and character
//  (%AppData%\RynthCore\rynthchat_discord.json, beside rynthchat_settings.json).
//  The routes and options are shared; the switch is per character
//  (Characters): a character not on the list relays nothing, and the list
//  starts empty. Saves merge each client's switch into the file's list.
//
//  Threads: the Discord panel (AC's render thread) edits fields in place, each
//  a single reference or 32-bit value, so the pump's reads never tear. Arrays
//  (Routes, a route's Tabs) are replaced, never changed in place, so the pump
//  can walk the one it read without a lock.
//
//  Secrets (webhook URL, bot token) are stored only as DPAPI blobs
//  (DiscordSecrets); the JSON never holds them in plain text.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Engine.UI.Data.Discord;

/// <summary>Route state the panel shows; replaced as a whole by the pump.</summary>
internal sealed record DiscordRouteStatus(int Queued, int MessagesSent, int LinesSent, int Dropped, bool Paused,
    string LastError, DateTime LastSent);

/// <summary>One Discord destination and the chat it receives.</summary>
internal sealed class DiscordRoute
{
    public const string ModeWebhook = "webhook";
    public const string ModeBot = "bot";
    public static readonly string[] Modes = { ModeWebhook, ModeBot };
    public static readonly string[] ModeNames = { "Webhook URL", "Bot token + channel ID" };

    /// <summary>Label shown in the panel, the log and /rc discord.</summary>
    public string Name = "Discord";

    public bool Enabled = true;

    public string Mode = ModeWebhook;

    /// <summary>DPAPI blob of the webhook URL (the URL embeds the webhook token).</summary>
    public string WebhookProtected = string.Empty;

    /// <summary>DPAPI blob of the bot token (bot mode).</summary>
    public string BotTokenProtected = string.Empty;

    /// <summary>Target channel snowflake (bot mode). Not secret.</summary>
    public string ChannelId = string.Empty;

    /// <summary>Webhook display name; blank uses the character's name. Ignored in bot mode.</summary>
    public string DisplayName = string.Empty;

    /// <summary>Speech channels relayed (names, <see cref="ChatSpeech.Name"/>). Saved form of <see cref="SpeechMask"/>.</summary>
    public string[]? Speech;

    /// <summary>
    /// Chat tabs relayed: every line that shows in one of them (channel tabs like System, or
    /// tabs the chat rules fill). Replaced, never changed in place.
    /// </summary>
    public string[] Tabs = Array.Empty<string>();

    /// <summary>Comma-separated words; when set, only lines containing one of them are relayed.</summary>
    public string Keywords = string.Empty;

    /// <summary>Use this route's own pacing instead of the relay's.</summary>
    public bool CustomPace;

    /// <summary>With <see cref="CustomPace"/>: lines arriving within this window go out in one message.</summary>
    public int BatchMs = 1500;

    /// <summary>With <see cref="CustomPace"/>: minimum spacing between this route's messages.</summary>
    public int MinIntervalMs = 2000;

    // ── Runtime only ─────────────────────────────────────────────────────

    /// <summary>Bits of <see cref="SpeechChannel"/> (<see cref="ChatSpeech.Bit"/>).</summary>
    [JsonIgnore] public uint SpeechMask;

    /// <summary>Written by the pump, read by the panel.</summary>
    [JsonIgnore] public volatile DiscordRouteStatus? Status;

    /// <summary>True when a send secret is stored for the current mode (the plain text is never kept here).</summary>
    [JsonIgnore]
    public bool HasSecret => Mode == ModeBot
        ? BotTokenProtected.Length > 0 && ChannelId.Length > 0
        : WebhookProtected.Length > 0;

    public bool RelaysSpeech(SpeechChannel ch) => (SpeechMask & ChatSpeech.Bit(ch)) != 0;

    /// <summary>Tells, fellowship and the allegiance channels: private chat a player usually wants on their phone.</summary>
    public static uint DefaultSpeechMask()
    {
        uint m = 0;
        foreach (var ch in new[]
                 {
                     SpeechChannel.Tell, SpeechChannel.Fellowship, SpeechChannel.Allegiance,
                     SpeechChannel.Patron, SpeechChannel.Vassals, SpeechChannel.Covassals, SpeechChannel.Monarch,
                 })
            m |= ChatSpeech.Bit(ch);
        return m;
    }

    public static uint AllSpeechMask()
    {
        uint m = 0;
        foreach (var ch in ChatSpeech.All) m |= ChatSpeech.Bit(ch);
        return m;
    }

    public static string[] NamesFromMask(uint mask)
    {
        var names = new List<string>();
        foreach (var ch in ChatSpeech.All)
            if ((mask & ChatSpeech.Bit(ch)) != 0) names.Add(ChatSpeech.Name(ch));
        return names.ToArray();
    }

    /// <summary>The mask for <paramref name="names"/>; <paramref name="fallback"/> when there is no list at all.</summary>
    public static uint MaskFromNames(IEnumerable<string>? names, uint fallback)
    {
        if (names == null) return fallback;
        uint m = 0;
        foreach (string n in names)
            if (ChatSpeech.TryParseName(n, out var ch)) m |= ChatSpeech.Bit(ch);
        return m;
    }
}

/// <summary>A character the relay is switched on for (server + name, any case).</summary>
internal sealed class DiscordCharacter
{
    public string Server = string.Empty;
    public string Name = string.Empty;

    /// <summary>The character, or null without a name (not logged in yet).</summary>
    public static DiscordCharacter? Of(string? server, string? name)
    {
        string n = (name ?? string.Empty).Trim().TrimStart('+').Trim();
        if (n.Length == 0) return null;
        return new DiscordCharacter { Server = (server ?? string.Empty).Trim(), Name = n };
    }

    public bool Same(DiscordCharacter? other)
        => other != null
           && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Server, other.Server, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Bob (Aelrynth)", or "Bob" when the server isn't known.</summary>
    public override string ToString() => Server.Length > 0 ? $"{Name} ({Server})" : Name;
}

/// <summary>Turns the relay on or off for one character; applied to the file's list when saving.</summary>
internal sealed record DiscordCharacterOp(DiscordCharacter Character, bool On);

internal sealed class ChatDiscordSettings
{
    /// <summary>2: the relay is switched on per character (<see cref="Characters"/>), not for everyone.</summary>
    public const int CurrentVersion = 2;

    public int Version = CurrentVersion;

    /// <summary>
    /// The characters the relay is on for; every other character relays nothing. Each client
    /// switches only its own character, and a save merges that change into the file's list
    /// (ChatDiscordStore.MergeForSave), so clients don't undo each other. Replaced, never
    /// changed in place. Empty (the default): off for everyone.
    /// </summary>
    public DiscordCharacter[] Characters = Array.Empty<DiscordCharacter>();

    /// <summary>Destinations, shared by every character. Replaced (copy-on-write) when one is added or removed.</summary>
    public DiscordRoute[] Routes = Array.Empty<DiscordRoute>();

    /// <summary>True when the relay is switched on for <paramref name="character"/>.</summary>
    public bool IsOnFor(DiscordCharacter? character)
    {
        if (character == null) return false;
        foreach (var c in Characters) if (c.Same(character)) return true;
        return false;
    }

    /// <summary>Leave out lines you typed yourself ("You say", "You tell").</summary>
    public bool SkipOwn;

    /// <summary>Leave out lines a chat rule hides.</summary>
    public bool SkipHidden = true;

    /// <summary>Start each relayed line with the game time (HH:mm:ss).</summary>
    public bool IncludeTimestamp = true;

    /// <summary>Start each relayed line with "[Character]" (useful when several characters share a channel).</summary>
    public bool TagCharacter;

    /// <summary>Lines arriving within this window go out in one Discord message.</summary>
    public int BatchMs = 1500;

    /// <summary>Minimum spacing between messages to one route (Discord allows about 30 per minute per channel).</summary>
    public int MinIntervalMs = 2000;

    /// <summary>Lines waiting per route; the oldest are dropped beyond this.</summary>
    public int MaxQueue = 200;

    /// <summary>Entries kept in the panel's log.</summary>
    public int MaxLog = 200;

    // Pacing limits, shared by the panel's sliders and Normalize.
    public const int BatchMinMs = 0, BatchMaxMs = 60_000;
    public const int IntervalMinMs = 1000, IntervalMaxMs = 600_000;
    public const int QueueMin = 20, QueueMax = 2000;
    public const int LogMin = 20, LogMax = 2000;

    /// <summary>The batch window <paramref name="route"/> uses.</summary>
    public int BatchFor(DiscordRoute route) => route.CustomPace ? route.BatchMs : BatchMs;

    /// <summary>The message spacing <paramref name="route"/> uses.</summary>
    public int IntervalFor(DiscordRoute route) => route.CustomPace ? route.MinIntervalMs : MinIntervalMs;
}

/// <summary>Loads / saves <see cref="ChatDiscordSettings"/>.</summary>
internal static class ChatDiscordStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthchat_discord.json");

    /// <summary>
    /// Reads the settings (or the defaults: off, no routes). <paramref name="note"/> says what
    /// happened, for the log. Never throws.
    /// </summary>
    public static ChatDiscordSettings Load(string path, out string note)
    {
        note = string.Empty;
        ChatDiscordSettings? s = null;
        try
        {
            if (File.Exists(path))
                s = JsonSerializer.Deserialize(File.ReadAllText(path), ChatDiscordJsonContext.Default.ChatDiscordSettings);
        }
        catch (Exception ex)
        {
            note = $"settings unreadable ({ex.GetType().Name}: {ex.Message}); using defaults";
            KeepUnreadable(path);
            s = null;
        }
        s ??= new ChatDiscordSettings();
        Normalize(s);
        return s;
    }

    /// <summary>The settings as JSON (speech names refreshed from the masks first). Any thread.</summary>
    public static string Serialize(ChatDiscordSettings s)
    {
        foreach (var r in s.Routes) r.Speech = DiscordRoute.NamesFromMask(r.SpeechMask);
        return JsonSerializer.Serialize(s, ChatDiscordJsonContext.Default.ChatDiscordSettings);
    }

    /// <summary>Writes <paramref name="json"/> beside the file and swaps it in. Background writer thread.</summary>
    public static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = $"{path}.{Environment.ProcessId}.tmp";   // every client shares the file
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>Repairs missing / out-of-range values and builds the runtime speech masks.</summary>
    internal static void Normalize(ChatDiscordSettings s)
    {
        s.Version = ChatDiscordSettings.CurrentVersion;
        s.Routes = (s.Routes ?? Array.Empty<DiscordRoute>()).Where(r => r != null).ToArray();
        foreach (var r in s.Routes) NormalizeRoute(r);
        s.Characters = NormalizeCharacters(s.Characters);
        s.BatchMs = Math.Clamp(s.BatchMs, ChatDiscordSettings.BatchMinMs, ChatDiscordSettings.BatchMaxMs);
        s.MinIntervalMs = Math.Clamp(s.MinIntervalMs, ChatDiscordSettings.IntervalMinMs, ChatDiscordSettings.IntervalMaxMs);
        s.MaxQueue = Math.Clamp(s.MaxQueue, ChatDiscordSettings.QueueMin, ChatDiscordSettings.QueueMax);
        s.MaxLog = Math.Clamp(s.MaxLog, ChatDiscordSettings.LogMin, ChatDiscordSettings.LogMax);
    }

    internal static void NormalizeRoute(DiscordRoute r)
    {
        r.Name = string.IsNullOrWhiteSpace(r.Name) ? "Discord" : r.Name.Trim();
        if (Array.IndexOf(DiscordRoute.Modes, r.Mode ?? string.Empty) < 0) r.Mode = DiscordRoute.ModeWebhook;
        r.WebhookProtected ??= string.Empty;
        r.BotTokenProtected ??= string.Empty;
        r.ChannelId = (r.ChannelId ?? string.Empty).Trim();
        r.DisplayName ??= string.Empty;
        r.Keywords ??= string.Empty;
        r.SpeechMask = DiscordRoute.MaskFromNames(r.Speech, DiscordRoute.DefaultSpeechMask());
        r.Tabs = (r.Tabs ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        r.BatchMs = Math.Clamp(r.BatchMs, ChatDiscordSettings.BatchMinMs, ChatDiscordSettings.BatchMaxMs);
        r.MinIntervalMs = Math.Clamp(r.MinIntervalMs, ChatDiscordSettings.IntervalMinMs, ChatDiscordSettings.IntervalMaxMs);
    }

    /// <summary>Drops nameless and repeated entries (same server + name, any case).</summary>
    internal static DiscordCharacter[] NormalizeCharacters(DiscordCharacter[]? list)
    {
        var result = new List<DiscordCharacter>();
        foreach (var c in list ?? Array.Empty<DiscordCharacter>())
        {
            DiscordCharacter? n = c == null ? null : DiscordCharacter.Of(c.Server, c.Name);
            if (n != null && !result.Exists(x => x.Same(n))) result.Add(n);
        }
        return result.ToArray();
    }

    /// <summary><paramref name="list"/> with each switch in <paramref name="ops"/> applied, in order.</summary>
    public static DiscordCharacter[] ApplyCharacterOps(DiscordCharacter[]? list, IEnumerable<DiscordCharacterOp> ops)
    {
        var result = new List<DiscordCharacter>(NormalizeCharacters(list));
        foreach (var op in ops)
        {
            result.RemoveAll(c => c.Same(op.Character));
            if (op.On) result.Add(op.Character);
        }
        return result.ToArray();
    }

    /// <summary>The character list in a settings file, or null when it can't be read.</summary>
    public static DiscordCharacter[]? ReadCharacters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var s = JsonSerializer.Deserialize(json, ChatDiscordJsonContext.Default.ChatDiscordSettings);
            return s == null ? null : NormalizeCharacters(s.Characters);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// What a client writes when it saves: its own settings (<paramref name="ours"/>, from
    /// <see cref="Serialize"/>), except the character list, which is the one in the file now
    /// (<paramref name="disk"/>, re-read just before) with this client's switches applied. So one
    /// client saving can't switch another client's character on or off. When the file is missing
    /// or unreadable, our own list is the base. <paramref name="merged"/> is the list written.
    /// </summary>
    public static string MergeForSave(string ours, string? disk, IReadOnlyList<DiscordCharacterOp> ops, out DiscordCharacter[] merged)
    {
        var mine = JsonSerializer.Deserialize(ours, ChatDiscordJsonContext.Default.ChatDiscordSettings)
                   ?? throw new InvalidOperationException("settings JSON is empty");
        DiscordCharacter[] baseList = ReadCharacters(disk) ?? NormalizeCharacters(mine.Characters);
        merged = ApplyCharacterOps(baseList, ops);
        mine.Characters = merged;
        mine.Version = ChatDiscordSettings.CurrentVersion;
        // Not Serialize(): mine has no runtime speech masks; its speech lists are already right.
        return JsonSerializer.Serialize(mine, ChatDiscordJsonContext.Default.ChatDiscordSettings);
    }

    private static void KeepUnreadable(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.Copy(path, Path.ChangeExtension(path, $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: false);
        }
        catch { }
    }
}

// JsonSerializerContext must be at namespace scope (not nested) for source generation.
[JsonSerializable(typeof(ChatDiscordSettings))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    IncludeFields = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
internal partial class ChatDiscordJsonContext : JsonSerializerContext { }
