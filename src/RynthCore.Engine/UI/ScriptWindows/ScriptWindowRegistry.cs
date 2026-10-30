// ============================================================================
//  RynthCore.Engine - UI/ScriptWindows/ScriptWindowRegistry.cs
//  Script windows (API v71): owners, their windows, the latest display list
//  per window and the event queue back to each owner.
//  Design: RynthSuite Docs/RYNTHLUA_WINDOWS_DESIGN.md §3, §4.
//
//  Threads:
//    Submit / PollEvents  plugin pump thread, inside the owner's own dispatch
//                         (PluginManager.CurrentDispatch). The buffer is read
//                         or written only during the call.
//    GetInfo              any thread.
//    Replay (the faces)   AC's thread: reads each window's latest list
//                         (Volatile) and appends events under the owner's
//                         short queue lock. It never takes Sync.
//  The engine never calls into a plugin for this and keeps no plugin pointer:
//  an owner is the engine's own LoadedPlugin record, dropped at unload.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.ImGuiBackend.Panels;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.ScriptWindows;

/// <summary>
/// Event types (format 1, §4.3). Every event starts with u16 Type, u16 Size (whole event),
/// u32 Seq, u32 WindowHash, u32 WidgetKey (0 for window-level events); payloads:
///   Clicked    u16 count, u8 button (0 = left)
///   Bool       u8 value
///   Int        i32 value (SliderInt, Combo)
///   Float      f32 value (SliderFloat)
///   Text       u8 submitted, u16 length + UTF-8 (≤ 4096 bytes)
///   Open       u8 open (CollapsingHeader, TreeNode)
///   Geometry   f32×8 windowPos x,y; windowSize w,h; contentAvail w,h; cursorStart x,y
///              (logical units at UI scale 1), u32 flags (bit0 focused, bit1 collapsed, bit2 hovered)
///   Visibility u8 visible, u8 reason
///   Error      u16 length + UTF-8
/// </summary>
internal static class ScriptEventType
{
    public const ushort Clicked = 1;
    public const ushort Bool = 2;
    public const ushort Int = 3;
    public const ushort Float = 4;
    public const ushort Text = 5;
    public const ushort Open = 6;
    public const ushort Geometry = 7;
    public const ushort Visibility = 8;
    public const ushort Error = 10;

    /// <summary>Per-widget value events: coalesced per (type, window, key), latest wins.</summary>
    public static bool IsValue(ushort type) => type is Bool or Int or Float or Text or Open;
}

/// <summary>Visibility event reasons (§4.3).</summary>
internal static class VisibilityReason
{
    public const byte Script = 0;
    /// <summary>The player closed it with the X (the default for a close the owner didn't ask for).</summary>
    public const byte PlayerClose = 1;
    /// <summary>The bar's Scripts menu or a command (the default for an open the owner didn't ask for).</summary>
    public const byte BarOrCommand = 2;
}

/// <summary>A window's geometry, in logical units at UI scale 1 (Geometry events).</summary>
internal struct ScriptGeometry
{
    public Vector2 Pos, Size, Avail, Cursor;
    public uint Flags;

    public const uint Focused = 1u << 0;
    public const uint Collapsed = 1u << 1;
    public const uint Hovered = 1u << 2;
}

/// <summary>One owner's queue of events for the plugin. Appended on AC's thread, drained on the pump thread.</summary>
internal sealed class ScriptEventQueue
{
    public const int MaxTextBytes = 4096;

    private struct Item
    {
        public ushort Type;
        public uint Seq;
        public uint WindowHash;
        public uint WidgetKey;
        public int Count;          // Clicked
        public byte Value;         // Bool/Open value; Visibility visible; Text submitted
        public byte Reason;        // Visibility reason
        public int Int;            // Int
        public float Float;        // Float
        public string? Text;       // Error
        public byte[]? Bytes;      // Text (UTF-8)
        public ScriptGeometry Geo; // Geometry
    }

    private readonly object _lock = new();
    private readonly List<Item> _items = new();
    private uint _nextSeq = 1;

    /// <summary>
    /// Adds a value or geometry event, replacing the queued one for the same (type, window, key)
    /// (Geometry: the same window). A Text event keeps "submitted" if either had it. Returns the seq.
    /// </summary>
    private uint AddCoalesced(Item item)
    {
        lock (_lock)
        {
            uint seq = _nextSeq++;
            item.Seq = seq;
            bool geometry = item.Type == ScriptEventType.Geometry;
            for (int i = 0; i < _items.Count; i++)
            {
                Item e = _items[i];
                if (e.Type == item.Type && e.WindowHash == item.WindowHash && (geometry || e.WidgetKey == item.WidgetKey))
                {
                    if (item.Type == ScriptEventType.Text) item.Value |= e.Value;
                    _items.RemoveAt(i);
                    break;
                }
            }
            MakeRoom();
            _items.Add(item);
            return seq;
        }
    }

    /// <summary>A new integer value (SliderInt, Combo index). Returns the event's seq.</summary>
    public uint Int(uint windowHash, uint key, int value) =>
        AddCoalesced(new Item { Type = ScriptEventType.Int, WindowHash = windowHash, WidgetKey = key, Int = value });

    /// <summary>A new float value (SliderFloat). Returns the event's seq.</summary>
    public uint Float(uint windowHash, uint key, float value) =>
        AddCoalesced(new Item { Type = ScriptEventType.Float, WindowHash = windowHash, WidgetKey = key, Float = value });

