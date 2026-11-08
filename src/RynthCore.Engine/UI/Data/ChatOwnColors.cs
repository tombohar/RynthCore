// ============================================================================
//  RynthCore.Engine - UI/Data/ChatOwnColors.cs
//  A plugin's own colour for a chat line it writes (2026-11-05). A player asked
//  for RynthAi's item info in, say, allegiance orange without the line moving
//  to where allegiance lines go: the chat type picks the tab, AC's own chat
//  window and its colour, so the colour has to travel on its own.
//
//  No API table change (any engine since v64 can be asked): the plugin calls
//    SendPluginCommand("RynthCore.Chat", "nextcolor", "#RRGGBB")
//  and then WriteToChat(text, chatType) on the same thread. The engine arms the
//  colour for that thread (ArmWindowMs), the next WriteToChat takes it and
//  notes (text, type, colour) here, and when RynthChat's copy of the line
//  reaches ChatSource it is drawn in that colour (ChatLine.OwnArgb). The line
//  keeps its chat type: the same tab, the same rules, the same retail window
//  (which can only show the type's colour). An engine without this finds no
//  plugin called "RynthCore.Chat" and answers 0; the plugin then just writes
//  the line, as before. "nextcolor" with "" or "none" disarms.
//
//  Matching is by the text and chat type (RynthChat passes both through
//  unchanged; a line AC cut at 1,024 characters matches by its start). Each
//  noted line colours one arriving line, oldest first. Lines nobody picked up
//  (RynthChat not loaded, the chat face closed for a long time, AC refused the
//  line) are forgotten after LineTtlMs, and at most MaxLines are kept.
//
//  Pure (no engine state), so tools/ChatFilterTests compiles it in.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Engine.UI.Data;

/// <summary>Lines a plugin wrote with a colour of their own, waiting for RynthChat's copy (any thread).</summary>
internal sealed class ChatOwnColorQueue
{
    /// <summary>A noted line nobody picked up is forgotten after this long.</summary>
    public const long LineTtlMs = 5 * 60_000;
    /// <summary>At most this many lines wait; the oldest goes first.</summary>
    public const int MaxLines = 256;
    /// <summary>AC's longest incoming chat line (ChatCallbackHooks.MaxIncomingChatChars): a longer line arrives cut.</summary>
    public const int MaxArrivingChars = 1024;

    private readonly record struct Entry(string Text, int ChatType, uint Argb, long AtMs);

    private readonly object _lock = new();
    private readonly LinkedList<Entry> _lines = new();
    private int _count;   // read without the lock: the fast "nothing waiting" check

    public int Count => Volatile.Read(ref _count);

    /// <summary>The key a line is matched by: its text as AC printed it (CR / LF trimmed, as WriteToChat does).</summary>
    public static string Key(string text) => text.TrimEnd('\r', '\n');

    /// <summary>The plugin is about to write <paramref name="text"/> as <paramref name="chatType"/>, drawn in <paramref name="argb"/>.</summary>
    public void Expect(string text, int chatType, uint argb, long nowMs)
    {
        string key = Key(text);
        if (key.Length == 0) return;
        lock (_lock)
        {
            Expire(nowMs);
            while (_lines.Count >= MaxLines) _lines.RemoveFirst();
            _lines.AddLast(new Entry(key, chatType, argb | 0xFF000000, nowMs));
            Volatile.Write(ref _count, _lines.Count);
        }
    }

    /// <summary>WriteToChat refused the line: it never arrives, so it colours nothing (the newest such entry goes).</summary>
    public void Forget(string text, int chatType)
    {
        string key = Key(text);
        lock (_lock)
        {
            for (var n = _lines.Last; n != null; n = n.Previous)
                if (n.Value.ChatType == chatType && n.Value.Text == key)
                {
                    _lines.Remove(n);
                    break;
                }
            Volatile.Write(ref _count, _lines.Count);
        }
    }

    /// <summary>
    /// The colour for an arriving line, or null: the oldest waiting entry with this text and chat
    /// type is used up. <paramref name="chatType"/> -1 (RynthChat before 0.2.0) matches any type.
    /// </summary>
    public uint? Take(string? text, int chatType, long nowMs)
    {
        if (Count == 0 || string.IsNullOrEmpty(text)) return null;
        string key = Key(text);
        lock (_lock)
        {
            Expire(nowMs);
            for (var n = _lines.First; n != null; n = n.Next)
            {
                Entry e = n.Value;
                if (chatType >= 0 && e.ChatType != chatType) continue;
                bool same = e.Text == key
                    || (key.Length >= MaxArrivingChars && e.Text.StartsWith(key, StringComparison.Ordinal));
                if (!same) continue;
                _lines.Remove(n);
                Volatile.Write(ref _count, _lines.Count);
                return e.Argb;
            }
            Volatile.Write(ref _count, _lines.Count);
            return null;
        }
    }

    private void Expire(long nowMs)
    {
        while (_lines.First is { } f && nowMs - f.Value.AtMs > LineTtlMs) _lines.RemoveFirst();
    }
}

/// <summary>SendPluginCommand("RynthCore.Chat", ...) and the lines it colours; see the file header.</summary>
internal static class ChatOwnColors
{
    /// <summary>The plugin name plugins pass to SendPluginCommand to reach this.</summary>
    public const string CommandTarget = "RynthCore.Chat";
    /// <summary>"#RRGGBB": the next WriteToChat on this thread is drawn in it; "" / "none" disarms.</summary>
    public const string NextColorAction = "nextcolor";
    /// <summary>An armed colour the next WriteToChat doesn't follow within this long is dropped.</summary>
    public const long ArmWindowMs = 2_000;

    public static readonly ChatOwnColorQueue Lines = new();

    [ThreadStatic] private static uint t_armed;      // 0 = none (an armed colour is always opaque)
    [ThreadStatic] private static long t_armedAtMs;

    /// <summary>The command. True when it was understood (the plugin then knows the engine has this).</summary>
    public static bool HandlePluginCommand(string? action, string? value, long nowMs)
    {
        if (!string.Equals((action ?? "").Trim(), NextColorAction, StringComparison.OrdinalIgnoreCase)) return false;
        string v = (value ?? "").Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            t_armed = 0;
            return true;
        }
        uint? argb = ChatLineColors.ParseColor(v);
        if (argb == null) return false;
        t_armed = argb.Value | 0xFF000000;
        t_armedAtMs = nowMs;
        return true;
    }

    /// <summary>This thread's armed colour, used up (null when none, or armed too long ago).</summary>
    public static uint? TakeArmed(long nowMs)
    {
        uint armed = t_armed;
        if (armed == 0) return null;
        t_armed = 0;
        return nowMs - t_armedAtMs <= ArmWindowMs ? armed : null;
    }
}
