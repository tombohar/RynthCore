// RynthChat 0.2.1's ChatBuffer as the per-poll baseline for ChatHistoryBench: unchanged
// but for its name and its capacity (a const 500 there). It cloned and walked the whole
// ring on every poll.

using System;
using System.Text;

using RynthCore.Plugin.RynthChat;

namespace ChatHistoryBench;

/// <summary>
/// The newest 500 chat lines, exported to the engine as JSON
/// (RynthChatGetScrollbackJson). Add runs on the game thread (chat dispatch),
/// BuildJson on the engine's pump thread; the lock covers both.
///
/// JSON per line: {"seq":N,"ts":"HH:mm:ss","chan":"Chat","type":3,"sender":"Bob"|null,"text":"..."}.
/// "type" (AC's ChatMessageType) was added in 0.2.0; engines before it ignore it.
/// Every non-ASCII character is written as a \uXXXX escape, so the export is
/// pure ASCII and survives the ANSI marshalling on both sides (before 0.2.0 an
/// accented name or the "→" of an outgoing tell arrived as "?").
/// </summary>
internal sealed class OldChatBuffer
{
    private readonly int Capacity;   // bench: a const 500 in 0.2.1

    private readonly ChatLine[] _ring;

    public OldChatBuffer(int capacity) { Capacity = capacity; _ring = new ChatLine[capacity]; }
    private readonly object     _lock = new();
    private int  _head;   // next write index (mod Capacity)
    private int  _count;  // total items stored (≤ Capacity)
    private ulong _nextSeq = 1;

    internal void Add(string? text, int chatType)
    {
        if (string.IsNullOrEmpty(text)) return;
        string channel = ChatClassifier.IsItemInfo(text) ? ChatClassifier.Items
            : ChatClassifier.IsRynthOutput(text) ? ChatClassifier.Rynth
            : ChatClassifier.ChannelFor(chatType);
        string? sender = ChatClassifier.SenderFor(text, chatType);
        string ts = DateTime.Now.ToString("HH:mm:ss");

        lock (_lock)
        {
            // Numbered under the lock, so the ring is always in seq order (the
            // engine skips anything at or below the last seq it has seen).
            _ring[_head % Capacity] = new ChatLine
            {
                Seq       = _nextSeq++,
                Timestamp = ts,
                Channel   = channel,
                ChatType  = chatType,
                Sender    = sender,
                Text      = text,
            };
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    // Returns JSON array of lines with Seq > sinceSeq, oldest-first.
    internal string BuildJson(ulong sinceSeq)
    {
        ChatLine[] snapshot;
        int count;
        int head;
        lock (_lock)
        {
            snapshot = (ChatLine[])_ring.Clone();
            count    = _count;
            head     = _head;
        }

        var sb = new StringBuilder(256);
        sb.Append('[');
        bool first = true;

        // Iterate oldest→newest. Oldest entry is at (head - count + Capacity) % Capacity.
        int start = (head - count + Capacity) % Capacity;
        for (int i = 0; i < count; i++)
        {
            var line = snapshot[(start + i) % Capacity];
            if (line == null || line.Seq <= sinceSeq) continue;

            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"seq\":");
            sb.Append(line.Seq);
            sb.Append(",\"ts\":\"");
            AppendEscaped(sb, line.Timestamp);
            sb.Append("\",\"chan\":\"");
            AppendEscaped(sb, line.Channel);
            sb.Append("\",\"type\":");
            sb.Append(line.ChatType);
            sb.Append(",\"sender\":");
            if (line.Sender != null)
            {
                sb.Append('"');
                AppendEscaped(sb, line.Sender);
                sb.Append('"');
            }
            else
            {
                sb.Append("null");
            }
            sb.Append(",\"text\":\"");
            AppendEscaped(sb, line.Text);
            sb.Append("\"}");
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static void AppendEscaped(StringBuilder sb, string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            // A surrogate pair is escaped as two \uXXXX; a lone half becomes U+FFFD.
            // System.Text.Json refuses a lone surrogate, and before 0.2.1 one such line
            // failed the engine's whole batch (and every poll after it).
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    AppendUnicodeEscape(sb, c);
                    AppendUnicodeEscape(sb, s[++i]);
                }
                else
                    sb.Append("\\uFFFD");
                continue;
            }
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    // Control characters and everything outside ASCII: the string is
                    // marshalled as ANSI, which would turn those into '?'.
                    if (c < 0x20 || c > 0x7E)
                        AppendUnicodeEscape(sb, c);
                    else
                        sb.Append(c);
                    break;
            }
        }
    }

    private static void AppendUnicodeEscape(StringBuilder sb, char c)
    {
        sb.Append("\\u");
        sb.Append(((int)c).ToString("X4"));
    }
}