    /// <summary>InputText's text (UTF-8, cut to 4096 bytes); <paramref name="submitted"/> = Enter on an EnterReturnsTrue field.</summary>
    public uint Text(uint windowHash, uint key, ReadOnlySpan<byte> utf8, bool submitted)
    {
        if (utf8.Length > MaxTextBytes)
        {
            int n = MaxTextBytes;
            while (n > 0 && (utf8[n] & 0xC0) == 0x80) n--;
            utf8 = utf8[..n];
        }
        return AddCoalesced(new Item { Type = ScriptEventType.Text, WindowHash = windowHash, WidgetKey = key, Value = submitted ? (byte)1 : (byte)0, Bytes = utf8.ToArray() });
    }

    /// <summary>A tree node's or collapsing header's live open state. Returns the event's seq.</summary>
    public uint Open(uint windowHash, uint key, bool open) =>
        AddCoalesced(new Item { Type = ScriptEventType.Open, WindowHash = windowHash, WidgetKey = key, Value = open ? (byte)1 : (byte)0 });

    /// <summary>The window's geometry changed (coalesced per window). Returns the event's seq.</summary>
    public uint Geometry(uint windowHash, in ScriptGeometry geo) =>
        AddCoalesced(new Item { Type = ScriptEventType.Geometry, WindowHash = windowHash, Geo = geo });

    /// <summary>A click on a button: coalesced per widget into a count, never dropped. Returns the event's seq.</summary>
    public uint Click(uint windowHash, uint key)
    {
        lock (_lock)
        {
            uint seq = _nextSeq++;
            for (int i = 0; i < _items.Count; i++)
            {
                Item e = _items[i];
                if (e.Type == ScriptEventType.Clicked && e.WindowHash == windowHash && e.WidgetKey == key)
                {
                    e.Count = Math.Min(e.Count + 1, ushort.MaxValue);
                    e.Seq = seq;
                    _items[i] = e;
                    return seq;
                }
            }
            MakeRoom();
            _items.Add(new Item { Type = ScriptEventType.Clicked, Seq = seq, WindowHash = windowHash, WidgetKey = key, Count = 1 });
            return seq;
        }
    }

    /// <summary>A new bool value: coalesced per widget (the latest wins). Returns the event's seq.</summary>
    public uint Bool(uint windowHash, uint key, bool value) =>
        AddCoalesced(new Item { Type = ScriptEventType.Bool, WindowHash = windowHash, WidgetKey = key, Value = value ? (byte)1 : (byte)0 });

    /// <summary>The window was shown or hidden outside the owner's own flag. Returns the event's seq.</summary>
    public uint Visibility(uint windowHash, bool visible, byte reason)
    {
        lock (_lock)
        {
            uint seq = _nextSeq++;
            MakeRoom();
            _items.Add(new Item { Type = ScriptEventType.Visibility, Seq = seq, WindowHash = windowHash, Value = visible ? (byte)1 : (byte)0, Reason = reason });
            return seq;
        }
    }

    public void Error(uint windowHash, string text)
    {
        if (text.Length > 1024) text = text[..1024];
        lock (_lock)
        {
            MakeRoom();
            _items.Add(new Item { Type = ScriptEventType.Error, Seq = _nextSeq++, WindowHash = windowHash, Text = text });
        }
    }

    /// <summary>
    /// Full: drop the oldest Geometry event, else the oldest value/Open event, else the oldest
    /// Visibility or Error event. Clicks are counted, never dropped. Caller holds the lock.
    /// </summary>
    private void MakeRoom()
    {
        if (_items.Count < ScriptWindowRegistry.MaxEventsPerOwner) return;
        int value = -1, other = -1;
        for (int i = 0; i < _items.Count; i++)
        {
            ushort t = _items[i].Type;
            if (t == ScriptEventType.Geometry) { _items.RemoveAt(i); return; }
            if (value < 0 && ScriptEventType.IsValue(t)) value = i;
            else if (other < 0 && t is ScriptEventType.Visibility or ScriptEventType.Error) other = i;
        }
        if (value >= 0) _items.RemoveAt(value);
        else if (other >= 0) _items.RemoveAt(other);
    }

    /// <summary>Writes whole events into <paramref name="buffer"/>; returns the bytes written.</summary>
    public unsafe int Drain(byte* buffer, int capacity, int* remaining)
    {
        lock (_lock)
        {
            int written = 0, taken = 0;
            var dst = buffer == null || capacity <= 0 ? Span<byte>.Empty : new Span<byte>(buffer, capacity);
            for (; taken < _items.Count; taken++)
            {
                Item e = _items[taken];
                int size = 16 + e.Type switch
                {
                    ScriptEventType.Clicked => 3,
                    ScriptEventType.Bool => 1,
                    ScriptEventType.Int => 4,
                    ScriptEventType.Float => 4,
                    ScriptEventType.Text => 3 + (e.Bytes?.Length ?? 0),
                    ScriptEventType.Open => 1,
                    ScriptEventType.Geometry => 36,
                    ScriptEventType.Visibility => 2,
                    ScriptEventType.Error => 2 + Encoding.UTF8.GetByteCount(e.Text ?? string.Empty),
                    _ => 0,
                };
                if (written + size > dst.Length) break;
                Span<byte> o = dst.Slice(written, size);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(o, e.Type);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(o[2..], (ushort)size);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(o[4..], e.Seq);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(o[8..], e.WindowHash);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(o[12..], e.WidgetKey);
                Span<byte> p = o[16..];
                switch (e.Type)
                {
                    case ScriptEventType.Clicked:
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)e.Count);
                        p[2] = 0; // left button
                        break;
                    case ScriptEventType.Bool:
                    case ScriptEventType.Open:
                        p[0] = e.Value;
                        break;
                    case ScriptEventType.Int:
                        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(p, e.Int);
                        break;
                    case ScriptEventType.Float:
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p, e.Float);
                        break;
                    case ScriptEventType.Text:
                    {
                        byte[] text = e.Bytes ?? Array.Empty<byte>();
                        p[0] = e.Value;
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(p[1..], (ushort)text.Length);
                        text.CopyTo(p[3..]);
                        break;
                    }
                    case ScriptEventType.Geometry:
                    {
                        ScriptGeometry g = e.Geo;
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p, g.Pos.X);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p[4..], g.Pos.Y);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p[8..], g.Size.X);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p[12..], g.Size.Y);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p[16..], g.Avail.X);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p[20..], g.Avail.Y);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p[24..], g.Cursor.X);
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p[28..], g.Cursor.Y);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p[32..], g.Flags);
                        break;
                    }
                    case ScriptEventType.Visibility:
                        p[0] = e.Value;
                        p[1] = e.Reason;
                        break;
                    case ScriptEventType.Error:
                        int n = Encoding.UTF8.GetBytes(e.Text ?? string.Empty, p[2..]);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)n);
                        break;
                }
                written += size;
            }
            _items.RemoveRange(0, taken);
            if (remaining != null) *remaining = _items.Count;
            return written;
        }
    }
}

