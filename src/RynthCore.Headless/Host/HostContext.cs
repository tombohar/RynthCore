// ============================================================================
//  RynthCore.Headless - Host/HostContext.cs
//  Everything one character's plugins can reach through the RynthCore API: its session
//  (network + world model), the geometry, the spell table, the plugin event queue and
//  the API's own bookkeeping (selection, busy count, use-done / error sequences, ...).
//
//  The API functions are static [UnmanagedCallersOnly] methods, so they find their
//  context through Current: set for the duration of every call into a plugin (all on
//  the session's tick thread), with the most recently created context as the fallback
//  for plugin threads that call in on their own (logging from a background load, ...).
//  Partial: each API area keeps its own state in its own file.
// ============================================================================

using System.Runtime.InteropServices;
using RynthCore.Headless.Dat;
using RynthCore.Headless.Movement;
using RynthCore.Headless.Session;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Host;

public sealed partial class HostContext
{
    [ThreadStatic] private static HostContext? _current;
    private static volatile HostContext? _fallback;

    /// <summary>The context of the plugin call in progress (or the last one created).</summary>
    public static HostContext? Current => _current ?? _fallback;

    public ClientSession Session { get; }
    public WorldState World => Session.World;
    public GeometryWalk? Geometry { get; }
    public SpellTable? Spells { get; }
    public HostEvents Events { get; }
    public string AccountName { get; }
    public Action<string> LogSink { get; set; } = _ => { };

    /// <summary>Milliseconds on the session's clock (same clock the session uses).</summary>
    public long NowMs => Environment.TickCount64;

    public HostContext(ClientSession session, string accountName, GeometryWalk? geometry, SpellTable? spells)
    {
        Session = session;
        AccountName = accountName;
        Geometry = geometry;
        Spells = spells;
        Events = new HostEvents(this);
        _fallback = this;
        InitAreas();
    }

    /// <summary>Makes this the current context until the returned scope is disposed.</summary>
    public Scope Enter() => new(this);

    public readonly struct Scope : IDisposable
    {
        private readonly HostContext? _prev;
        public Scope(HostContext ctx)
        {
            _prev = _current;
            _current = ctx;
        }
        public void Dispose() => _current = _prev;
    }

    public void Log(string line) => LogSink(line);

    // Each area's partial file may add an initializer (called once from the constructor).
    partial void InitObjects();
    partial void InitPlayer();
    partial void InitActions();
    private void InitAreas()
    {
        InitObjects();
        InitPlayer();
        InitActions();
    }

    // ── strings handed to plugins ───────────────────────────────────────────

    [ThreadStatic] private static Dictionary<int, IntPtr>? _ansi;

    /// <summary>
    /// An ANSI (single-byte) copy of <paramref name="s"/> the host owns, as the engine returns
    /// names: one buffer per <paramref name="slot"/> and thread, freed by the next call for that
    /// slot on that thread. Null (0) for null/whitespace.
    /// </summary>
    public static IntPtr Ansi(int slot, string? s)
    {
        _ansi ??= new Dictionary<int, IntPtr>();
        if (_ansi.TryGetValue(slot, out IntPtr old) && old != IntPtr.Zero) Marshal.FreeHGlobal(old);
        IntPtr p = string.IsNullOrWhiteSpace(s) ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(s);
        _ansi[slot] = p;
        return p;
    }

    /// <summary>Writes a UTF-8 string NUL-terminated into a caller buffer; returns its byte length (not counting NUL), or -1 if it doesn't fit.</summary>
    public static unsafe int Utf8(string s, byte* buffer, int capacity)
    {
        if (buffer == null || capacity <= 0) return -1;
        byte[] b = System.Text.Encoding.UTF8.GetBytes(s);
        if (b.Length + 1 > capacity) return -1;
        b.AsSpan().CopyTo(new Span<byte>(buffer, capacity));
        buffer[b.Length] = 0;
        return b.Length;
    }
}
