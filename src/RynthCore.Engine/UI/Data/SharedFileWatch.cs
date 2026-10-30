// ============================================================================
//  RynthCore.Engine - UI/Data/SharedFileWatch.cs
//  Notices that ANOTHER client rewrote a settings file all clients share
//  (rynthchat_settings.json), so ChatModel can re-read it. Every client saves its
//  whole state, so a client that never re-read the file wrote back stale rules and
//  ticked filters over another client's change (last writer won).
//
//  Polled, not a FileSystemWatcher: no thread or event subscription to outlive an
//  engine reload. A cheap timestamp check at most every PollMs; the file is only
//  read when the timestamp moved, and a write this client made itself (Saved, called
//  just before its swap) does not count as a change. Pure (no AC, no engine state):
//  tools/ChatFilterTests compiles this file.
//
//  Idea from Silentkelpie (rynth PR 6, 2026.10.16.23, ChatModel.PollSettingsFile).
//  A read that fails (the other client is mid-swap) is retried at the next poll; his
//  version marked the change as seen before reading, so a failed read lost it until
//  somebody saved again.
// ============================================================================

using System;
using System.IO;
using System.Threading;

namespace RynthCore.Engine.UI.Data;

internal sealed class SharedFileWatch
{
    private readonly long _pollMs;
    private DateTime _seenWriteUtc;
    private string? _lastSavedText;
    private long _nextPollMs;

    public SharedFileWatch(long pollMs = 2000) => _pollMs = pollMs;

    /// <summary>The file was just loaded at start: its timestamp is not a change.</summary>
    public void Seen(DateTime writtenUtc) => _seenWriteUtc = writtenUtc;

    /// <summary>This client is about to write <paramref name="text"/> (call before the swap): that write is its own.</summary>
    public void Saved(string text) => Volatile.Write(ref _lastSavedText, text);

    /// <summary>
    /// The file's text when another client changed it since the last look, else null (not time to
    /// look yet, no file, same timestamp, or the text is this client's own last save). Throws when
    /// the file can't be read; the change is then offered again at the next poll.
    /// </summary>
    public string? Changed(long nowMs, string path)
    {
        if (nowMs < _nextPollMs) return null;
        _nextPollMs = nowMs + _pollMs;
        if (!File.Exists(path)) return null;
        DateTime written = File.GetLastWriteTimeUtc(path);
        if (written == _seenWriteUtc) return null;
        string text = File.ReadAllText(path);
        _seenWriteUtc = written;   // only after a good read
        return text == Volatile.Read(ref _lastSavedText) ? null : text;
    }
}