/// <summary>A plugin that has script windows.</summary>
internal sealed class ScriptWindowOwner
{
    public readonly LoadedPlugin Plugin;
    public readonly Dictionary<string, ScriptWindow> Windows = new(StringComparer.Ordinal);
    public readonly ScriptEventQueue Events = new();

    public ScriptWindowOwner(LoadedPlugin plugin) => Plugin = plugin;
}

/// <summary>One script window: shared between the pump thread (submit) and AC's thread (replay).</summary>
internal sealed class ScriptWindow
{
    public readonly ScriptWindowOwner Owner;
    public readonly string Key;
    public readonly string PanelKey;
    public readonly uint Hash;

    // Pump thread (Submit), read on AC's thread.
    private DisplayList _list = DisplayList.Empty;
    public DisplayList List => Volatile.Read(ref _list);
    public void Publish(DisplayList list) => Volatile.Write(ref _list, list);
    public volatile bool SubmittedVisible;
    public volatile bool Removed;
    public string Title = string.Empty;
    public uint Flags;
    /// <summary>Panel logical units. RequestSize/RequestPos: NaN = none.</summary>
    public Vector2 DefaultSize, MinSize, RequestSize = new(float.NaN), RequestPos = new(float.NaN);
    /// <summary>RequestPos is a FirstUseEver default position, used only when nothing is saved.</summary>
    public bool PosIsDefault;
    /// <summary>The spec's first-open position (PosIsDefault with a RequestPos); null = the cascade.</summary>
    public Vector2? DefaultPos;
    // Pump thread only: the placement requests last passed to the panel host (NaN = none)
    // and the window's ListSeq at that time (the §3.2 re-application rule).
    public Vector2 AppliedRequestSize = new(float.NaN), AppliedRequestPos = new(float.NaN);
    public uint AppliedSizeListSeq, AppliedPosListSeq;

    // Visibility, last change wins (§3.3). The markers are set on the pump thread just
    // before the owner's own Open/Close and consumed by the face on AC's thread, so an
    // open or close the owner asked for sends no Visibility event.
    public volatile bool ExpectOpen, ExpectClose;
    /// <summary>Seq of the last Visibility event the player caused; an owner flag recorded before it (AckSeq below it) is stale.</summary>
    public volatile uint PlayerVisibilitySeq;
    /// <summary>The reason for the next player-caused open/close (0 = none: the face's default). Any thread.</summary>
    public volatile int PendingReason;
    // Pump thread only, under Sync: the owner's last submit for this window, kept so a
    // visibility decision put off while an open/close was still in flight (ApplyVisible)
    // is made at the owner's next poll, without waiting for another submit.
    public bool VisibilityDeferred;
    public uint LastAckSeq;
    public uint LastListSeq;
    /// <summary>The script error text the pump thread last applied (compared to avoid re-encoding).</summary>
    public string? ErrorText;
    private byte[]? _errorUtf8;
    /// <summary>The script's error as pinned, NUL-terminated UTF-8; null = no error. Read on AC's thread.</summary>
    public byte[]? ErrorUtf8 => Volatile.Read(ref _errorUtf8);

    /// <summary>Pump thread: stores the error text, allocating only when it changed.</summary>
    public void SetError(string? text)
    {
        if (string.IsNullOrEmpty(text)) text = null;
        if (string.Equals(text, ErrorText, StringComparison.Ordinal)) return;
        ErrorText = text;
        byte[]? bytes = null;
        if (text != null)
        {
            int n = Encoding.UTF8.GetByteCount(text);
            bytes = GC.AllocateArray<byte>(n + 1, pinned: true);
            Encoding.UTF8.GetBytes(text, bytes);
        }
        Volatile.Write(ref _errorUtf8, bytes);
    }

    // AC thread only (replay).
    /// <summary>The player's edits the plugin hasn't acknowledged yet, and InputText buffers (no snap-back, §2.5).</summary>
    public readonly ScriptLocals Locals = new();
    /// <summary>The geometry last sent in a Geometry event (logical units).</summary>
    public ScriptGeometry LastGeometry;
    public bool GeometrySent;
    public DisplayList? LastReplayed;
    /// <summary>First input not yet reflected in a replayed list: for the click → reaction measurement.</summary>
    public uint PendingInputSeq;
    public int PendingInputFrame;
    public long PendingInputTicks;

