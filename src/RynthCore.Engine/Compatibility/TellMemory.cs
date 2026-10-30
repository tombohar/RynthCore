// ============================================================================
//  RynthCore.Engine - Compatibility/TellMemory.cs
//
//  /reply (/r) and /retell (/rt): who last sent you a tell, who you last sent
//  one to, and the rewrite of "/r text" into "/tell Name, text".
//
//  Retail AC answers these itself (ClientCommunicationSystem::DoReply/DoReTell),
//  from state its own /tell and its own incoming-tell handler keep. Neither
//  holds up under RynthCore: our /tell goes straight to Event_TalkDirectByName,
//  so AC never learns who you told (its /retell says "provide a name using
//  @tell"), and /r and /rt reach AC's parser only through the raw path, which
//  depends on a captured chat-bar 'this' (keystroke simulation until then).
//  So ChatCommandDispatcher rewrites them to a /tell, from what this class
//  reads off the chat lines:
//    - "<Tell:IIDString:id:Bob>Bob<\Tell> tells you, ..." (type 3) - AC adds the
//      link only for a player, so NPC tells (no link) never become the reply
//      target, as in retail;
//    - "You tell Bob, ..." (type 4, the server's echo of any tell you sent,
//      whichever chat sent it) and every /tell the dispatcher sends.
//
//  Pure (no AC, no engine state) so tools\ChatTellTests can compile it in.
// ============================================================================

using System;
using System.Text.RegularExpressions;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal enum TellShortcut { None, Reply, Retell }

internal sealed class TellMemory
{
    /// <summary>The engine's one instance (chat lines in, dispatcher and chat input out).</summary>
    public static readonly TellMemory Shared = new();

    // Short enough for RynthChat's empty input box, where they show too.
    public const string NoTellerMessage = "No one has sent you a tell yet - no one to /reply to.";
    public const string NoTelleeMessage = "You haven't sent a tell yet - no one to /retell to.";

    private string? _lastTeller, _lastTellee;

    /// <summary>The player who last sent you a tell, or null.</summary>
    public string? LastTeller => Volatile.Read(ref _lastTeller);
    /// <summary>The player you last sent a tell to, or null.</summary>
    public string? LastTellee => Volatile.Read(ref _lastTellee);

    // AC's player link (HearDirectSpeech prints it for player senders only).
    private static readonly Regex IncomingTell = new(
        @"^\s*<Tell:IIDString:\d+:([^>]*)>[^<]*<\\Tell> tells you, ",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
    private static readonly Regex OutgoingTell = new(
        @"^\s*You tell ([^,]+), ",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

    /// <summary>
    /// An incoming chat line, as AC prints it. Any thread. Matched by its text, not its
    /// chat type (3 and 4 on the native hook; the Decal bridge's numbering isn't relied on):
    /// both shapes only ever start a tell line.
    /// </summary>
    public void OnChatLine(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(" tell", StringComparison.Ordinal) < 0) return;
        try
        {
            Match m = IncomingTell.Match(text);
            if (m.Success) { Remember(ref _lastTeller, m.Groups[1].Value); return; }
            m = OutgoingTell.Match(text);
            if (m.Success) Remember(ref _lastTellee, m.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { }
    }

    /// <summary>A tell we sent (the dispatcher's /tell): its target is the /retell target.</summary>
    public void NoteTellSent(string? name) => Remember(ref _lastTellee, name);

    public void Clear()
    {
        Volatile.Write(ref _lastTeller, null);
        Volatile.Write(ref _lastTellee, null);
    }

    private static void Remember(ref string? field, string? name)
    {
        name = CleanName(name);
        if (name != null) Volatile.Write(ref field, name);
    }

    /// <summary>The name trimmed, without an Olthoi player's '^'/'&amp;' suffix; null if nothing is left.</summary>
    private static string? CleanName(string? name)
    {
        if (name == null) return null;
        name = name.Trim().TrimEnd('^', '&').Trim();
        return name.Length == 0 || name.Contains(',') ? null : name;
    }

    /// <summary>
    /// "/r", "/reply", "/rt", "/retell" (or '@'), alone or followed by a space and the
    /// message. <paramref name="message"/> is the trimmed rest ("" when there is none).
    /// </summary>
    public static TellShortcut Parse(string? line, out string message)
    {
        message = "";
        if (string.IsNullOrEmpty(line)) return TellShortcut.None;
        string t = line.Trim();
        if (t.Length < 2 || (t[0] != '/' && t[0] != '@')) return TellShortcut.None;
        int end = 1;
        while (end < t.Length && !char.IsWhiteSpace(t[end])) end++;
        TellShortcut kind = t[1..end].ToLowerInvariant() switch
        {
            "r" or "reply" => TellShortcut.Reply,
            "rt" or "retell" => TellShortcut.Retell,
            _ => TellShortcut.None,
        };
        if (kind != TellShortcut.None) message = t[end..].Trim();
        return kind;
    }

    /// <summary>The name a shortcut addresses (null: no one yet).</summary>
    public string? TargetFor(TellShortcut kind) => kind switch
    {
        TellShortcut.Reply => LastTeller,
        TellShortcut.Retell => LastTellee,
        _ => null,
    };

    /// <summary>
    /// Rewrites a /reply or /retell line to "/tell Name, message". Returns false when the
    /// line isn't one (both outs null). When it is one but can't be sent, returns true with
    /// <paramref name="tellLine"/> null and <paramref name="error"/> saying why.
    /// </summary>
    public bool TryRewrite(string? line, out string? tellLine, out string? error)
    {
        tellLine = error = null;
        TellShortcut kind = Parse(line, out string message);
        if (kind == TellShortcut.None) return false;
        string? name = TargetFor(kind);
        if (name == null)
            error = kind == TellShortcut.Reply ? NoTellerMessage : NoTelleeMessage;
        else if (message.Length == 0)
            error = $"Type a message after {(kind == TellShortcut.Reply ? "/r" : "/rt")} to send it to {name}.";
        else
            tellLine = $"/tell {name}, {message}";
        return true;
    }
}
