// ============================================================================
//  RynthCore.Engine - UI/Data/ChatWindows.cs
//  Extra chat windows (2026-11-09), the pure part: retail AC opens more chat
//  windows beside the main one, each showing its own tab. The engine has a
//  fixed set of them, "Chat 2" to "Chat 7" (MaxWindows), registered as panels
//  like the main "Chat" (ImGui/Panels/ChatWindowFace.cs): each is opened,
//  closed, placed, sized and popped out by the panel host, so which ones are
//  open, where, docked or popped out lives in the panel layout (panel_state.txt,
//  per character) with every other window. The tab each one shows is saved in
//  that layout too (PanelStateStore value "chatwin.<n>.tab"), written only when
//  the player picks a tab for that window, opens one, or renames the tab.
//
//  No ImGui and no engine state here: tools/ChatWindowTests compiles this file.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.UI.Data;

/// <summary>What "/rc chatwindow ..." asks for.</summary>
internal enum ChatWindowVerb { List, Open, Close, CloseAll, Tab, Error }

/// <summary>A parsed "/rc chatwindow" line. Number: the window (2..); Tab: as typed.</summary>
internal readonly record struct ChatWindowCommand(ChatWindowVerb Verb, int Number = 0, string Tab = "", string Error = "");

internal static class ChatWindows
{
    /// <summary>How many extra chat windows there are (the main chat is window 1).</summary>
    public const int MaxWindows = 6;

    /// <summary>The first extra window's number ("Chat 2").</summary>
    public const int FirstNumber = 2;

    /// <summary>The last extra window's number.</summary>
    public const int LastNumber = FirstNumber + MaxWindows - 1;

    /// <summary>The tab a window shows when its own is gone (as the main chat does).</summary>
    public const string FallbackTab = "All";

    public const string Usage =
        "Usage: /rc chatwindow [list] | open [tab] | close <2-7|all> | tab <2-7> <tab>";

    /// <summary>The panel key of window <paramref name="number"/>: "Chat 2".</summary>
    public static string Key(int number) => "Chat " + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every extra window's key, in order.</summary>
    public static IEnumerable<string> Keys()
    {
        for (int n = FirstNumber; n <= LastNumber; n++) yield return Key(n);
    }

    /// <summary>The window number in a key ("Chat 3" is 3); false for any other key.</summary>
    public static bool TryNumber(string? key, out int number)
    {
        number = 0;
        if (key == null || !key.StartsWith("Chat ", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(key.AsSpan(5), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out number)
               && number >= FirstNumber && number <= LastNumber;
    }

    /// <summary>The layout value that holds window <paramref name="number"/>'s tab.</summary>
    public static string TabValueKey(int number) =>
        "chatwin." + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tab";

    /// <summary>The first window that isn't open, or 0 when all are.</summary>
    public static int FirstFree(Func<string, bool> isOpen)
    {
        for (int n = FirstNumber; n <= LastNumber; n++)
            if (!isOpen(Key(n))) return n;
        return 0;
    }

    /// <summary>
    /// The tab a window shows: its saved one, spelled as the tab is now, or All when it was
    /// deleted (or nothing is saved). The saved value is left alone: a tab that comes back
    /// (another client's settings, an undo) shows again.
    /// </summary>
    public static string EffectiveTab(string? saved, IReadOnlyList<string> allTabs) =>
        Resolve(saved, allTabs) ?? FallbackTab;

    /// <summary>The tab called <paramref name="typed"/> (any case), as it is spelled; null when there is none.</summary>
    public static string? Resolve(string? typed, IReadOnlyList<string> allTabs)
    {
        string t = (typed ?? "").Trim();
        if (t.Length == 0) return null;
        foreach (string tab in allTabs)
            if (string.Equals(tab, t, StringComparison.OrdinalIgnoreCase)) return tab;
        return null;
    }

    /// <summary>A window's saved tab after the player renamed <paramref name="from"/>: the new name, or null for no change.</summary>
    public static string? FollowRename(string? saved, string from, string to) =>
        saved != null && to.Trim().Length > 0 && string.Equals(saved.Trim(), from.Trim(), StringComparison.OrdinalIgnoreCase)
            ? to.Trim() : null;

    /// <summary>The header text of a window showing <paramref name="tab"/>.</summary>
    public static string HeaderText(int number, string tab) =>
        "Chat " + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · " + tab;

    /// <summary>
    /// "/rc chatwindow" arguments: "" or list; open [tab]; close &lt;n|all&gt;; tab &lt;n&gt; &lt;tab&gt;.
    /// A tab name may have spaces. The number may be written "2" or "chat 2".
    /// </summary>
    public static ChatWindowCommand Parse(string? args)
    {
        string a = (args ?? "").Trim();
        if (a.Length == 0 || a.Equals("list", StringComparison.OrdinalIgnoreCase) || a.Equals("status", StringComparison.OrdinalIgnoreCase))
            return new ChatWindowCommand(ChatWindowVerb.List);
        int sp = a.IndexOf(' ');
        string verb = (sp < 0 ? a : a[..sp]).ToLowerInvariant();
        string rest = sp < 0 ? "" : a[(sp + 1)..].Trim();
        switch (verb)
        {
            case "open":
            case "new":
                return new ChatWindowCommand(ChatWindowVerb.Open, Tab: rest);
            case "close":
                if (rest.Equals("all", StringComparison.OrdinalIgnoreCase)) return new ChatWindowCommand(ChatWindowVerb.CloseAll);
                if (TryWindowNumber(rest, out int n, out string after) && after.Length == 0)
                    return new ChatWindowCommand(ChatWindowVerb.Close, n);
                return Bad(rest.Length == 0 ? "Which window? " : $"No chat window '{rest}'. ");
            case "tab":
                if (!TryWindowNumber(rest, out int w, out string tab))
                    return Bad(rest.Length == 0 ? "Which window? " : $"No chat window '{rest}'. ");
                if (tab.Length == 0) return Bad("Which tab? ");
                return new ChatWindowCommand(ChatWindowVerb.Tab, w, tab);
            default:
                return Bad($"Unknown '{verb}'. ");
        }
    }

    private static ChatWindowCommand Bad(string why) =>
        new(ChatWindowVerb.Error, Error: why + Usage);

    /// <summary>"3 Combat" or "chat 3 Combat": the window number, and what follows it.</summary>
    private static bool TryWindowNumber(string s, out int number, out string rest)
    {
        number = 0;
        rest = "";
        s = s.Trim();
        if (s.StartsWith("chat ", StringComparison.OrdinalIgnoreCase)) s = s[5..].TrimStart();
        int sp = s.IndexOf(' ');
        string word = sp < 0 ? s : s[..sp];
        if (!int.TryParse(word, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number)
            || number < FirstNumber || number > LastNumber)
            return false;
        rest = sp < 0 ? "" : s[(sp + 1)..].Trim();
        return true;
    }
}