    public ScriptWindow(ScriptWindowOwner owner, string key, string panelKey, uint hash)
    {
        Owner = owner;
        Key = key;
        PanelKey = panelKey;
        Hash = hash;
    }

    /// <summary>Records an input for the latency measurement (AC thread).</summary>
    public void NoteInput(uint seq, int frame)
    {
        if (PendingInputSeq != 0) return;
        PendingInputSeq = seq;
        PendingInputFrame = frame;
        PendingInputTicks = Stopwatch.GetTimestamp();
    }
}

/// <summary>One line of the bar's Scripts menu. Built once per snapshot, never per frame.</summary>
internal sealed class ScriptBarItem
{
    public readonly ScriptWindow Window;
    /// <summary>The key up to its last '/', e.g. "RynthLua/Tracker" (the owner's name when the key has none).</summary>
    public readonly string Group;
    /// <summary>True for the first item of its group (the menu draws the group header above it).</summary>
    public readonly bool GroupStart;
    /// <summary>The title plus "##sw" and the window hash in hex: unique, and the same across snapshots.</summary>
    public readonly string MenuLabel;
    public readonly string PanelKey;

    public ScriptBarItem(ScriptWindow window, string group, bool groupStart)
    {
        Window = window;
        Group = group;
        GroupStart = groupStart;
        // A '##' inside the title would cut the visible text short (and '###' replace the ID).
        string title = window.Title.Contains("##", StringComparison.Ordinal) ? window.Title.Replace("##", "# #") : window.Title;
        MenuLabel = title + "##sw" + window.Hash.ToString("X8");
        PanelKey = window.PanelKey;
    }
}

internal static class ScriptWindowRegistry
{
    // §6.1 limits (Phase 0 starting values).
    public const int MaxOpsPerWindow = 1500;
    public const int MaxBytesPerWindow = 64 * 1024;
    public const int MaxWindowsPerOwner = 32;
    public const int MaxBytesPerSubmit = 256 * 1024;
    public const int MaxEventsPerOwner = 1024;
    /// <summary>Ops replayed per frame across all script windows; windows past it draw a placeholder.</summary>
    public const int MaxReplayOpsPerFrame = 4000;
    public const string PanelKeyPrefix = "script:";

    private static readonly object Sync = new();
    private static readonly Dictionary<LoadedPlugin, ScriptWindowOwner> Owners = new(ReferenceEqualityComparer.Instance);
    /// <summary>Panel key → window, across owners: a key belongs to the first owner that used it.</summary>
    private static readonly Dictionary<string, ScriptWindow> ByPanelKey = new(StringComparer.OrdinalIgnoreCase);
    private static long _lastRejectLogTicks;

    // The bar's Scripts menu and the windows with a bar button of their own: copy-on-write
    // snapshots of the ShowInBar windows, republished (under Sync) when the window set, a
    // title or a ShowInBar/BarButton flag changed. AC's thread reads them without taking Sync.
    private static ScriptBarItem[] _barItems = Array.Empty<ScriptBarItem>();
    private static ScriptBarItem[] _barButtons = Array.Empty<ScriptBarItem>();
    private static bool _barDirty;

    /// <summary>The Scripts menu's items, grouped (empty = no Scripts button). Any thread.</summary>
    public static ScriptBarItem[] BarItems => Volatile.Read(ref _barItems);

    /// <summary>
    /// The windows that asked for a bar button of their own (ShowInBar + BarButton), sorted
    /// like the menu. A new array whenever the set or a title changes, so the bar can compare
    /// references to know when to rebuild its buttons. Any thread.
    /// </summary>
    public static ScriptBarItem[] BarButtons => Volatile.Read(ref _barButtons);

    /// <summary>True when a window with these flags gets a bar button of its own (not a Scripts menu line).</summary>
    public static bool HasOwnBarButton(uint flags) =>
        (flags & (WindowFlags.ShowInBar | WindowFlags.BarButton)) == (WindowFlags.ShowInBar | WindowFlags.BarButton);

    // ── Host functions ───────────────────────────────────────────────────

