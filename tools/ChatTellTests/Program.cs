using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI.Data;

namespace ChatTellTests;

/// <summary>
/// RynthChat's /reply, /retell (TellMemory.cs) and Ctrl+V (ChatInputPaste.cs) without AC.
/// Chat lines are written the way AC prints them (HearDirectSpeech's player link, ACE's
/// "You tell" echo).
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private const string BobTells = "<Tell:IIDString:1342177281:Bob>Bob<\\Tell> tells you, \"hi there\"";

    private static int Main()
    {
        ParseShortcuts();
        IncomingTellSetsReplyTarget();
        NpcAndOtherLinesIgnored();
        OutgoingTellSetsRetellTarget();
        RewriteCases();
        PasteClean();
        PasteInsert();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL CHAT TELL TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // ── shortcuts ───────────────────────────────────────────────────────────

    private static void ParseShortcuts()
    {
        Case("parse: /r /reply /rt /retell");
        Check(TellMemory.Parse("/r hello", out string m) == TellShortcut.Reply && m == "hello", "/r hello");
        Check(TellMemory.Parse("/reply  hello there ", out m) == TellShortcut.Reply && m == "hello there", "/reply, trimmed");
        Check(TellMemory.Parse("/R Hi", out m) == TellShortcut.Reply && m == "Hi", "case-insensitive, message case kept");
        Check(TellMemory.Parse("@r hi", out m) == TellShortcut.Reply && m == "hi", "@ prefix, as AC accepts");
        Check(TellMemory.Parse("/rt again", out m) == TellShortcut.Retell && m == "again", "/rt");
        Check(TellMemory.Parse("/retell again", out m) == TellShortcut.Retell && m == "again", "/retell");
        Check(TellMemory.Parse("/r", out m) == TellShortcut.Reply && m == "", "/r alone");
        Check(TellMemory.Parse("  /rt  ", out m) == TellShortcut.Retell && m == "", "/rt alone, padded");
        Check(TellMemory.Parse("/ra start", out _) == TellShortcut.None, "/ra is RynthAi's");
        Check(TellMemory.Parse("/rc ui", out _) == TellShortcut.None, "/rc is the engine's");
        Check(TellMemory.Parse("/rn", out _) == TellShortcut.None, "/rn");
        Check(TellMemory.Parse("/rtx hi", out _) == TellShortcut.None, "/rtx");
        Check(TellMemory.Parse("r hi", out _) == TellShortcut.None, "no slash: said aloud");
        Check(TellMemory.Parse("/tell Bob, hi", out _) == TellShortcut.None, "/tell");
        Check(TellMemory.Parse("", out _) == TellShortcut.None && TellMemory.Parse(null, out _) == TellShortcut.None, "empty / null");
    }

    // ── tracking ────────────────────────────────────────────────────────────

    private static void IncomingTellSetsReplyTarget()
    {
        Case("incoming player tell: the reply target");
        var t = new TellMemory();
        Check(t.LastTeller == null && t.LastTellee == null, "starts empty");
        t.OnChatLine(BobTells);
        Check(t.LastTeller == "Bob", "Bob");
        t.OnChatLine("<Tell:IIDString:1342177300:Sir Alice Two>Sir Alice Two<\\Tell> tells you, \"x, y\"");
        Check(t.LastTeller == "Sir Alice Two", "a spaced name; the newest tell wins");
        t.OnChatLine("<Tell:IIDString:1342177301:Olthy&>Olthy&<\\Tell> tells you, \"grr\"");
        Check(t.LastTeller == "Olthy", "an Olthoi player's suffix dropped");
        t.OnChatLine("<Tell:IIDString:1342177302:Carl>Carl<\\Tell> tells you, \"bye\"\n");
        Check(t.LastTeller == "Carl", "trailing line break");
        Check(t.LastTellee == null, "an incoming tell is not a retell target");
    }

    private static void NpcAndOtherLinesIgnored()
    {
        Case("lines that aren't a player's tell change nothing");
        var t = new TellMemory();
        t.OnChatLine(BobTells);
        t.OnChatLine("Town Crier tells you, \"Hear ye\"");   // an NPC: AC adds no link (retail /r ignores it too)
        Check(t.LastTeller == "Bob", "NPC tell");
        t.OnChatLine("Dave says, \"<Tell:IIDString:1:Eve>Eve<\\Tell> tells you, nonsense\"");
        Check(t.LastTeller == "Bob", "a quoted tell inside a say");
        t.OnChatLine("[Allegiance] <Tell:IIDString:1342177400:Fred>Fred<\\Tell> says, \"hi\"");
        Check(t.LastTeller == "Bob", "channel line with a link");
        t.OnChatLine("Dave says, \"You tell Bob, nothing\"");
        Check(t.LastTellee == null, "a quoted echo inside a say");
        t.OnChatLine("");
        t.OnChatLine(null);
        Check(t.LastTeller == "Bob" && t.LastTellee == null, "empty / null");
    }

    private static void OutgoingTellSetsRetellTarget()
    {
        Case("tells you send: the retell target");
        var t = new TellMemory();
        t.OnChatLine("You tell Bob, \"hello\"");
        Check(t.LastTellee == "Bob", "server echo");
        t.NoteTellSent("  Alice ");
        Check(t.LastTellee == "Alice", "dispatcher's /tell, trimmed");
        t.OnChatLine("You tell Sir Bob Two, \"a, b\"");
        Check(t.LastTellee == "Sir Bob Two", "echo with a spaced name and a comma in the message");
        t.NoteTellSent("");
        t.NoteTellSent(null);
        t.NoteTellSent("  ");
        Check(t.LastTellee == "Sir Bob Two", "blank names ignored");
        Check(t.LastTeller == null, "a sent tell is not a reply target");
        t.Clear();
        Check(t.LastTeller == null && t.LastTellee == null, "Clear");
    }

    // ── rewrite ─────────────────────────────────────────────────────────────

    private static void RewriteCases()
    {
        Case("rewrite to /tell");
        var t = new TellMemory();
        Check(!t.TryRewrite("/say hi", out string? line, out string? err) && line == null && err == null, "not a shortcut");
        Check(t.TryRewrite("/r hi", out line, out err) && line == null && err == TellMemory.NoTellerMessage, "/r with no one to reply to");
        Check(t.TryRewrite("/rt hi", out line, out err) && line == null && err == TellMemory.NoTelleeMessage, "/rt with no one to retell");

        t.OnChatLine(BobTells);
        t.NoteTellSent("Alice");
        Check(t.TryRewrite("/r thanks, mate", out line, out err) && line == "/tell Bob, thanks, mate" && err == null, "/r -> Bob (message commas kept)");
        Check(t.TryRewrite("/reply ok", out line, out _) && line == "/tell Bob, ok", "/reply -> Bob");
        Check(t.TryRewrite("/rt again", out line, out _) && line == "/tell Alice, again", "/rt -> Alice");
        Check(t.TryRewrite("/retell again", out line, out _) && line == "/tell Alice, again", "/retell -> Alice");
        Check(t.TryRewrite("/r", out line, out err) && line == null && err != null && err.Contains("Bob"), "/r alone: says who it would go to");

        // The dispatcher's /tell then remembers Bob, so /rt after /r goes to Bob (retail too: the server echoes "You tell Bob").
        t.NoteTellSent("Bob");
        Check(t.TryRewrite("/rt more", out line, out _) && line == "/tell Bob, more", "/rt after /r");
    }

    // ── paste ───────────────────────────────────────────────────────────────

    private static void PasteClean()
    {
        Case("paste: one clean line");
        Check(ChatInputPaste.Clean("hello") == "hello", "plain");
        Check(ChatInputPaste.Clean("a\r\nb") == "a b", "CRLF -> space");
        Check(ChatInputPaste.Clean("a\n\n\nb") == "a b", "a run of breaks -> one space");
        Check(ChatInputPaste.Clean("a \nb") == "a b" && ChatInputPaste.Clean("a\n b") == "a b", "no double space at a break");
        Check(ChatInputPaste.Clean("\r\nab\r\n") == "ab", "leading / trailing breaks dropped");
        Check(ChatInputPaste.Clean("a\tb") == "a b", "tab -> space");
        Check(ChatInputPaste.Clean("a\u0016b\u0003c\u007Fd") == "abcd", "control characters dropped");
        Check(ChatInputPaste.Clean("héllo ✓") == "héllo ✓", "non-ASCII kept");
        Check(ChatInputPaste.Clean(null) == "" && ChatInputPaste.Clean("") == "", "null / empty");
    }

    private static void PasteInsert()
    {
        Case("paste: inserted at the caret");
        Check(ChatInputPaste.Insert("/tell Bob, ", 11, "hi there", out int c) == "/tell Bob, hi there" && c == 19, "at the end");
        Check(ChatInputPaste.Insert("ac", 1, "b", out c) == "abc" && c == 2, "in the middle");
        Check(ChatInputPaste.Insert("bc", 0, "a", out c) == "abc" && c == 1, "at the start");
        Check(ChatInputPaste.Insert("ab", 99, "c", out c) == "abc" && c == 3, "caret past the end clamped");
        string same = "keep";
        Check(ReferenceEquals(ChatInputPaste.Insert(same, 2, "\r\n", out c), same) && c == 2, "nothing to paste: line unchanged");
        Check(ReferenceEquals(ChatInputPaste.Insert(same, 2, null, out c), same) && c == 2, "empty clipboard: line unchanged");

        string big = new('x', 5000);
        string r = ChatInputPaste.Insert("ab", 2, big, out c);
        Check(r.Length == ChatInputPaste.MaxLineChars && c == ChatInputPaste.MaxLineChars, "capped at MaxLineChars");
        string full = new('y', ChatInputPaste.MaxLineChars);
        Check(ReferenceEquals(ChatInputPaste.Insert(full, 3, "z", out c), full) && c == 3, "full line: nothing added");
        string pair = new string('a', ChatInputPaste.MaxLineChars - 2);
        r = ChatInputPaste.Insert(pair, pair.Length, "b\U0001F600c", out c);   // room for "b" + half the pair
        Check(r == pair + "b" && c == pair.Length + 1, "never splits a surrogate pair");
    }

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"-- {name}");
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"   FAIL [{_case}] {what}");
    }
}
