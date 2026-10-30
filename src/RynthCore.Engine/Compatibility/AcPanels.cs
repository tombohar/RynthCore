// ============================================================================
//  RynthCore.Engine - Compatibility/AcPanels.cs
//
//  AC's own panels (the pack, the character, the spellbook, fellowship,
//  allegiance, the map, the options...), opened and closed the way AC does it
//  (Tom 2026-10-22: with the retail toolbar hidden, tiny buttons for them).
//  Plugin API v90 GetAcPanels / AcPanelAction, and /rc panels.
//
//  AC shows and hides a panel with a notice, CM_UI::SendNotice_SetPanelVisibility
//  (panel id, visible) (0x0047A4A0, cdecl; gmVitaeUI opens the vitae panel with
//  (0xF, 1)). The containers that answer it hold their panels in a table at
//  +0x5FC, the count at +0x604 (their notice handler's +4 and +0xC):
//    gmFloatyPanelUI     0x1000004F  {element, id}: the pack, character...
//    gmFloatyEnvPanelUI  0x1000004E  {element, id, keep}: vendor, trade, salvage...
//    0x10000054                      {element, id, keep}: spellcasting
//    gmPanelUI 0x10000008 and gmEnvPanelUI 0x10000033, the docked versions, alike.
//  (Read from the test client, 2026-10-22.) A panel is open when its element
//  and its container both show (+0xA4 bit 1). A table entry is the panel
//  itself (gmInventoryUI) or a frame around it: it is named after the first
//  class AC names inside. The containers are found in the UI manager's element
//  registry, as RetailToolbarHooks finds the toolbar, and checked by class
//  before each use.
//
//  Opening and closing is queued and done on AC's game thread after its tick.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class AcPanels
{
    // CM_UI::SendNotice_SetPanelVisibility: the notice list (0x0047A810), its 0x4DD1F8 handlers, each told (id, visible).
    private static readonly byte?[] NoticePattern =
        [0xE8, null, null, null, null, 0x8B, 0x10, 0x68, 0xF8, 0xD1, 0x4D, 0x00, 0x8B, 0xC8, 0xFF, 0x52, 0x10, 0x85, 0xC0, 0x74, 0x3A, 0x57, 0x8B, 0x78, 0x04];

    // The containers: class and table stride.
    private static readonly (int Type, int Stride)[] Hosts =
        [(0x1000004F, 8), (0x10000008, 8), (0x1000004E, 12), (0x10000033, 12), (0x10000054, 12)];

    private const int TableOffset = 0x5FC, CountOffset = 0x604, RegionFlagsOffset = 0xA4, VisibleBit = 0x2;
    private const int MgrElements = 0x14, MgrElementCount = 0x1C, MaxRegistered = 200_000, MaxPanels = 64;
    private const int ChildCountOffset = 0x120, ChildHeadOffset = 0x124;

    private static IntPtr _notice;
    private static bool _initialized;
    private static string _status = "Not initialized.";
    public static string Status => _status;
    public static bool CanToggle => _notice != IntPtr.Zero;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text)) { _status = "acclient.exe not available."; return; }
        var r = HookResolver.Resolve(text, "AcPanels.SendNotice_SetPanelVisibility", NoticePattern, 0);
        _notice = r.Success && r.Source == HookResolver.ResolveSource.PatternScan ? r.Address : IntPtr.Zero;
        _status = CanToggle ? "ready." : "the panel notice wasn't found.";
        RynthLog.Compat("AcPanels: " + _status);
    }

    /// <summary>One of AC's panels: its id (the notice's), the class AC gives it, open or not.</summary>
    public readonly record struct Panel(uint Id, string Class, bool Open);

    // The classes AC's own GetUIElementType functions return (Ghidra symbols, read from the client).
    private static readonly Dictionary<int, string> ClassNames = new()
    {
        [0x1000000D] = "gmExternalContainerUI", [0x10000011] = "gmSalvageUI", [0x10000012] = "gmSecureTradeUI",
        [0x10000015] = "gmSpellcastingUI", [0x10000017] = "gmVendorUI", [0x10000018] = "gmAbuseUI", [0x10000019] = "gmBookUI",
        [0x1000001A] = "gmCharacterInfoUI", [0x1000001B] = "gmEffectsUI", [0x1000001D] = "gmLinkStatusUI",
        [0x1000001E] = "gmMiniGameUI", [0x1000001F] = "gmUrgentAssistanceUI", [0x10000020] = "gmVitaeUI",
        [0x10000021] = "gm3DItemsUI", [0x10000022] = "gmBackpackUI", [0x10000023] = "gmInventoryUI",
        [0x10000024] = "gmPaperDollUI", [0x10000026] = "gmMapUI", [0x10000028] = "gmConfigUI",
        [0x10000029] = "gmGameplayOptionsUI", [0x1000002A] = "gmAttributeUI", [0x1000002B] = "gmSkillUI",
        [0x1000002C] = "gmAllegianceUI", [0x1000002D] = "gmFellowshipUI", [0x1000002E] = "gmSpellbookUI",
        [0x10000041] = "gmMainChatUI", [0x10000045] = "gmFriendsUI", [0x10000047] = "gmSquelchUI", [0x1000004A] = "gmBarberUI",
    };

    private static readonly IntPtr[] _found = new IntPtr[Hosts.Length];
    private static long _nextFindMs;
    private static bool _searchLogged;

    /// <summary>
    /// The panels AC holds now. <paramref name="names"/>: each panel's class names (only /rc panels asks:
    /// naming a frame looks inside it, a bounded walk); without, only ids and open (what the corner
    /// buttons need, read several times a second). AC's main thread (the render or the game tick).
    /// </summary>
    public static List<Panel> List(bool names = false)
    {
        var list = new List<Panel>();
        using var probe = MemoryProbe.BeginWalk();
        bool any = false;
        for (int h = 0; h < Hosts.Length; h++) any |= Valid(h);
        if (!any) Find();
        for (int h = 0; h < Hosts.Length; h++)
            if (Valid(h)) Read(_found[h], Hosts[h].Stride, names, list);
        return list;
    }

    private static bool Valid(int h) => _found[h] != IntPtr.Zero && RetailToolbarHooks.TypeOf(_found[h]) == Hosts[h].Type;

    private static void Find()
    {
        long now = Environment.TickCount64;
        if (now < _nextFindMs) return;
        _nextFindMs = now + 10000;   // a scan of every UI element: rarely (the containers live as long as the UI)
        IntPtr mgr = UiElementHooks.Manager;
        if (mgr == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(mgr, MgrElementCount + 4)) return;
        int count = Marshal.ReadInt32(mgr, MgrElementCount);
        IntPtr arr = Marshal.ReadIntPtr(mgr, MgrElements);
        if (count <= 0 || count > MaxRegistered || !ClientObjectHooks.IsReadableSpan(arr, count * IntPtr.Size)) return;
        for (int i = 0; i < count; i++)
        {
            IntPtr e = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
            if (e == IntPtr.Zero) continue;
            int t = RetailToolbarHooks.TypeOf(e);
            for (int h = 0; h < Hosts.Length; h++)
                if (t == Hosts[h].Type && !Valid(h)) _found[h] = e;
        }
        if (!_searchLogged)
        {
            _searchLogged = true;
            int n = 0;
            for (int h = 0; h < Hosts.Length; h++) if (Valid(h)) n++;
            RynthLog.Compat($"AcPanels: {n} panel container(s) among {count} UI elements.");
        }
    }

    private static bool Shows(IntPtr e) =>
        ClientObjectHooks.IsReadableSpan(e, RegionFlagsOffset + 4) && (Marshal.ReadInt32(e, RegionFlagsOffset) & VisibleBit) != 0;

    private static void Read(IntPtr host, int stride, bool names, List<Panel> into)
    {
        if (!ClientObjectHooks.IsReadableSpan(host, CountOffset + 4)) return;
        int n = Marshal.ReadInt32(host, CountOffset);
        IntPtr table = Marshal.ReadIntPtr(host, TableOffset);
        if (n <= 0 || n > MaxPanels || !ClientObjectHooks.IsReadableSpan(table, n * stride)) return;
        bool hostShows = Shows(host);
        for (int i = 0; i < n; i++)
        {
            IntPtr e = Marshal.ReadIntPtr(table, i * stride);
            uint id = unchecked((uint)Marshal.ReadInt32(table, i * stride + 4));
            if (e == IntPtr.Zero || id == 0 || id > 0xFFFF || !ClientObjectHooks.IsReadableSpan(e, ChildHeadOffset + 4)) continue;
            into.Add(new Panel(id, names ? NameOf(e, 0) : string.Empty, hostShows && Shows(e)));
        }
    }

    /// <summary>The element's class as AC names it, else the named classes inside it ("gmAllegianceUI+gmFellowshipUI").</summary>
    private static string NameOf(IntPtr e, int depth)
    {
        int t = RetailToolbarHooks.TypeOf(e);
        if (ClassNames.TryGetValue(t, out string? name)) return name;
        var inside = new List<string>();
        int budget = 2000;   // a frame around a list of hundreds (a contracts page, a chest) must not cost much
        Collect(e, 0, inside, ref budget);
        return inside.Count > 0 ? string.Join("+", inside) : $"0x{t:X8}";
    }

    private static void Collect(IntPtr e, int depth, List<string> into, ref int budget)
    {
        if (depth > 3 || into.Count >= 4 || --budget <= 0 || !ClientObjectHooks.IsReadableSpan(e, ChildHeadOffset + 4) || Marshal.ReadInt32(e, ChildCountOffset) <= 0) return;
        IntPtr head = Marshal.ReadIntPtr(e, ChildHeadOffset);
        IntPtr node = head == IntPtr.Zero ? IntPtr.Zero : head - 8;
        for (int k = 0; node != IntPtr.Zero && k < 64; k++)
        {
            if (!ClientObjectHooks.IsReadableSpan(node, 0x14)) break;
            IntPtr child = Marshal.ReadIntPtr(node, 0x10);
            if (child != IntPtr.Zero)
            {
                if (ClassNames.TryGetValue(RetailToolbarHooks.TypeOf(child), out string? n)) { if (!into.Contains(n)) into.Add(n); }
                else Collect(child, depth + 1, into, ref budget);
                if (budget <= 0) return;
            }
            IntPtr next = Marshal.ReadIntPtr(node, 8);
            node = next == IntPtr.Zero ? IntPtr.Zero : next - 8;
        }
    }

    private static List<Panel> _cached = new();
    private static long _cachedMs = long.MinValue / 2;

    /// <summary>1 open, 0 closed, -1 not one of AC's panels. AC's main thread; read at most 5 times a second.</summary>
    public static int State(uint id)
    {
        long now = Environment.TickCount64;
        if (now - _cachedMs > 200)
        {
            _cached = List();
            _cachedMs = now;
        }
        foreach (Panel p in _cached)
            if (p.Id == id) return p.Open ? 1 : 0;
        return -1;
    }

    // ── Opening and closing (queued, game thread) ────────────────────────────

    /// <summary>What to do with a panel: open it, close it, or the other of what it is now.</summary>
    public enum Op { Toggle = 0, Open = 1, Close = 2 }

    private readonly record struct Request(uint Id, Op Op, long QueuedMs);

    private static readonly ConcurrentQueue<Request> Requests = new();

    /// <summary>Queues a panel to open, close or toggle (by its id, <see cref="List"/>). Any thread.</summary>
    public static bool Enqueue(uint id, Op op)
    {
        if (!CanToggle || id == 0 || id > 0xFFFF || Requests.Count >= 8) return false;
        Requests.Enqueue(new Request(id, op, Environment.TickCount64));
        return true;
    }

    /// <summary>Game thread, after AC's tick.</summary>
    public static void Drain()
    {
        if (Requests.IsEmpty) return;
        long now = Environment.TickCount64;
        // Not held between worlds: a panel opens and closes in portal space as with AC's own keys
        // (held, a click there or just after landing was dropped; Tom 2026-10-23).
        while (Requests.TryDequeue(out Request r))
        {
            if (now - r.QueuedMs > 3000) continue;
            try
            {
                bool open = r.Op == Op.Open;
                if (r.Op == Op.Toggle)
                {
                    List<Panel> all = List();
                    int at = all.FindIndex(x => x.Id == r.Id);
                    if (at < 0) { RynthLog.Compat($"AcPanels: no panel {r.Id}."); continue; }
                    open = !all[at].Open;
                }
                ((delegate* unmanaged[Cdecl]<uint, int, byte>)_notice)(r.Id, open ? 1 : 0);
                _cachedMs = long.MinValue / 2;
            }
            catch (Exception ex)
            {
                RynthLog.Compat($"AcPanels: panel {r.Id} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public static void Reset()
    {
        while (Requests.TryDequeue(out _)) { }
        Array.Clear(_found);
    }
}