    /// <summary>UiSubmit: the owner's complete window set. Pump thread, inside the owner's dispatch.</summary>
    public static unsafe int Submit(LoadedPlugin plugin, byte* data, int length)
    {
        float displayMax = DisplayMax();
        int rc = DisplayListParser.Parse(data, length, displayMax, out uint ackSeq, out List<ParsedWindow> windows, out string error);
        if (rc != 0)
        {
            // Nothing applied: the previous lists stay; the owner hears why.
            GetOrAddOwner(plugin).Events.Error(0, $"list refused: {error}");
            long now = Stopwatch.GetTimestamp();
            if (now - Interlocked.Read(ref _lastRejectLogTicks) > Stopwatch.Frequency * 5)
            {
                Interlocked.Exchange(ref _lastRejectLogTicks, now);
                RynthLog.UI($"ScriptWindows: {plugin.DisplayName} submit refused ({rc}): {error}");
            }
            return rc;
        }

        lock (Sync)
        {
            ScriptWindowOwner owner = GetOrAddOwnerLocked(plugin);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ParsedWindow pw in windows)
            {
                seen.Add(pw.Key);
                string panelKey = PanelKeyPrefix + SanitizeKey(pw.Key);
                if (!owner.Windows.TryGetValue(pw.Key, out ScriptWindow? w))
                {
                    // Panel keys ignore case and sanitise '=', ',' and '#', so two keys can meet
                    // on one panel entry, from another owner or from this one ("Hud" and "hud").
                    // Sharing it would let each window open, close and unregister the other's.
                    if (ByPanelKey.TryGetValue(panelKey, out ScriptWindow? other))
                    {
                        owner.Events.Error(pw.Hash, other.Owner != owner
                            ? $"window '{pw.Key}' belongs to {other.Owner.Plugin.DisplayName}"
                            : $"window '{pw.Key}' clashes with window '{other.Key}' (window keys ignore case, and '=', ',' and '#' count as '_')");
                        continue;
                    }
                    w = new ScriptWindow(owner, pw.Key, panelKey, pw.Hash);
                    owner.Windows[pw.Key] = w;
                    ByPanelKey[panelKey] = w;
                    Apply(w, pw, ackSeq, isNew: true);
                }
                else
                {
                    Apply(w, pw, ackSeq, isNew: false);
                }
            }

            // A window missing from the set is closed (the script stopped, or the hud was disposed).
            List<string>? gone = null;
            foreach (var kv in owner.Windows)
                if (!seen.Contains(kv.Key)) (gone ??= new List<string>()).Add(kv.Key);
            if (gone != null)
                foreach (string key in gone)
                    RemoveLocked(owner, owner.Windows[key]);
            PublishBarLocked();
        }
        return 0;
    }

    /// <summary>UiPollEvents. Pump thread, inside the owner's dispatch.</summary>
    public static unsafe int PollEvents(LoadedPlugin plugin, byte* buffer, int capacity, int* remaining)
    {
        ScriptWindowOwner? owner;
        lock (Sync)
        {
            if (Owners.TryGetValue(plugin, out owner))
                RecheckDeferredLocked(owner);
        }
        if (owner == null)
        {
            if (remaining != null) *remaining = 0;
            return 0;
        }
        return owner.Events.Drain(buffer, capacity, remaining);
    }

    /// <summary>UiGetInfo. Any thread.</summary>
    public static unsafe int GetInfo(UiInfoNative* info)
    {
        if (info == null) return 0;
        uint want = info->Size;
        if (want < 8) return 0;
        UiInfoNative full = default;
        full.Size = (uint)sizeof(UiInfoNative);
        bool imgui = EngineSettings.EnableImGuiBackend;
        full.Flags = (imgui ? 1u : 0u) | (Compatibility.LoginLifecycleHooks.HasObservedLoginComplete ? 2u : 0u);
        full.MaxFormatVersion = DisplayListParser.FormatVersion;
        full.OpLevel = DisplayListParser.OpLevel;
        full.MaxOpsPerWindow = MaxOpsPerWindow;
        full.MaxBytesPerWindow = MaxBytesPerWindow;
        full.MaxWindowsPerOwner = MaxWindowsPerOwner;
        full.MaxBytesPerSubmit = MaxBytesPerSubmit;
        ScriptWindowMetrics.Fill(&full);
        int n = (int)Math.Min(want, (uint)sizeof(UiInfoNative));
        full.Size = (uint)n;
        Buffer.MemoryCopy(&full, info, n, n);
        return imgui ? n : 0;
    }

    // ── Owners ───────────────────────────────────────────────────────────

    private static ScriptWindowOwner GetOrAddOwner(LoadedPlugin plugin)
    {
        lock (Sync) return GetOrAddOwnerLocked(plugin);
    }

    private static ScriptWindowOwner GetOrAddOwnerLocked(LoadedPlugin plugin)
    {
        if (!Owners.TryGetValue(plugin, out ScriptWindowOwner? owner))
            Owners[plugin] = owner = new ScriptWindowOwner(plugin);
        return owner;
    }

    /// <summary>The plugin unloaded (or failed): its windows close and its queue goes. Pump thread.</summary>
    public static void DropOwner(LoadedPlugin plugin)
    {
        lock (Sync)
        {
            if (!Owners.TryGetValue(plugin, out ScriptWindowOwner? owner)) return;
            foreach (ScriptWindow w in new List<ScriptWindow>(owner.Windows.Values))
                RemoveLocked(owner, w);
            Owners.Remove(plugin);
            PublishBarLocked();
        }
    }

    /// <summary>After each tick: owners whose plugin failed lose their windows.</summary>
    public static void DropFailedOwners()
    {
        List<LoadedPlugin>? failed = null;
        lock (Sync)
        {
            if (Owners.Count == 0) return;
            foreach (LoadedPlugin p in Owners.Keys)
                if (p.Failed) (failed ??= new List<LoadedPlugin>()).Add(p);
        }
        if (failed == null) return;
        foreach (LoadedPlugin p in failed)
        {
            RynthLog.UI($"ScriptWindows: {p.DisplayName} failed; closing its windows.");
            DropOwner(p);
        }
    }

    /// <summary>Engine shutdown: everything goes.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            foreach (ScriptWindowOwner owner in new List<ScriptWindowOwner>(Owners.Values))
                foreach (ScriptWindow w in new List<ScriptWindow>(owner.Windows.Values))
                    RemoveLocked(owner, w);
            Owners.Clear();
            ByPanelKey.Clear();
            _barDirty = false;
            Volatile.Write(ref _barItems, Array.Empty<ScriptBarItem>());
            Volatile.Write(ref _barButtons, Array.Empty<ScriptBarItem>());
        }
        // Engine reload / shutdown: the Skills embed hides and Oracle's announcement is forgotten.
        ImGuiBackend.Panels.OracleSkillsEmbed.Shutdown();
    }

    /// <summary>
    /// Rebuilds the Scripts menu and bar button snapshots when something they show changed.
    /// A ShowInBar window goes in the menu, or on a button of its own with BarButton set.
    /// Caller holds Sync.
    /// </summary>
    private static void PublishBarLocked()
    {
        if (!_barDirty) return;
        _barDirty = false;
        var menu = new List<(string Group, ScriptWindow W)>();
        var buttons = new List<(string Group, ScriptWindow W)>();
        foreach (ScriptWindowOwner o in Owners.Values)
            foreach (ScriptWindow w in o.Windows.Values)
            {
                if ((w.Flags & WindowFlags.ShowInBar) == 0) continue;
                int slash = w.Key.LastIndexOf('/');
                string group = slash > 0 ? w.Key[..slash] : o.Plugin.DisplayName;
                (HasOwnBarButton(w.Flags) ? buttons : menu).Add((group, w));
            }
        Volatile.Write(ref _barItems, Snapshot(menu));
        Volatile.Write(ref _barButtons, Snapshot(buttons));
    }

    private static ScriptBarItem[] Snapshot(List<(string Group, ScriptWindow W)> rows)
    {
        if (rows.Count == 0) return Array.Empty<ScriptBarItem>();
        rows.Sort(static (a, b) =>
        {
            int c = string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.Compare(a.Group, b.Group, StringComparison.Ordinal);
            if (c == 0) c = string.Compare(a.W.Title, b.W.Title, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.W.Key, b.W.Key, StringComparison.Ordinal);
        });
        var items = new ScriptBarItem[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            items[i] = new ScriptBarItem(rows[i].W, rows[i].Group,
                groupStart: i == 0 || !string.Equals(rows[i].Group, rows[i - 1].Group, StringComparison.Ordinal));
        return items;
    }

    // ── Windows ──────────────────────────────────────────────────────────

    /// <summary>The flag bits that shape the panel spec (a change re-registers the entry).</summary>
    private const uint SpecFlags = WindowFlags.ChromeNone | WindowFlags.NoBackground
        | WindowFlags.NoScrollbar | WindowFlags.NoScrollWithMouse;

    private static void Apply(ScriptWindow w, ParsedWindow pw, uint ackSeq, bool isNew)
    {
        Vector2? defaultPos = pw.PosIsDefault && !float.IsNaN(pw.RequestPos.X) ? pw.RequestPos : null;
        bool specChanged = isNew || ((w.Flags ^ pw.Flags) & SpecFlags) != 0
            || w.DefaultSize != pw.DefaultSize || w.MinSize != pw.MinSize || w.DefaultPos != defaultPos;
        bool titleChanged = w.Title != pw.Title;
        if (isNew || titleChanged || ((w.Flags ^ pw.Flags) & (WindowFlags.ShowInBar | WindowFlags.BarButton)) != 0)
            _barDirty = true;
        w.Flags = pw.Flags;
        w.Title = pw.Title;
        w.DefaultSize = pw.DefaultSize;
        w.MinSize = pw.MinSize;
        w.RequestSize = pw.RequestSize;
        w.RequestPos = pw.RequestPos;
        w.PosIsDefault = pw.PosIsDefault;
        w.DefaultPos = defaultPos;
        w.SetError((pw.Flags & WindowFlags.HasError) != 0 ? pw.ErrorText : null);
        if (pw.List != null) w.Publish(pw.List);

        // DrawPanel reads the entry's spec every frame, so a new spec applies at once.
        if (specChanged)
            ImGuiPanelHost.RegisterDynamic(w.PanelKey, w.Title, SpecFor(w), () => new ScriptWindowFace(w));
        else if (titleChanged)
            ImGuiPanelHost.SetDisplayTitle(w.PanelKey, w.Title);

        w.LastAckSeq = ackSeq;
        w.LastListSeq = pw.ListSeq;
        bool opened = ApplyVisible(w, (pw.Flags & WindowFlags.Visible) != 0, ackSeq);
        ApplyRequests(w, pw.ListSeq, opened);
    }

    /// <summary>
    /// The visibility decisions ApplyVisible put off while an open/close was in flight, made
    /// now from the owner's last submit. Without this a window stayed as the in-flight change
    /// left it until the owner happened to submit again, which a hud whose picture doesn't
    /// change never does (e.g. hidden and shown again before AC drew a frame, or while AC was
    /// minimised and drew none). Pump thread (the owner's poll), under Sync.
    /// </summary>
    private static void RecheckDeferredLocked(ScriptWindowOwner owner)
    {
        foreach (ScriptWindow w in owner.Windows.Values)
        {
            if (!w.VisibilityDeferred || w.Removed) continue;
            if (ApplyVisible(w, w.SubmittedVisible, w.LastAckSeq))
                ApplyRequests(w, w.LastListSeq, opened: true);
        }
    }

    /// <summary>
    /// The owner's Visible flag, last change wins (§3.3). The player's X, the bar and
    /// /rc ui open|close change the window locally and queue one Visibility event (the
    /// face's OnShown/OnHidden); the owner's flag is stale until its AckSeq reaches that
    /// event's seq, and a stale flag neither opens nor closes. Otherwise the window follows
    /// the flag; the expect markers keep the owner's own open/close from sending an event.
    /// While a queued open/close hasn't reached AC's thread yet, nothing is decided: the
    /// player's X closes locally at once but its event is queued only when the close is
    /// applied, so a submit in between would otherwise reopen it. The decision is kept
    /// (VisibilityDeferred) and made at the owner's next poll or submit, whichever comes
    /// first. Returns true when this call opened the window. Pump thread, under Sync.
    /// </summary>
    private static bool ApplyVisible(ScriptWindow w, bool visible, uint ackSeq)
    {
        w.SubmittedVisible = visible;
        w.VisibilityDeferred = false;
        // In-flight first: AC's thread sets PlayerVisibilitySeq (OnShown/OnHidden) before the
        // entry stops transitioning, so once it has stopped the stale check below sees the seq.
        if (ImGuiPanelHost.IsTransitioning(w.PanelKey))
        {
            w.VisibilityDeferred = true;                            // decided at the owner's next poll
            return false;
        }
        if (ackSeq < w.PlayerVisibilitySeq) return false;          // stale: the player changed it since
        bool open = ImGuiPanelHost.IsOpen(w.PanelKey);
        if (visible && !open)
        {
            w.ExpectOpen = true;
            ImGuiPanelHost.Open(w.PanelKey);
            return true;
        }
        if (!visible && open)
        {
            w.ExpectClose = true;
            ImGuiPanelHost.Close(w.PanelKey);
        }
        return false;
    }

    /// <summary>
    /// The bar's (or a command's) show/hide: the window opens or closes at once and the
    /// owner gets a Visibility event with reason 2 (bar/command). Any thread.
    /// </summary>
    public static void PlayerSetVisible(ScriptWindow w, bool visible)
    {
        if (w.Removed || ImGuiPanelHost.IsOpen(w.PanelKey) == visible) return;
        w.PendingReason = VisibilityReason.BarOrCommand;
        if (visible) ImGuiPanelHost.Open(w.PanelKey);
        else ImGuiPanelHost.Close(w.PanelKey);
    }

    /// <summary>
    /// Opens or closes a plugin's script window by its key ("RynthOracle/Oracle"), as the bar
    /// does (the owner gets a Visibility event, reason 2). False when no live window has the
    /// key (the plugin isn't loaded or hasn't submitted it yet). Any thread.
    /// </summary>
    public static bool PlayerSetVisible(string key, bool visible)
    {
        ScriptWindow? w = Find(key);
        if (w == null) return false;
        PlayerSetVisible(w, visible);
        return true;
    }

    /// <summary>Whether a plugin has submitted a script window with this key (and it is still live). Any thread.</summary>
    public static bool Has(string key) => Find(key) != null;

    /// <summary>Whether a plugin's script window (by key) is open; false when there is none. Any thread.</summary>
    public static bool IsOpen(string key)
    {
        ScriptWindow? w = Find(key);
        return w != null && ImGuiPanelHost.IsOpen(w.PanelKey);
    }

    /// <summary>The live window with a plugin key, or null.</summary>
    private static ScriptWindow? Find(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        ScriptWindow? w;
        lock (Sync) ByPanelKey.TryGetValue(PanelKeyPrefix + SanitizeKey(key), out w);
        return w == null || w.Removed ? null : w;
    }

    /// <summary>
    /// SetNextWindowSize / SetNextWindowPos (§3.2): a request is passed to the panel host
    /// when its value changed since the last one applied, when the window was re-recorded
    /// (a new ListSeq), or when this submit opens the window. A NaN value (none) forgets the
    /// last one. The host applies it on the window's next docked frame, after an open's
    /// placement from the saved state. Pump thread.
    /// </summary>
    private static void ApplyRequests(ScriptWindow w, uint listSeq, bool opened)
    {
        Vector2 size = w.RequestSize;
        if (float.IsNaN(size.X))
        {
            w.AppliedRequestSize = new Vector2(float.NaN);
        }
        else if (opened || size != w.AppliedRequestSize || listSeq != w.AppliedSizeListSeq)
        {
            w.AppliedRequestSize = size;
            w.AppliedSizeListSeq = listSeq;
            ImGuiPanelHost.RequestSize(w.PanelKey, size);
        }

        Vector2 pos = w.RequestPos;
        if (float.IsNaN(pos.X) || w.PosIsDefault)
        {
            // None, or a FirstUseEver default (the spec's DefaultPos), which is not a request.
            w.AppliedRequestPos = new Vector2(float.NaN);
        }
        else if (opened || pos != w.AppliedRequestPos || listSeq != w.AppliedPosListSeq)
        {
            w.AppliedRequestPos = pos;
            w.AppliedPosListSeq = listSeq;
            ImGuiPanelHost.RequestPos(w.PanelKey, pos);
        }
    }

    private static PanelSpec SpecFor(ScriptWindow w)
    {
        Vector2 size = w.DefaultSize;
        if (size.X <= 0) size.X = 300;
        if (size.Y <= 0) size.Y = 200;
        Vector2 min = Vector2.Max(w.MinSize, new Vector2(60, 40));
        uint f = w.Flags;
        bool none = (f & WindowFlags.ChromeNone) != 0;
        ImGuiNET.ImGuiWindowFlags body = ImGuiNET.ImGuiWindowFlags.None;
        if ((f & WindowFlags.NoScrollbar) != 0) body |= ImGuiNET.ImGuiWindowFlags.NoScrollbar;
        if ((f & WindowFlags.NoScrollWithMouse) != 0) body |= ImGuiNET.ImGuiWindowFlags.NoScrollWithMouse;
        // ClickThrough stays on the face (IImGuiPanel.ClickThrough), read every frame.
        // Pop-outs aren't in the MVP (decision 8): no ↗.
        return new PanelSpec(size, min, none ? PanelChrome.None : PanelChrome.Standard,
            Background: (f & WindowFlags.NoBackground) != 0 ? 0u : null,
            NoPopOut: true,
            BodyFlags: body,
            DefaultPos: w.DefaultPos);
    }

    private static void RemoveLocked(ScriptWindowOwner owner, ScriptWindow w)
    {
        w.Removed = true;
        w.SubmittedVisible = false;
        _barDirty = true;
        // Oracle's window went (plugin unloaded or stopped drawing it): its Skills embed hides now,
        // not at the next frame, which may never come (reload, shutdown).
        if (ImGuiBackend.Panels.OracleSkillsEmbed.IsOraclePanel(w.PanelKey))
            ImGuiBackend.Panels.OracleSkillsEmbed.Clear();
        owner.Windows.Remove(w.Key);
        if (ByPanelKey.TryGetValue(w.PanelKey, out ScriptWindow? cur) && ReferenceEquals(cur, w))
            ByPanelKey.Remove(w.PanelKey);
        ImGuiPanelHost.Unregister(w.PanelKey);
    }

    /// <summary>PanelStateStore rows are "panel.&lt;key&gt;=..." lines: no '=', ',' or control characters.</summary>
    private static string SanitizeKey(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key) sb.Append(c is '=' or ',' or '#' || char.IsControl(c) ? '_' : c);
        return sb.ToString();
    }

    private static float DisplayMax()
    {
        float m = ScriptWindowMetrics.DisplayMax;
        return m > 0 ? m : 16384f;
    }

    /// <summary>A line per owner and window, for diagnostics. Any thread.</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        lock (Sync)
        {
            foreach (ScriptWindowOwner o in Owners.Values)
                foreach (ScriptWindow w in o.Windows.Values)
                    lines.Add($"script window {w.PanelKey}: owner={o.Plugin.DisplayName} visible={w.SubmittedVisible} open={ImGuiPanelHost.IsOpen(w.PanelKey)}"
                        + $" flags=0x{w.Flags:X3}({DescribeFlags(w.Flags)}) error={(w.ErrorUtf8 != null ? "shown" : "none")}"
                        + $" ops={w.List.OpCount} listSeq={w.List.ListSeq} playerSeq={w.PlayerVisibilitySeq}");
        }
        lines.Add($"script windows: {BarItems.Length} in the bar's Scripts menu, {BarButtons.Length} with a bar button");
        return lines;
    }

    private static string DescribeFlags(uint f)
    {
        var sb = new StringBuilder();
        void Add(uint bit, string name) { if ((f & bit) != 0) { if (sb.Length > 0) sb.Append('|'); sb.Append(name); } }
        Add(WindowFlags.Visible, "Visible");
        Add(WindowFlags.ShowInBar, "ShowInBar");
        Add(WindowFlags.ChromeNone, "ChromeNone");
        Add(WindowFlags.ClickThrough, "ClickThrough");
        Add(WindowFlags.NoBackground, "NoBackground");
        Add(WindowFlags.NoScrollbar, "NoScrollbar");
        Add(WindowFlags.NoScrollWithMouse, "NoScrollWithMouse");
        Add(WindowFlags.AutoSize, "AutoSize");
        Add(WindowFlags.NoPopOut, "NoPopOut");
        Add(WindowFlags.HasError, "HasError");
        Add(WindowFlags.PosIsDefault, "PosIsDefault");
        Add(WindowFlags.BarButton, "BarButton");
        return sb.Length > 0 ? sb.ToString() : "none";
    }
}

/// <summary>
/// Layout facts for UiGetInfo, taken on AC's thread while a script window
/// draws (the panel's font pushed) and published as one snapshot.
/// </summary>
internal static unsafe class ScriptWindowMetrics
{
    private sealed class Snapshot
    {
        public float DisplayW, DisplayH, UiScale, LineH, FrameH, SpacingX, SpacingY, PadX, PadY;
        public readonly float[] Advance = new float[95];
        public uint Frame;
    }

    private static Snapshot? _current;
    private static long _takenAt;

    public static float DisplayMax
    {
        get
        {
            Snapshot? s = Volatile.Read(ref _current);
            return s == null ? 0 : 4f * Math.Max(s.DisplayW, s.DisplayH);
        }
    }

    /// <summary>Retakes the snapshot about once a second. AC thread, inside the ImGui frame.</summary>
    public static void Refresh()
    {
        long now = Stopwatch.GetTimestamp();
        if (_current != null && now - _takenAt < Stopwatch.Frequency) return;
        _takenAt = now;
        var io = ImGuiNET.ImGui.GetIO();
        var style = ImGuiNET.ImGui.GetStyle();
        var s = new Snapshot
        {
            DisplayW = io.DisplaySize.X,
            DisplayH = io.DisplaySize.Y,
            UiScale = EngineFrameController.FontScale,
            LineH = ImGuiNET.ImGui.GetTextLineHeight(),
            FrameH = ImGuiNET.ImGui.GetFrameHeight(),
            SpacingX = style.ItemSpacing.X,
            SpacingY = style.ItemSpacing.Y,
            PadX = style.FramePadding.X,
            PadY = style.FramePadding.Y,
            Frame = (uint)ImGuiNET.ImGui.GetFrameCount(),
        };
        byte* one = stackalloc byte[2];
        for (int i = 0; i < 95; i++)
        {
            one[0] = (byte)(' ' + i);
            one[1] = 0;
            Vector2 size;
            ImGuiNET.ImGuiNative.igCalcTextSize(&size, one, one + 1, 0, -1f);
            s.Advance[i] = size.X;
        }
        Volatile.Write(ref _current, s);
    }

    public static void Fill(UiInfoNative* info)
    {
        Snapshot? s = Volatile.Read(ref _current);
        if (s == null) return;
        info->DisplayWidth = s.DisplayW;
        info->DisplayHeight = s.DisplayH;
        info->UiScale = s.UiScale;
        info->TextLineHeight = s.LineH;
        info->FrameHeight = s.FrameH;
        info->ItemSpacingX = s.SpacingX;
        info->ItemSpacingY = s.SpacingY;
        info->FramePaddingX = s.PadX;
        info->FramePaddingY = s.PadY;
        for (int i = 0; i < 95; i++) info->AsciiAdvance[i] = s.Advance[i];
        info->FrameCounter = s.Frame;
    }
}
