// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiPanelHost.cs
//  Engine-side ImGui panel faces (docs/IMGUI_PARITY_PLAN.md §4.1).
//
//  A panel docked in the client is drawn here. Popped out, the same face
//  is drawn into its own window by ImGuiPopOuts (Tom, 2026-09-29: pop-outs are
//  ImGui too; Avalonia is being retired). The host owns each face's window: the
//  Avalonia-matching frame (24 px header with DRAG, title, text size, pop-out
//  and close; 16 px resize grip, all Phosphor icons), placement restore/persistence shared
//  with the Avalonia face through PanelStateStore, and per-panel error isolation.
//
//  A plugin window can take an engine panel's place (TakeOver, the manifest's
//  "takeOver"): same key, so the saved place, open state and commands carry
//  over; the engine's face is kept and comes back on ReleaseTakeOver.
//
//  Threads:
//    Register        engine init.
//    RegisterDynamic / Unregister / RequestSize / RequestPos / TakeOver / ReleaseTakeOver
//                    any thread (script windows, from the plugin pump thread);
//                    DrawOrder is copy-on-write, removal finishes in DrawAll;
//                    requests are applied in DrawPanel, face swaps in DrawAll.
//    Open / Close    any thread (router: game thread / Avalonia UI thread).
//                    Applied on AC's render thread at the next DrawAll.
//    DrawAll         AC's render thread, inside the ImGui frame.
//    IsOpen/AnyOpen  any thread; a copy-on-write set, so frame reads never lock.
//  Panel Draw code runs on AC's thread: it reads hub snapshots and posts
//  commands only (no plugin calls, no AC reads, no locks, no file I/O).
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend;

/// <summary>Where a panel first opens when nothing is saved for it (v90 plugin windows' "corner").</summary>
internal enum PanelCorner { None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom }

internal enum PanelChrome
{
    /// <summary>DRAG + title + pop-out + close header, resize grip.</summary>
    Standard,
    /// <summary>No header or grip: the panel draws its own drag surface (RynthAi, Radar).</summary>
    None,
}

/// <summary>How a face's window looks and sizes, in Avalonia logical units.</summary>
internal sealed record PanelSpec(
    Vector2 DefaultSize,
    Vector2 MinSize,
    PanelChrome Chrome = PanelChrome.Standard,
    uint? Background = null,     // null: panel glass; 0: transparent (the panel paints its own)
    uint? BorderColor = null,    // null: teal
    float Rounding = RynthTheme.PanelRounding,
    // A face is registered only once it's finished, so finished panels open in
    // ImGui unless the player picked another face (/rc ui). Popped out is Avalonia.
    PanelFace CodeDefault = PanelFace.ImGui,
    bool EdgeToEdge = false,     // body has no padding and never scrolls (Radar)
    bool GoldGrip = false,       // the Radar's 14 px gold grip, inset 4 px
    bool NoPopOut = false,       // no pop-out button
    // Standard chrome without the 18 px strip under the body for the grip: the
    // grip sits over the body's bottom-right corner and the face keeps that
    // corner free (ImGuiPanelHost.BodyGripReserve wide) - Chat puts it on its
    // input row, beside Tell; Tracker, Status, Log, Monsters, Meta, Lua,
    // ChatFilters and RynthNav keep their bottom rows short of it. A body that
    // scrolls ends above the grip instead (the reserve is then 0).
    bool GripInBody = false,
    // OR'd into the body child's window flags (script windows: NoScrollbar,
    // NoScrollWithMouse). 0 = no change.
    ImGuiWindowFlags BodyFlags = ImGuiWindowFlags.None,
    // Where the panel first opens (logical units) when nothing is saved for its
    // key; null = the cascade. (Script windows: a FirstUseEver position.)
    Vector2? DefaultPos = null,
    // With nothing saved and no DefaultPos: open centred in the game view rather
    // than in the top-left cascade, where popped-out panels (separate windows
    // above the game frame) usually sit and would hide it.
    bool OpenCentered = false,
    // No resize grip, docked or popped out: the face sizes itself (the RynthNav arrow
    // fits its height to what it shows). Chromeless and GripInBody panels only.
    bool NoResize = false,
    // The grip over the body's corner shows only while the mouse is near it (or resizing):
    // a frameless plugin window's corner is its own content (RynthAi's footer clock).
    bool GripOnHover = false,
    // With nothing saved: open in this corner of the game view (a plugin window's "corner", v90).
    PanelCorner OpenCorner = PanelCorner.None,
    // The panel draws its own content at any size (a plugin window's "scale": "draw", v90): Ctrl+wheel
    // steps its draw size (PanelDrawScale, 30-250%) instead of its text size.
    bool DrawScale = false,
    // Drawn under AC's own UI, like a native AC window (a plugin window's "layer": "underUi",
    // 2026-11-07): built in the normal frame, presented at AC's UI pass (UnderUiLayer), and
    // click-through wherever an AC window covers the cursor (AcMouseOver). Docked only.
    bool UnderUi = false);

/// <summary>An ImGui panel face. One instance per open; dropped on close.</summary>
internal interface IImGuiPanel
{
    /// <summary>Opened: subscribe to hub sources. AC thread.</summary>
    void OnShown() { }

    /// <summary>Closed: unsubscribe. AC thread.</summary>
    void OnHidden() { }

    /// <summary>Draws the body (below the header). AC thread, inside the panel's child region.</summary>
    void Draw();

    /// <summary>Minimum size (logical units) when it depends on the panel's state; null = the spec's.</summary>
    Vector2? MinSize => null;

    /// <summary>
    /// True while the panel should let clicks through to the game (e.g. the
    /// radar's "hold Ctrl to interact" option): the window takes no input.
    /// </summary>
    bool ClickThrough => false;

    /// <summary>
    /// While <see cref="ClickThrough"/>: the mouse wheel over the panel is the panel's (the face reads
    /// io.MouseWheel itself) and doesn't reach the game; clicks and moves still do. Answered by the
    /// face's last Draw (the radar's "Mouse wheel still zooms", 2026-10-29).
    /// </summary>
    bool ClickThroughWantsWheel => false;

    /// <summary>
    /// True while the face shows a bare look (the Sense panel's compact view, 2026-10-31): docked,
    /// the host draws no header, frame, border, background or resize grip, so only what the face
    /// paints shows; with <see cref="ClickThrough"/> the rest passes clicks to the game. The window
    /// keeps its place and size for when the face is framed again. Popped out (an OS window of its
    /// own) and embedded copies are framed as usual. Answered by the face's last Draw; keep it cheap.
    /// </summary>
    bool Bare => false;

    /// <summary>
    /// Popped out and not hovered or in use: how often the face is redrawn (Hz).
    /// Its pictures are only pushed when they change; this bounds the cost of
    /// building the frame. Hovered or just used, a pop-out runs at 30 Hz.
    /// </summary>
    int PopOutIdleHz => 10;

    /// <summary>
    /// True while the face has nothing to show docked (the Dungeon Map outdoors): it
    /// stays open and subscribed, but the host draws no window for it until this is
    /// false again. Popped out it is drawn as usual. AC thread; keep it cheap.
    /// </summary>
    bool HiddenWhileDocked => false;
}

internal static class ImGuiPanelHost
{
    private sealed class Entry
    {
        /// <summary>The key: PanelStateStore row, router and command name.</summary>
        public required string Title;
        /// <summary>What the header shows (the key for engine panels; a script window's own title).</summary>
        public required string DisplayTitle;
        /// <summary>Registered at run time (script windows, RegisterDynamic); never restored by RestoreOnce.</summary>
        public bool Dynamic;
        /// <summary>Unregistered: removed once it has closed (AC thread, DrawAll).</summary>
        public bool PendingRemoval;
        public required PanelSpec Spec;
        public required Func<IImGuiPanel> Factory;
        public required string WindowId;       // built once: no per-frame string work
        public required string BothTitle;
        public IImGuiPanel? Instance;
        public bool Shown;
        public bool Popped;        // drawn in its own window (ImGuiPopOuts), not in the client
        public bool BodyScrolls;   // last frame the body had a vertical scrollbar
        public float LastGripRoom; // body height kept free for the grip (held during a resize)
        public bool NeedsPlacement;
        public Vector2 Pos, Size;              // client pixels, as last drawn
        public Vector2 PersistedPos, PersistedSize;
        // Docked: the panel's home (the saved spot, or where the player / a request put it) and
        // where that draws, pulled onto the screen for its size and the display (UI/PanelPlacement).
        // Worked out again whenever either changes, from the home, and the home is what is saved:
        // a pull from a moment's smaller client (AC's 800x600 character select) or a taller first
        // frame (a fit-height window at its saved height) is undone once it fits and never saved.
        public readonly HomePlace Place = new();
        public bool PullLogged;                // the first pull after this load's placement is logged
        public Vector2? RequestedSize;        // logical units, applied next frame (under Sync)
        public Vector2? RequestedPos;         // logical units, applied next frame (under Sync)
        public volatile bool HasRequest;      // RequestedSize or RequestedPos is set
        public int Failures;
        public string? Error;
        /// <summary>True while a plugin window has this engine panel's place (TakeOver).</summary>
        public bool TakenOver;
        /// <summary>The engine's own face, kept from the first TakeOver for ReleaseTakeOver.</summary>
        public FaceSet? EngineOwn;
        /// <summary>A face change waiting for the render thread (ApplyFaceChanges).</summary>
        public FaceSet? NextFace;
    }

    /// <summary>What draws a panel: its spec, its face's factory and its header text.</summary>
    private sealed record FaceSet(PanelSpec Spec, Func<IImGuiPanel> Factory, string DisplayTitle);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    // Copy-on-write under Sync: DrawAll iterates the list it read without holding the lock,
    // and dynamic entries are added and removed from the plugin pump thread.
    private static List<Entry> DrawOrder = new();
    private static volatile bool _removalPending;
    private enum RequestKind { Open, Close, PopOut, Redock, ToAvaloniaDocked }

    private static readonly ConcurrentQueue<(string Title, RequestKind Kind)> Requests = new();
    private static HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private static bool _restored;
    private const int MaxFailures = 3;

    // ── Registry ───────────────────────────────────────────────────────

    /// <summary>
    /// Declares an ImGui face for <paramref name="title"/> (the same title the
    /// Avalonia face registers with OverlayHost). Engine init.
    /// </summary>
    public static void Register(string title, PanelSpec spec, Func<IImGuiPanel> factory)
    {
        lock (Sync)
        {
            if (Entries.ContainsKey(title)) return;
            var entry = new Entry
            {
                Title = title, DisplayTitle = title, Spec = spec, Factory = factory,
                WindowId = title + "##rc_panel", BothTitle = title + "  · ImGui",
            };
            Entries[title] = entry;
            DrawOrder = new List<Entry>(DrawOrder) { entry };
        }
    }

    /// <summary>
    /// Declares (or updates) a face registered at run time: a script window
    /// (UI/ScriptWindows). <paramref name="key"/> is the panel key (saved
    /// placement, commands); <paramref name="displayTitle"/> is what the header
    /// shows and may change on every call. An entry being removed is revived.
    /// Opening and closing stay separate (Open / Close). Any thread.
    /// </summary>
    public static void RegisterDynamic(string key, string displayTitle, PanelSpec spec, Func<IImGuiPanel> factory)
    {
        lock (Sync)
        {
            if (Entries.TryGetValue(key, out Entry? existing))
            {
                if (!existing.Dynamic) return;     // never replaces an engine panel
                existing.DisplayTitle = displayTitle;
                existing.Spec = spec;
                existing.Factory = factory;
                existing.PendingRemoval = false;
                return;
            }
            var entry = new Entry
            {
                Title = key, DisplayTitle = displayTitle, Spec = spec, Factory = factory, Dynamic = true,
                WindowId = key + "##rc_panel", BothTitle = displayTitle,
            };
            Entries[key] = entry;
            DrawOrder = new List<Entry>(DrawOrder) { entry };
        }
    }

    /// <summary>
    /// Changes a face's header text: a dynamic face's, or an engine panel's that names what it
    /// shows (an extra chat window's tab). Not while a plugin window has the panel's place. Any thread.
    /// </summary>
    public static void SetDisplayTitle(string key, string displayTitle)
    {
        lock (Sync)
            if (Entries.TryGetValue(key, out Entry? e) && !e.TakenOver) e.DisplayTitle = displayTitle;
    }

    /// <summary>
    /// Removes a face added with <see cref="RegisterDynamic"/>: it closes (its
    /// placement is saved as usual) and the entry goes once it has closed. Any thread.
    /// </summary>
    public static void Unregister(string key)
    {
        lock (Sync)
        {
            if (!Entries.TryGetValue(key, out Entry? e) || !e.Dynamic) return;
            e.PendingRemoval = true;
        }
        Close(key);
        _removalPending = true;
    }

    /// <summary>Drops unregistered entries that have closed. AC thread, after the requests are applied.</summary>
    private static void RemoveUnregistered()
    {
        if (!_removalPending) return;
        _removalPending = false;
        lock (Sync)
        {
            List<Entry>? next = null;
            foreach (Entry e in DrawOrder)
            {
                if (e.PendingRemoval && !e.Shown && !IsOpen(e.Title))
                {
                    next ??= new List<Entry>(DrawOrder);
                    next.Remove(e);
                    Entries.Remove(e.Title);
                }
                else if (e.PendingRemoval)
                {
                    _removalPending = true;   // still closing: look again next frame
                }
            }
            if (next != null) DrawOrder = next;
        }
    }

    public static bool HasFace(string title)
    {
        lock (Sync) return Entries.ContainsKey(title);
    }

    // ── Plugin windows taking over an engine panel (docs/PLUGIN_WINDOWS.md, "takeOver") ──

    /// <summary>True when <paramref name="key"/> is an engine panel (Register), taken over or not. Any thread.</summary>
    public static bool IsEnginePanel(string key)
    {
        lock (Sync) return Entries.TryGetValue(key, out Entry? e) && !e.Dynamic;
    }

    /// <summary>True while a plugin window draws in the engine panel <paramref name="key"/>'s place. Any thread.</summary>
    public static bool IsTakenOver(string key)
    {
        lock (Sync) return Entries.TryGetValue(key, out Entry? e) && e.TakenOver;
    }

    /// <summary>
    /// A plugin window takes the engine panel <paramref name="key"/>'s place: the panel keeps its
    /// key (its saved place and size, open state, hotkeys, /rc command and router entry) and is
    /// drawn with <paramref name="spec"/> and <paramref name="factory"/>'s face from the next
    /// frame; one open now closes and opens again as the new face, where it was (popped out
    /// stays popped out). The engine's own face is kept for <see cref="ReleaseTakeOver"/>. Also
    /// updates a window already in place. False when <paramref name="key"/> isn't an engine
    /// panel. Any thread; applied on AC's render thread.
    /// </summary>
    public static bool TakeOver(string key, string displayTitle, PanelSpec spec, Func<IImGuiPanel> factory)
    {
        lock (Sync)
        {
            if (!Entries.TryGetValue(key, out Entry? e) || e.Dynamic) return false;
            // The first takeover finds the engine's face in place; later ones may find a plugin's.
            e.EngineOwn ??= new FaceSet(e.Spec, e.Factory, e.DisplayTitle);
            e.TakenOver = true;
            e.NextFace = new FaceSet(spec, factory, displayTitle);
        }
        _faceChangePending = true;
        return true;
    }

    /// <summary>
    /// The plugin window that took over <paramref name="key"/> went (plugin unloaded, window
    /// dropped): the engine's own face comes back in its place, open if the window was open.
    /// Any thread; applied on AC's render thread.
    /// </summary>
    public static void ReleaseTakeOver(string key)
    {
        lock (Sync)
        {
            if (!Entries.TryGetValue(key, out Entry? e) || !e.TakenOver || e.EngineOwn == null) return;
            e.TakenOver = false;
            e.NextFace = e.EngineOwn;
        }
        _faceChangePending = true;
    }

    private static volatile bool _faceChangePending;

    /// <summary>
    /// Swaps in the faces TakeOver / ReleaseTakeOver asked for. A panel shown now closes as its
    /// old face and opens as the new one (placement saved and read back as usual), and embedded
    /// copies of it are dropped so their hosts draw the new face. AC thread, before the requests.
    /// </summary>
    private static void ApplyFaceChanges(float placeScale)
    {
        if (!_faceChangePending) return;
        _faceChangePending = false;
        List<Entry>? changed = null;
        lock (Sync)
            foreach (Entry e in DrawOrder)
            {
                if (e.NextFace is not FaceSet next) continue;
                e.NextFace = null;
                bool sameFace = e.Factory == next.Factory;
                e.Spec = next.Spec;
                e.Factory = next.Factory;
                e.DisplayTitle = next.DisplayTitle;
                if (!sameFace) (changed ??= new List<Entry>()).Add(e);
            }
        if (changed == null) return;
        foreach (Entry e in changed)
        {
            RynthLog.UI($"ImGuiPanelHost: {e.Title} is drawn by {(e.TakenOver ? "a plugin's window" : "the engine's face again")}.");
            DropEmbeddedCopies(e.Title);
            if (!e.Shown) continue;   // the next open uses the new face
            Apply(e.Title, RequestKind.Close, placeScale);
            Apply(e.Title, RequestKind.Open, placeScale);
        }
    }

    /// <summary>Hides and drops the embedded copies of <paramref name="title"/> (another face takes its place). AC thread.</summary>
    private static void DropEmbeddedCopies(string title)
    {
        List<string>? gone = null;
        foreach (var kv in EmbeddedFaces)
            if (kv.Value.Title.Equals(title, StringComparison.OrdinalIgnoreCase)) (gone ??= new List<string>()).Add(kv.Key);
        if (gone == null) return;
        foreach (string key in gone)
        {
            EmbeddedFace face = EmbeddedFaces[key];
            EmbeddedFaces.Remove(key);
            try { face.Instance.OnHidden(); }
            catch (Exception ex) { RynthLog.UI($"ImGuiPanelHost: embedded {face.Title}.OnHidden threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public static PanelFace CodeDefault(string title)
    {
        lock (Sync) return Entries.TryGetValue(title, out Entry? e) ? e.Spec.CodeDefault : PanelFace.Avalonia;
    }

    // ── Open state (any thread) ────────────────────────────────────────

    public static bool IsOpen(string title) => System.Threading.Volatile.Read(ref _open).Contains(title);

    /// <summary>True when any ImGui panel face is open.</summary>
    public static bool AnyOpen => System.Threading.Volatile.Read(ref _open).Count > 0;

    /// <summary>
    /// True while the host needs ImGui frames: a face is open, one is still
    /// shown, or a request is queued. Requests (close, pop out, hand off) are
    /// applied inside DrawAll, so frames must keep running until the last one
    /// is processed - otherwise closing or popping out the last open face left
    /// it unapplied (nothing saved, Avalonia never opened, still subscribed).
    /// </summary>
    public static bool NeedsFrame => AnyOpen || !Requests.IsEmpty || System.Threading.Volatile.Read(ref _shownCount) > 0;

    private static int _shownCount;

    public static void Open(string title) => Request(title, RequestKind.Open);

    public static void Close(string title) => Request(title, RequestKind.Close);

    /// <summary>Moves an open panel out of the client into its own window (ImGuiPopOuts).</summary>
    public static void PopOut(string title) => Request(title, RequestKind.PopOut);

    /// <summary>Moves a popped-out panel back into the client, where it was docked.</summary>
    public static void Redock(string title) => Request(title, RequestKind.Redock);

    /// <summary>The face's idle redraw rate while popped out (IImGuiPanel.PopOutIdleHz). AC thread.</summary>
    public static int PopOutIdleHz(string title)
    {
        Entry? entry;
        lock (Sync) Entries.TryGetValue(title, out entry);
        try { return Math.Clamp(entry?.Instance?.PopOutIdleHz ?? 10, 1, 30); } catch { return 10; }
    }

    /// <summary>For a face's own pop-out button: dock back (arrow in) while it is drawn in its own window, else pop out.</summary>
    public static string PopOutGlyph => ImGuiPopOuts.InPopOutFrame ? PhosphorIcons.ArrowSquareIn : PhosphorIcons.ArrowSquareOut;

    /// <summary>The pop-out button's tooltip, matching <see cref="PopOutGlyph"/>.</summary>
    public static string PopOutTooltip => ImGuiPopOuts.InPopOutFrame
        ? "Dock back into the game window" : "Pop out into a floating window";

    /// <summary>A face's own pop-out button: pops out, or docks back while popped out. AC thread, in Draw.</summary>
    public static void TogglePopOut(string title)
    {
        if (ImGuiPopOuts.InPopOutFrame) Redock(title);
        else PopOut(title);
    }

    /// <summary>True when the panel is open in its own window. Any thread (racy read).</summary>
    public static bool IsPopped(string title)
    {
        Entry? entry;
        lock (Sync) Entries.TryGetValue(title, out entry);
        return entry != null && entry.Popped;
    }

    /// <summary>
    /// The docked face switched from ImGui to Avalonia while open: close the
    /// ImGui face, save "docked, open" at its placement, then open the Avalonia
    /// face there. One ordered step, like PopOut.
    /// </summary>
    public static void HandOffToAvalonia(string title) => Request(title, RequestKind.ToAvaloniaDocked);

    private static void Request(string title, RequestKind kind)
    {
        lock (Sync)
        {
            if (!Entries.ContainsKey(title)) return;
            if (kind is RequestKind.PopOut or RequestKind.Redock)
            {
                // Moves an open panel between the client and its own window.
                if (!_open.Contains(title)) return;
            }
            else
            {
                bool open = kind == RequestKind.Open;
                var next = new HashSet<string>(_open, StringComparer.OrdinalIgnoreCase);
                if (!(open ? next.Add(title) : next.Remove(title))) return;
                System.Threading.Volatile.Write(ref _open, next);
            }
        }
        Requests.Enqueue((title, kind));
    }

    /// <summary>One line per face that is requested open or shown. Diagnostics; any thread (racy reads).</summary>
    public static List<string> DescribeFaces()
    {
        var lines = new List<string>();
        List<Entry> order;
        lock (Sync) order = DrawOrder;
        foreach (Entry e in order)
        {
            if (!e.Shown && !IsOpen(e.Title)) continue;
            lines.Add($"face {e.Title}: requested={IsOpen(e.Title)} shown={e.Shown} pos=({e.Pos.X:0},{e.Pos.Y:0}){(e.Place.Pulled ? $" home=({e.Place.Home.X:0},{e.Place.Home.Y:0})" : "")} size=({e.Size.X:0}x{e.Size.Y:0}) failures={e.Failures}{(e.Error != null ? " error=" + e.Error : "")}");
        }
        if (lines.Count == 0) lines.Add("faces: none open");
        return lines;
    }

    // ── Helpers for panel faces (AC thread, inside the panel's Draw) ─────

    /// <summary>
    /// Makes the last item a drag handle for its panel window (chromeless
    /// panels drag from their own title row, like the Avalonia face's
    /// AttachDragHandle). Clamped to the client area.
    /// </summary>
    public static void DragWindowWithLastItem()
    {
        if (_inEmbed) return;   // embedded in another panel: that panel's window isn't this face's to move
        if (ImGuiPopOuts.InPopOutFrame)
        {
            // Popped out: the OS window moves (it follows the cursor from here on).
            if (ImGuiNET.ImGui.IsItemActive() && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
                ImGuiPopOuts.BeginMoveCurrent();
            return;
        }
        if (!ImGuiNET.ImGui.IsItemActive() || !ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f)) return;
        // The panel body is a child window; move its parent (the panel window).
        _pendingDragDelta += ImGuiNET.ImGui.GetIO().MouseDelta;
    }

    /// <summary>Asks for a new window size (logical units), applied on the next frame it draws. Any thread.</summary>
    public static void RequestSize(string title, Vector2 logicalSize)
    {
        if (_inEmbed) return;   // an embedded copy must not resize the face's own window
        lock (Sync)
        {
            if (!Entries.TryGetValue(title, out Entry? entry)) return;
            entry.RequestedSize = logicalSize;
            entry.HasRequest = true;
        }
    }

    /// <summary>
    /// Asks for a new window position (logical units), applied on the next frame it
    /// draws docked, clamped to the client area. It is not saved as a move by the
    /// player. A request queued with an Open survives the open's placement. Any thread.
    /// </summary>
    public static void RequestPos(string title, Vector2 logicalPos)
    {
        if (_inEmbed) return;
        lock (Sync)
        {
            if (!Entries.TryGetValue(title, out Entry? entry)) return;
            entry.RequestedPos = logicalPos;
            entry.HasRequest = true;
        }
    }

    /// <summary>
    /// True while an Open or Close for <paramref name="title"/> is queued but not yet
    /// applied on AC's thread (the requested state differs from what is drawn). Any thread (racy).
    /// </summary>
    public static bool IsTransitioning(string title)
    {
        Entry? entry;
        lock (Sync) Entries.TryGetValue(title, out entry);
        return entry != null && entry.Shown != IsOpen(title);
    }

    private static Vector2 _pendingDragDelta;

    // Chromeless-panel corner resize in progress: which panel, and where it began.
    private static string? _resizeTitle;
    private static Vector2 _resizeStartSize, _resizeStartMouse;

    /// <summary>
    /// While a GripInBody face draws: the width (px) at the body's right edge,
    /// across the bottom <see cref="BodyGripReserve"/> px, that the grip covers
    /// and the face must leave free. 0 for every other face. AC thread.
    /// </summary>
    public static float BodyGripReserve { get; private set; }

    /// <summary>
    /// The last DrawAll: a docked click-through panel has the mouse wheel over it
    /// (IImGuiPanel.ClickThroughWantsWheel), so the wheel is kept from the game while the clicks
    /// still reach it (EngineFrameController, Win32Backend.UpdateCaptureFlags). AC thread.
    /// </summary>
    public static bool WheelWanted { get; private set; }

    /// <summary>
    /// The last DrawAll: an under-UI panel (PanelSpec.UnderUi) is only hovered (no button held), so the
    /// mouse moves still go to AC and its own mouse-over stays current (EngineFrameController). AC thread.
    /// </summary>
    public static bool UnderUiHovered { get; private set; }

    // ── Frame (AC thread) ──────────────────────────────────────────────

    /// <summary>
    /// Draws every open face. <paramref name="uiScale"/> is the scale fonts
    /// were built for; placement uses the Avalonia overlay's scale so a panel
    /// lands where its Avalonia face would.
    /// </summary>
    public static void DrawAll(float uiScale)
    {
        // UiCost: each panel by its title; the rest of DrawAll as the host's upkeep.
        long cHost = UiCost.Start(), costMark = UiCost.Mark;
        int drawn = 0;
        _frameScale = uiScale;
        WheelWanted = false;
        UnderUiHovered = false;
        float placeScale = PlacementScale();
        ApplyFaceChanges(placeScale);   // plugin windows taking over (or giving back) engine panels
        ApplyLayoutSwitch(placeScale);  // another character's panel layout (UI/PanelLayout)
        while (Requests.TryDequeue(out var req))
            Apply(req.Title, req.Kind, placeScale);
        RemoveUnregistered();
        SweepEmbedded();
        Panels.OracleSkillsEmbed.Sweep(Environment.TickCount64);   // Oracle's Skills embed that stopped drawing

        List<Entry> order;
        lock (Sync) order = DrawOrder;
        foreach (Entry entry in order)
        {
            if (!entry.Shown || entry.Popped) continue;
            if (HiddenWhileDocked(entry))
            {
                if (_resizeTitle == entry.Title) _resizeTitle = null;   // hid mid-resize: free the grip
                continue;
            }
            long cPanel = UiCost.Start();
            DrawPanel(entry, uiScale, placeScale, null);
            UiCost.StopPanel(entry.Title, cPanel);
            drawn++;
        }
        DrawSizeNote();
        UiCost.StopExclusive(UiCost.PanelHost, cHost, costMark, cHost != 0 ? drawn : -1);
    }

    // ── One way to resize every panel (Tom 2026-10-23) ───────────────────
    //
    // Ctrl+wheel anywhere over a panel - engine panel, plugin window, framed or frameless, docked
    // or popped out - makes that panel one step bigger or smaller; each panel keeps its own size.
    // A panel that draws its own content at any size (a plugin window with "scale": "draw") steps
    // its draw size 10% (30-250%, PanelDrawScale); the others their text size (100-250%,
    // PanelTextScale). The wheel is used up, so the panel doesn't also scroll. A face that has its
    // own Ctrl+wheel inside it (the Mini Remote's boxes) clears the wheel first and keeps it.
    // A note by the cursor says the new size for a moment.

    private static string _sizeNote = string.Empty;
    private static long _sizeNoteUntilMs;

    private static void StepSizeWithWheel(Entry entry)
    {
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        if (!io.KeyCtrl || io.MouseWheel == 0f) return;
        if (!ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)) return;
        int dir = io.MouseWheel > 0f ? 1 : -1;
        io.MouseWheel = 0f;
        int pct;
        if (entry.Spec.DrawScale)
        {
            int now = PanelDrawScale.Get(entry.Title);
            if (now == 0) now = (int)MathF.Round(PanelTextScale.Factor(PanelTextScale.Get(entry.Title)) * 10f) * 10;
            pct = Math.Clamp(now + dir * PanelDrawScale.Step, PanelDrawScale.Min, PanelDrawScale.Max);
            PanelDrawScale.Set(entry.Title, pct);
        }
        else
        {
            int step = Math.Clamp(PanelTextScale.Get(entry.Title) + dir, 0, PanelTextScale.SizeNames.Length - 1);
            PanelTextScale.SetForWindow(entry.Title, step);
            pct = (int)MathF.Round(PanelTextScale.Factor(step) * 100f);
        }
        _sizeNote = $"{entry.Title}: {pct.ToString(System.Globalization.CultureInfo.InvariantCulture)}%";
        _sizeNoteUntilMs = Environment.TickCount64 + 1200;
    }

    private static void DrawSizeNote()
    {
        if (_sizeNote.Length == 0 || Environment.TickCount64 > _sizeNoteUntilMs) return;
        ImGuiNET.ImGui.SetTooltip(_sizeNote + "  (Ctrl+wheel)");
    }

    private static bool HiddenWhileDocked(Entry entry)
    {
        if (entry.Error != null) return false;   // an error line is always shown
        try { return entry.Instance?.HiddenWhileDocked ?? false; }
        catch { return false; }
    }

    /// <summary>
    /// Draws a popped-out panel filling its own window (<paramref name="display"/>
    /// px). AC thread, called by ImGuiPopOuts with the pop-out's context current.
    /// </summary>
    public static void DrawPopped(string title, float uiScale, Vector2 display)
    {
        Entry? entry;
        lock (Sync) Entries.TryGetValue(title, out entry);
        if (entry == null || !entry.Shown || !entry.Popped) return;
        _frameScale = uiScale;
        long cPanel = UiCost.Start();
        DrawPanel(entry, uiScale, PlacementScale(), display);
        UiCost.StopPopped(entry.Title, cPanel);
    }

    /// <summary>A pop-out window was moved or resized by the user: save it. Game thread.</summary>
    public static void OnPopOutGeometry(string title, int left, int top, int width, int height)
    {
        Entry? entry;
        lock (Sync) Entries.TryGetValue(title, out entry);
        if (entry == null || !entry.Popped) return;
        PersistPopped(entry, open: true, left, top, width, height, PlacementScale());
    }

    /// <summary>
    /// Once per engine load, in the world: docked panels that were open and
    /// whose docked face is ImGui (or Both) reopen here. (The Avalonia overlay
    /// skips those in its own restore.) AC thread.
    /// </summary>
    public static void RestoreOnce()
    {
        if (_restored) return;
        _restored = true;
        // Settings > Interface "Remember Open Windows and Positions" (default on).
        if (!Plugins.EngineSettings.RememberWindowLayout) return;
        OpenSavedOpen();
    }

    /// <summary>
    /// Opens the engine panels and plugin windows the panel store has saved open (ImGui or Both docked
    /// face). AC thread.
    /// </summary>
    private static void OpenSavedOpen()
    {
        List<string> titles = new();
        // Script windows (dynamic) open when their owner says so, not from the saved state. Plugin
        // windows (dynamic too) follow it like engine panels (2026-11-05): one registered before the
        // character's layout was in use (a hot reload, a name that came late, a relogin) decided its
        // open state from the layout before, and opened here it comes back as this layout left it.
        lock (Sync)
            foreach (Entry e in Entries.Values)
                if (PanelLayoutPolicy.FollowsSavedOpenState(KindOf(e))) titles.Add(e.Title);
        foreach (string title in titles)
        {
            if (!PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry saved)) continue;
            if (!saved.Open) continue;
            OpenSaved(title);
        }
    }

    /// <summary>Opens a panel saved open, when its docked face is ImGui (or Both). AC thread.</summary>
    private static void OpenSaved(string title)
    {
        PanelFace face = PanelRouter.ResolveDockedFace(title);
        if (face is not (PanelFace.ImGui or PanelFace.Both)) return;
        // Saved popped out: it reopens popped out (Apply reads the saved entry).
        Open(title);
    }

    /// <summary>An engine panel, a plugin's window (UI/Sections/PluginWindows) or a script window. AC thread.</summary>
    private static WindowKind KindOf(Entry e) =>
        !e.Dynamic ? WindowKind.EnginePanel
        : !e.PendingRemoval && UI.Sections.PluginWindows.IsHosted(e.Title) ? WindowKind.PluginWindow
        : WindowKind.ScriptWindow;

    // ── Panel layout per character (UI/PanelLayout) ───────────────────────

    private static volatile bool _layoutSwitched;

    /// <summary>
    /// The panel store now holds another layout (another character's, the default, or a reset): the
    /// open panels take their places from it at the next DrawAll. Any thread.
    /// </summary>
    public static void OnLayoutSwitched() => _layoutSwitched = true;

    /// <summary>
    /// After a layout switch: each shown panel is placed from the new layout (docked spot and size, popped
    /// out or not, and where); an engine panel or plugin window the layout has saved closed closes,
    /// without saving over it; the ones it has saved open open. Script windows stay open (their owners
    /// decide that) and move. Nothing shown (a hot reload, the first login): only the opening. AC thread.
    /// (Plugin windows follow the layout's open state since 2026-11-05: they register from the plugins'
    /// manifests seconds after a login, and one registered while the store held another layout kept
    /// that layout's open state for the whole session.)
    /// </summary>
    private static void ApplyLayoutSwitch(float placeScale)
    {
        if (!_layoutSwitched) return;
        _layoutSwitched = false;
        bool remember = Plugins.EngineSettings.RememberWindowLayout;
        List<Entry> order;
        lock (Sync) order = DrawOrder;
        foreach (Entry e in order)
        {
            bool has = PanelStateStore.TryGetPanel(e.Title, out PanelStateStore.PanelEntry saved);
            switch (PanelLayoutPolicy.StepAtSwitch(KindOf(e), e.Shown, remember, has, saved.Open))
            {
                case SwitchStep.Leave:
                    continue;
                case SwitchStep.Open:
                    OpenSaved(e.Title);   // queued; applied by this DrawAll, placed from the new layout
                    continue;
                case SwitchStep.Close:
                    CloseQuietly(e);
                    continue;
            }
            if (e.Popped)
            {
                ImGuiPopOuts.Close(e.Title);
                e.Popped = false;
            }
            PlaceFromSavedState(e, placeScale);
            if (PanelRouter.CanPopOut && !e.Spec.NoPopOut && has && saved.Floating)
                StartPopOut(e, placeScale, fromSaved: true);
            else if (!has)
                Persist(e, open: true, placeScale);   // new to this layout: saved here, so it reopens here
        }
        RynthLog.UI("ImGuiPanelHost: the open panels took their places from the panel layout now in use.");
    }

    /// <summary>Closes a shown face without saving its placement (the layout it came from isn't in use any more). AC thread.</summary>
    private static void CloseQuietly(Entry entry)
    {
        lock (Sync)
        {
            if (_open.Contains(entry.Title))
            {
                var next = new HashSet<string>(_open, StringComparer.OrdinalIgnoreCase);
                next.Remove(entry.Title);
                System.Threading.Volatile.Write(ref _open, next);
            }
        }
        if (entry.Popped)
        {
            ImGuiPopOuts.Close(entry.Title);
            entry.Popped = false;
        }
        try { entry.Instance?.OnHidden(); }
        catch (Exception ex) { RynthLog.UI($"ImGuiPanelHost: {entry.Title}.OnHidden threw {ex.GetType().Name}: {ex.Message}"); }
        entry.Instance = null;
        entry.Shown = false;
        System.Threading.Interlocked.Decrement(ref _shownCount);
    }

    /// <summary>Client pixels per logical unit (the unit PanelSpec sizes, MinSize and RequestSize use).</summary>
    internal static float PlacementScale()
    {
        // Saved positions are in Avalonia logical units. Without the Avalonia overlay
        // (off, or the CoreCLR engine) the ImGui DPI scale is the same conversion.
        float s = AvaloniaOverlay.IsRunning ? AvaloniaOverlay.InputScale : EngineFrameController.FontScale;
        return s > 0f ? s : 1f;
    }

    private static void Apply(string title, RequestKind kind, float placeScale)
    {
        Entry? entry;
        lock (Sync) Entries.TryGetValue(title, out entry);
        if (entry == null) return;

        if (kind == RequestKind.PopOut)
        {
            if (entry.Shown && !entry.Popped) StartPopOut(entry, placeScale, fromSaved: false);
            return;
        }
        if (kind == RequestKind.Redock)
        {
            if (!entry.Shown || !entry.Popped) return;
            ImGuiPopOuts.Close(title);
            entry.Popped = false;
            entry.NeedsPlacement = true;   // back where it was docked
            Persist(entry, open: true, placeScale);
            return;
        }

        bool open = kind == RequestKind.Open;
        if (entry.Shown == open) return;

        if (open)
        {
            PlaceFromSavedState(entry, placeScale);
            entry.Failures = 0;
            entry.Error = null;
            try
            {
                entry.Instance = entry.Factory();
                entry.Instance.OnShown();
            }
            catch (Exception ex)
            {
                entry.Error = $"{title} failed to open: {ex.Message}";
                RynthLog.UI($"ImGuiPanelHost: {title} factory/OnShown threw {ex.GetType().Name}: {ex.Message}");
            }
            entry.Shown = true;
            System.Threading.Interlocked.Increment(ref _shownCount);
            // One panel on screen once: close any Avalonia copy of it (script windows have none).
            if (!entry.Dynamic) AvaloniaOverlay.CloseAvaloniaCopy(title);
            // Last left popped out: it opens popped out again, where it was.
            if (PanelRouter.CanPopOut && !entry.Spec.NoPopOut
                && PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry saved) && saved.Floating)
                StartPopOut(entry, placeScale, fromSaved: true);
            else
                Persist(entry, open: true, placeScale); // reopen here after a restart
        }
        else
        {
            bool wasPopped = entry.Popped;
            if (wasPopped)
            {
                ImGuiPopOuts.Close(title);
                entry.Popped = false;
            }
            try { entry.Instance?.OnHidden(); }
            catch (Exception ex) { RynthLog.UI($"ImGuiPanelHost: {title}.OnHidden threw {ex.GetType().Name}: {ex.Message}"); }
            entry.Instance = null;
            entry.Shown = false;
            System.Threading.Interlocked.Decrement(ref _shownCount);
            if (kind is RequestKind.PopOut or RequestKind.ToAvaloniaDocked)
            {
                // Save the placement as open (popped out or docked), then let the
                // Avalonia face open: its toggle reads that entry, so a popped-out
                // one opens at its saved screen position.
                Persist(entry, open: true, placeScale, floating: kind == RequestKind.PopOut);
                UiBackgroundWriter.Enqueue($"hand off {title}", () => AvaloniaOverlay.ActivateBarButton(title));
            }
            else if (wasPopped)
            {
                // Closed while popped out: it reopens popped out, where it was.
                PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry prior);
                PersistPopped(entry, open: false, (int)prior.FloatingLeft, (int)prior.FloatingTop,
                    (int)(entry.Size.X), (int)(entry.Size.Y), placeScale, keepSize: true);
            }
            else
            {
                Persist(entry, open: false, placeScale);
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="entry"/>'s own window: at its saved popped-out
    /// position, or where it sits in the client. AC thread, main context current.
    /// </summary>
    private static void StartPopOut(Entry entry, float placeScale, bool fromSaved)
    {
        float s = UiScaleForChrome();
        Vector2 size = entry.Size;
        Vector2 origin = ImGuiPopOuts.ClientOriginOnScreen();
        int left = (int)(origin.X + entry.Pos.X), top = (int)(origin.Y + entry.Pos.Y);
        if (PanelStateStore.TryGetPanel(entry.Title, out PanelStateStore.PanelEntry saved)
            && (saved.FloatingLeft != 0 || saved.FloatingTop != 0) && (fromSaved || saved.Floating))
        {
            left = (int)Math.Round(saved.FloatingLeft);
            top = (int)Math.Round(saved.FloatingTop);
        }
        bool standard = entry.Spec.Chrome == PanelChrome.Standard;
        int caption = standard ? (int)(RynthTheme.HeaderHeight * s) : 0;
        int buttons = standard ? (int)HeaderButtonsWidth(entry, s, popped: true) : 0;
        ImGuiPopOuts.Open(entry.Title, left, top, (int)size.X, (int)size.Y, caption, buttons, s, resizable: !entry.Spec.NoResize);
        entry.Popped = true;
        PersistPopped(entry, open: true, left, top, (int)size.X, (int)size.Y, placeScale);
    }

    private static float UiScaleForChrome() => EngineFrameController.FontScale;

    private static void PlaceFromSavedState(Entry entry, float placeScale)
    {
        Vector2 size = entry.Spec.DefaultSize;
        Vector2 pos;
        string source;
        if (PanelStateStore.TryGetPanel(entry.Title, out PanelStateStore.PanelEntry saved) && (saved.Width > 0 || saved.Left != 0 || saved.Top != 0))
        {
            pos = new Vector2((float)saved.Left, (float)saved.Top);
            if (saved.Width > 0 && saved.Height > 0) size = new Vector2((float)saved.Width, (float)saved.Height);
            source = "its saved place";
        }
        else if (entry.Spec.DefaultPos is Vector2 defaultPos)
        {
            pos = defaultPos; // the face's own first spot (a script window's FirstUseEver position)
            source = "its first-open spot";
        }
        else if (entry.Spec.OpenCorner != PanelCorner.None && CornerLogicalPos(entry.Spec.OpenCorner, size, placeScale) is Vector2 corner)
        {
            pos = corner;
            source = "its first-open corner";
        }
        else if (entry.Spec.OpenCentered && CenteredLogicalPos(size, placeScale) is Vector2 centered)
        {
            pos = centered;
            source = "the centre";
        }
        else
        {
            int shown = 0;
            lock (Sync) foreach (Entry e in DrawOrder) if (e.Shown) shown++;
            pos = new Vector2(100 + shown * 20, 100 + shown * 20); // Avalonia's cascade
            source = "the cascade";
        }

        size = Vector2.Max(size, entry.Spec.MinSize);
        // Side-by-side check: the ImGui copy sits offset from the Avalonia one.
        if (PanelRouter.ResolveDockedFace(entry.Title) == PanelFace.Both)
            pos += new Vector2(24, 24);

        // Its home is the saved spot; where it draws is that pulled onto the screen (HomePlace), and
        // DrawPanel lets go of the pull once the panel fits (a bigger display, its real height).
        Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
        entry.Size = size * placeScale;
        entry.Place.Set(pos * placeScale, entry.Size, display);
        entry.Pos = entry.Place.Drawn;
        entry.PersistedPos = entry.Pos;
        entry.PersistedSize = entry.Size;
        entry.NeedsPlacement = true;
        entry.PullLogged = false;

        // Popped-out windows sit above the game frame, so a docked panel opening under
        // one would be invisible. The new spot is its home now and is saved by the open's
        // Persist, so the panel comes back where it could be seen.
        if (ClearOfPopOuts(entry.Pos, entry.Size) is Vector2 clear)
        {
            RynthLog.UI($"ImGuiPanelHost: {entry.Title} would open under a popped-out window; " +
                        $"moved from ({entry.Pos.X:0},{entry.Pos.Y:0}) to ({clear.X:0},{clear.Y:0}).");
            entry.Place.MoveTo(clear, entry.Size, display);
            entry.Pos = clear;
            entry.PersistedPos = clear;
            source = "a spot clear of popped-out windows";
        }
        LogPlacement(entry, source);
    }

    // Titles whose placement this engine load has logged (AC's render thread).
    private static readonly HashSet<string> PlacementLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One line per panel per engine load (its first placement: the login's or the hot reload's), and every
    /// placement that pulls it off its saved spot: where it draws, from what, and why. AC thread.
    /// </summary>
    private static void LogPlacement(Entry entry, string source)
    {
        if (!PlacementLogged.Add(entry.Title) && !entry.Place.Pulled) return;
        RynthLog.UI("ImGuiPanelHost: " + entry.Place.Describe(entry.Title, source) + $" Layout {PanelStateStore.CurrentPath}.");
    }

    /// <summary>Above this share of a docked panel hidden by popped-out windows, it opens elsewhere.</summary>
    private const float MaxCoveredFraction = 0.5f;
    /// <summary>Candidate spots per axis when looking for a clear place (a grid over the game view).</summary>
    private const int ClearSpotSteps = 8;

    /// <summary>
    /// A spot (client px) for a docked window of <paramref name="size"/> at <paramref name="pos"/>
    /// that popped-out windows hide less, nearest the original among the least-covered; null when
    /// at most half of it is covered or no spot is better.
    /// </summary>
    private static Vector2? ClearOfPopOuts(Vector2 pos, Vector2 size)
    {
        float area = size.X * size.Y;
        if (area <= 0) return null;
        List<(Vector2 Min, Vector2 Max)> covers = PopOutRectsInClient();
        if (covers.Count == 0) return null;
        float covered = CoveredArea(pos, size, covers);
        if (covered <= area * MaxCoveredFraction) return null;

        Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
        if (display.X <= 1 || display.Y <= 1) return null;
        Vector2 room = Vector2.Max(Vector2.Zero, display - size);

        // Pass 1: the least coverage any grid spot reaches. Pass 2: the nearest spot within
        // 2% of the panel's area of that, so a nearly-as-clear nearby spot beats a far one.
        float least = float.MaxValue;
        for (int i = 0; i <= ClearSpotSteps; i++)
            for (int j = 0; j <= ClearSpotSteps; j++)
                least = Math.Min(least, CoveredArea(GridSpot(room, i, j), size, covers));
        if (least >= covered) return null;

        float tolerance = least + area * 0.02f;
        Vector2? best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i <= ClearSpotSteps; i++)
            for (int j = 0; j <= ClearSpotSteps; j++)
            {
                Vector2 spot = GridSpot(room, i, j);
                if (CoveredArea(spot, size, covers) > tolerance) continue;
                float dist = Vector2.DistanceSquared(spot, pos);
                if (dist < bestDist) { bestDist = dist; best = spot; }
            }
        return best;
    }

    private static Vector2 GridSpot(Vector2 room, int i, int j) =>
        new(MathF.Round(room.X * i / ClearSpotSteps), MathF.Round(room.Y * j / ClearSpotSteps));

    /// <summary>Visible popped-out windows' rects in the game window's client px (ImGui coordinates).</summary>
    private static List<(Vector2 Min, Vector2 Max)> PopOutRectsInClient()
    {
        Vector2 origin = ImGuiPopOuts.ClientOriginOnScreen();
        var rects = new List<(Vector2, Vector2)>();
        foreach (var (left, top, width, height) in UI.LayeredWindow.VisibleContentRects())
        {
            var min = new Vector2(left, top) - origin;
            rects.Add((min, min + new Vector2(width, height)));
        }
        return rects;
    }

    /// <summary>Area of the window hidden by <paramref name="covers"/> (overlaps between covers count twice).</summary>
    private static float CoveredArea(Vector2 pos, Vector2 size, List<(Vector2 Min, Vector2 Max)> covers)
    {
        Vector2 max = pos + size;
        float total = 0;
        foreach (var (cMin, cMax) in covers)
        {
            float w = MathF.Min(max.X, cMax.X) - MathF.Max(pos.X, cMin.X);
            float h = MathF.Min(max.Y, cMax.Y) - MathF.Max(pos.Y, cMin.Y);
            if (w > 0 && h > 0) total += w * h;
        }
        return total;
    }

    /// <summary>
    /// Top-left (logical units) that centres a window of logical <paramref name="size"/> in the
    /// game view; null while the display size isn't known yet.
    /// </summary>
    private static Vector2? CenteredLogicalPos(Vector2 size, float placeScale)
    {
        Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
        if (display.X <= 1 || display.Y <= 1 || placeScale <= 0) return null;
        return Vector2.Max(Vector2.Zero, (display / placeScale - size) * 0.5f);
    }

    /// <summary>A corner of the game view, 8 units in from its edges (logical units).</summary>
    private static Vector2? CornerLogicalPos(PanelCorner corner, Vector2 size, float placeScale)
    {
        Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
        if (display.X <= 1 || display.Y <= 1 || placeScale <= 0) return null;
        Vector2 view = display / placeScale;
        const float m = 8f;
        float x = corner is PanelCorner.TopRight or PanelCorner.BottomRight ? view.X - size.X - m
                : corner is PanelCorner.Top or PanelCorner.Bottom ? (view.X - size.X) * 0.5f : m;
        float y = corner is PanelCorner.BottomLeft or PanelCorner.BottomRight or PanelCorner.Bottom ? view.Y - size.Y - m : m;
        return Vector2.Max(Vector2.Zero, new Vector2(x, y));
    }

    private static Vector2 ClampToDisplay(Vector2 pos, Vector2 size)
    {
        Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
        if (display.X <= 1 || display.Y <= 1) return pos;
        return new Vector2(
            Math.Clamp(pos.X, 0, Math.Max(0, display.X - size.X)),
            Math.Clamp(pos.Y, 0, Math.Max(0, display.Y - size.Y)));
    }

    /// <param name="popDisplay">The pop-out window's size when drawing popped out; null in the client.</param>
    private static void DrawPanel(Entry entry, float uiScale, float placeScale, Vector2? popDisplay)
    {
        PanelSpec spec = entry.Spec;
        bool popped = popDisplay.HasValue;
        // A bare face (IImGuiPanel.Bare): docked, no frame of any kind, only what it paints.
        bool bare = false;
        if (!popped) try { bare = entry.Instance?.Bare ?? false; } catch { }
        bool standard = spec.Chrome == PanelChrome.Standard && !bare;
        bool both = PanelRouter.ResolveDockedFace(entry.Title) == PanelFace.Both;

        Vector2 minSize = (entry.Instance?.MinSize ?? spec.MinSize) * placeScale;
        if (popped)
        {
            // The panel fills its window; moving and resizing are the window's.
            // (At the pop-out's panel origin: its display is the work area around it.)
            ImGuiNET.ImGui.SetNextWindowPos(ImGuiPopOuts.PanelOrigin, ImGuiCond.Always);
            ImGuiNET.ImGui.SetNextWindowSize(popDisplay!.Value, ImGuiCond.Always);
            minSize = Vector2.Zero;
        }
        else
        {
            if (entry.HasRequest)
            {
                Vector2? reqSize, reqPos;
                lock (Sync)
                {
                    reqSize = entry.RequestedSize;
                    reqPos = entry.RequestedPos;
                    entry.RequestedSize = null;
                    entry.RequestedPos = null;
                    entry.HasRequest = false;
                }
                if (reqSize is Vector2 requested)
                {
                    entry.Size = Vector2.Max(requested * placeScale, minSize);
                    entry.NeedsPlacement = true;
                    // A script's size request is its own, not the player's: not saved.
                    if (entry.Dynamic) entry.PersistedSize = entry.Size;
                }
                // A face asking for the spot it is drawn at (PluginWindowFace / RynthAiFace after a text
                // size change: "clamped to the screen at the new size") changes nothing here: the fit
                // below pulls it onto the screen from its home. Any other spot is its new home.
                if (reqPos is Vector2 requestedPos && !HomePlace.Near(requestedPos * placeScale, entry.Pos))
                {
                    entry.Place.MoveTo(requestedPos * placeScale, entry.Size, ImGuiNET.ImGui.GetIO().DisplaySize);
                    entry.Pos = entry.Place.Drawn;
                    entry.PersistedPos = entry.Pos;   // not a move by the player: nothing to save now
                    entry.NeedsPlacement = true;
                }
            }
            // Its size or the display changed: drawn from its home again, pulled onto the screen only
            // as far as needed (UI/PanelPlacement). Not a move by the player: nothing is saved.
            if (entry.Place.Fit(entry.Size, ImGuiNET.ImGui.GetIO().DisplaySize))
            {
                entry.Pos = entry.Place.Drawn;
                entry.PersistedPos = entry.Pos;
                entry.NeedsPlacement = true;
                if (entry.Place.Pulled && !entry.PullLogged)
                {
                    entry.PullLogged = true;
                    RynthLog.UI("ImGuiPanelHost: " + entry.Place.DescribePull(entry.Title));
                }
            }
            if (entry.NeedsPlacement)
            {
                ImGuiNET.ImGui.SetNextWindowPos(entry.Pos, ImGuiCond.Always);
                ImGuiNET.ImGui.SetNextWindowSize(entry.Size, ImGuiCond.Always);
                entry.NeedsPlacement = false;
            }
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(minSize, new Vector2(float.MaxValue, float.MaxValue));
        }

        uint background = bare ? 0u : spec.Background ?? RynthTheme.PanelGlass;
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, spec.Rounding);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, spec.Chrome == PanelChrome.None || bare ? 0f : 1f);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, background);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, spec.BorderColor ?? RynthTheme.PanelBorder);

        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoMove;
        bool clickThrough = false;
        // Popped out, a panel is always interactive (like the Avalonia pop-out was).
        if (!popped) try { clickThrough = entry.Instance?.ClickThrough ?? false; } catch { }
        // Under AC's UI: where an AC window covers the cursor the click is AC's (unless a press that
        // began on this panel is still held, so a drag over an AC window isn't dropped).
        bool underUi = spec.UnderUi && !popped;
        if (underUi && !clickThrough && !ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)
            && Compatibility.AcMouseOver.Current() == Compatibility.AcMouseTarget.AcWindow)
            clickThrough = true;
        if (clickThrough) flags |= ImGuiWindowFlags.NoInputs;

        bool childOpen = false;
        ImGuiNET.ImGui.Begin(entry.WindowId, flags);
        if (underUi)
        {
            UnderUiLayer.AddWindowList(ImGuiNET.ImGui.GetWindowDrawList());
            // Only hovered: the mouse moves still reach AC, so its own mouse-over follows the cursor
            // onto an AC window covering this one (as the nameplates do, MonsterHud.MouseMovesToGame).
            if (!clickThrough && !ImGuiNET.ImGui.IsAnyMouseDown()
                && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
                UnderUiHovered = true;
        }
        try
        {
            ImGuiNET.ImGui.PopStyleVar(3);  // the frame's own vars; the body gets normal padding
            float header = standard ? RynthTheme.HeaderHeight * uiScale : 0f;
            float grip = standard ? 18f * uiScale : 0f;

            if (standard && DrawHeader(entry, uiScale, both, popped))
                return; // closed, popped out or docked back this frame

            ImGuiNET.ImGui.SetCursorPos(new Vector2(0, header));
            // Per-panel text size (Settings > Interface): the face's fonts come from the
            // set baked at that size (ImGuiFonts.Get); the header above keeps its size.
            // (Until a size change has been re-baked, the nearest size that is.)
            ImGuiFonts.CurrentStep = ImGuiFonts.BakedStepNear(PanelTextScale.Get(entry.Title));
            // Chromeless panels (the dashboard) have no frame to hold the grip, so it
            // sits over the body's bottom-right corner. It is handled by hand instead
            // of as an ImGui item: an item there competed with the face's own items
            // (the drag-anywhere surface) and with the body's scrollbar, so resizing
            // worked only sometimes. The mouse is tested against the corner directly,
            // the resize follows the mouse from where the drag began (no per-frame
            // drift), and the face's window-drag is ignored while resizing.
            // A GripInBody face (standard chrome, no strip under the body) is handled
            // the same way, over the corner the face leaves free.
            bool inlineGrip = standard && spec.GripInBody;
            bool manualGrip = (!standard || inlineGrip) && !clickThrough && !popped   // popped: the window resizes itself
                && !spec.NoResize && !bare;                                          // bare: sized while framed
            Vector2 outerPos = ImGuiNET.ImGui.GetWindowPos(), outerSize = ImGuiNET.ImGui.GetWindowSize();
            float gSize = GripSize(entry, uiScale);
            Vector2 gMin = GripMin(entry, uiScale, outerPos + outerSize);
            bool resizing = false, gHot = false;
            if (manualGrip)
            {
                Vector2 mouse = ImGuiNET.ImGui.GetMousePos();
                bool over = ImGuiNET.ImGui.IsMouseHoveringRect(gMin, gMin + new Vector2(gSize, gSize), false)
                    && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                if (over && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _resizeTitle == null)
                {
                    _resizeTitle = entry.Title;
                    _resizeStartSize = outerSize;
                    _resizeStartMouse = mouse;
                }
                resizing = _resizeTitle == entry.Title;
                if (resizing && !ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    _resizeTitle = null;
                    resizing = false;
                }
                if (resizing)
                {
                    Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
                    Vector2 maxSize = display.X > 1 ? display - outerPos : new Vector2(float.MaxValue, float.MaxValue);
                    Vector2 next = Vector2.Clamp(_resizeStartSize + (mouse - _resizeStartMouse), minSize, Vector2.Max(minSize, maxSize));
                    if (next != outerSize) ImGuiNET.ImGui.SetWindowSize(next);
                    // Resized where it draws: that spot is the player's now (a pulled window doesn't jump back mid-drag).
                    entry.Place.MoveTo(outerPos, next, display);
                }
                gHot = over || resizing;
                if (gHot) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNWSE);
            }

            // While the body scrolls, end it above the corner so its scrollbar can't
            // take the grip's clicks. Held for the length of a resize so the body
            // doesn't flip between the two heights mid-drag.
            // (A popped GripInBody face too: its window's own corner grip sits there.)
            bool gripOverBody = manualGrip || (popped && inlineGrip);
            float gripRoom = standard && !inlineGrip ? grip
                : resizing ? entry.LastGripRoom
                : (entry.BodyScrolls && gripOverBody ? (gSize + (spec.GoldGrip ? 4f : 1f) * uiScale) : 0f);
            entry.LastGripRoom = gripRoom;
            Vector2 bodySize = ImGuiNET.ImGui.GetWindowSize() - new Vector2(0, header + gripRoom);
            if (bodySize.Y < 1) bodySize.Y = 1;
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, spec.EdgeToEdge ? Vector2.Zero : new Vector2(6, 4) * uiScale);
            ImGuiWindowFlags bodyFlags = ImGuiWindowFlags.NoBackground;
            if (spec.EdgeToEdge) bodyFlags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            bodyFlags |= spec.BodyFlags;
            if (clickThrough) bodyFlags |= ImGuiWindowFlags.NoInputs;
            childOpen = ImGuiNET.ImGui.BeginChild("##body", bodySize, ImGuiChildFlags.AlwaysUseWindowPadding, bodyFlags);
            ImGuiNET.ImGui.PopStyleVar();
            if (underUi && childOpen) UnderUiLayer.AddWindowList(ImGuiNET.ImGui.GetWindowDrawList());

            if (childOpen)
            {
                if (entry.Error != null)
                {
                    // Never a format string: the error names the panel key (a script window's
                    // key is script text, e.g. a hud called "50%s") and an exception message.
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.3f, 1f));
                    ImGuiNET.ImGui.PushTextWrapPos(0f);
                    ImGuiNET.ImGui.TextUnformatted(entry.Error);
                    ImGuiNET.ImGui.PopTextWrapPos();
                    ImGuiNET.ImGui.PopStyleColor();
                }
                else if (entry.Instance != null)
                {
                    // Only while the body covers the corner (not when it scrolls and ends above it).
                    BodyGripReserve = inlineGrip && gripRoom == 0f ? gSize + 1f * uiScale : 0f;
                    try { DrawBody(entry); }
                    finally { BodyGripReserve = 0f; }
                    // A click-through face that takes the wheel over itself (IImGuiPanel.ClickThroughWantsWheel).
                    if (clickThrough) try { if (entry.Instance.ClickThroughWantsWheel) WheelWanted = true; } catch { }
                }
                // Grip painted on top of the face when the body covers the corner
                // (a GripInBody face's even while it lets clicks through: its slot stays).
                if ((manualGrip || inlineGrip || (popped && !standard)) && gripRoom == 0f
                    && (!spec.GripOnHover || gHot || NearGrip(gMin, gSize)))
                    GripPaint(entry, uiScale, gMin, gHot, ImGuiNET.ImGui.GetWindowDrawList());
                entry.BodyScrolls = ImGuiNET.ImGui.GetScrollMaxY() > 0f;
            }
            ImGuiNET.ImGui.EndChild();
            childOpen = false;

            if (gripOverBody && gripRoom > 0f)
                GripPaint(entry, uiScale, gMin, gHot, ImGuiNET.ImGui.GetWindowDrawList());
            // Framed panels: the grip is an ordinary item in the frame's corner.
            // Popped out it's only painted: the window's own corner resizes it.
            if (standard && !inlineGrip && popped)
                GripPaint(entry, uiScale, GripMin(entry, uiScale, outerPos + outerSize), false, ImGuiNET.ImGui.GetWindowDrawList());
            else if (standard && !inlineGrip && !clickThrough)
                DrawGrip(entry, uiScale, minSize);
            if (resizing) _pendingDragDelta = Vector2.Zero;   // the face's window-drag doesn't also move it

            StepSizeWithWheel(entry);

            // A drag started from the panel's own surface (DragWindowWithLastItem).
            if (popped) _pendingDragDelta = Vector2.Zero;
            if (_pendingDragDelta != Vector2.Zero)
            {
                Vector2 next = ClampToDisplay(ImGuiNET.ImGui.GetWindowPos() + _pendingDragDelta, ImGuiNET.ImGui.GetWindowSize());
                ImGuiNET.ImGui.SetWindowPos(next);
                entry.Place.MoveTo(next, ImGuiNET.ImGui.GetWindowSize(), ImGuiNET.ImGui.GetIO().DisplaySize);   // the player's spot
                _pendingDragDelta = Vector2.Zero;
            }
        }
        finally
        {
            ImGuiFonts.CurrentStep = 0;
            if (childOpen) ImGuiNET.ImGui.EndChild();
            if (!popped)
            {
                // Popped out, Pos/Size keep the docked placement to return to.
                entry.Pos = ImGuiNET.ImGui.GetWindowPos();
                entry.Size = ImGuiNET.ImGui.GetWindowSize();
            }
            ImGuiNET.ImGui.End();
            ImGuiNET.ImGui.PopStyleColor(2);
        }

        // Persist once a move/resize has finished (not every frame of a drag).
        if (!popped && !ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)
            && (entry.Pos != entry.PersistedPos || entry.Size != entry.PersistedSize))
        {
            entry.PersistedPos = entry.Pos;
            entry.PersistedSize = entry.Size;
            Persist(entry, open: true, placeScale);   // its home (a drag made that the new spot)
        }
    }

    /// <summary>The panel whose face is drawing right now (names a text box's owner in the input log). AC thread.</summary>
    internal static string? DrawingTitle { get; private set; }

    // ── Embedding: a face drawn inside another panel (the Mini Remote's sections) ──

    /// <summary>An embedded copy of a face: its own instance, so the face's own window is unaffected.</summary>
    private sealed class EmbeddedFace
    {
        public required string Title;
        public required IImGuiPanel Instance;
        public long LastDrawnMs;
        public int Failures;
        public string? Error;
    }

    /// <summary>Embedded copies by host + title. AC thread only (docked and pop-out frames both run there).</summary>
    private static readonly Dictionary<string, EmbeddedFace> EmbeddedFaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An embedded copy not drawn for this long is hidden and dropped (its section was left).</summary>
    private const long EmbedIdleMs = 750;

    /// <summary>Faces that can't be embedded: the hosts themselves.</summary>
    private static readonly HashSet<string> NotEmbeddable = new(StringComparer.OrdinalIgnoreCase) { "Mini Remote" };

    /// <summary>The UI scale of the frame being drawn (docked or popped). AC thread.</summary>
    private static float _frameScale = 1f;

    /// <summary>True while an embedded copy draws: its window moves and size requests are ignored.</summary>
    [ThreadStatic] private static bool _inEmbed;

    /// <summary>True while an embedded copy of a face draws (inside another panel's section). AC thread.</summary>
    internal static bool DrawingEmbedded => _inEmbed;

    /// <summary>True when <paramref name="title"/> is registered and may be drawn inside another panel. Any thread.</summary>
    public static bool CanEmbed(string title) => !NotEmbeddable.Contains(title) && HasFace(title);

    /// <summary>A registered face's default window size (logical units). Any thread.</summary>
    public static bool TryGetDefaultSize(string title, out Vector2 size)
    {
        Entry? entry;
        lock (Sync) Entries.TryGetValue(title, out entry);
        size = entry?.Spec.DefaultSize ?? Vector2.Zero;
        return entry != null;
    }

    /// <summary>
    /// Draws a copy of the face <paramref name="title"/> into the current window, in a child of
    /// <paramref name="size"/> (pixels; 0 = fill). <paramref name="hostKey"/> separates the copies
    /// of different hosts. The copy is created on first draw and dropped (OnHidden) once it
    /// hasn't drawn for <see cref="EmbedIdleMs"/>. AC thread, inside a window.
    /// </summary>
    public static void DrawEmbedded(string hostKey, string title, Vector2 size)
    {
        Entry? entry;
        lock (Sync) Entries.TryGetValue(title, out entry);
        if (entry == null || NotEmbeddable.Contains(title))
        {
            ImGuiNET.ImGui.TextDisabled("Not available.");
            return;
        }

        string key = hostKey + "\u001f" + title;
        if (!EmbeddedFaces.TryGetValue(key, out EmbeddedFace? face))
        {
            try
            {
                IImGuiPanel instance = entry.Factory();
                instance.OnShown();
                face = new EmbeddedFace { Title = title, Instance = instance };
            }
            catch (Exception ex)
            {
                RynthLog.UI($"ImGuiPanelHost: embedding {title} failed - {ex.GetType().Name}: {ex.Message}");
                face = new EmbeddedFace { Title = title, Instance = new NullFace(), Error = $"{title} failed to open: {ex.Message}" };
            }
            EmbeddedFaces[key] = face;
        }
        face.LastDrawnMs = Environment.TickCount64;

        PanelSpec spec = entry.Spec;
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, spec.EdgeToEdge ? Vector2.Zero : new Vector2(6, 4) * _frameScale);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoBackground | spec.BodyFlags;
        if (spec.EdgeToEdge) flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        bool open = ImGuiNET.ImGui.BeginChild("##embed_" + title, size, ImGuiChildFlags.AlwaysUseWindowPadding, flags);
        ImGuiNET.ImGui.PopStyleVar();
        try
        {
            if (!open) return;
            if (face.Error != null)
            {
                // Never a format string: the error carries an exception message.
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.3f, 1f));
                ImGuiNET.ImGui.PushTextWrapPos(0f);
                ImGuiNET.ImGui.TextUnformatted(face.Error);
                ImGuiNET.ImGui.PopTextWrapPos();
                ImGuiNET.ImGui.PopStyleColor();
                return;
            }
            string? outer = DrawingTitle;
            _inEmbed = true;
            DrawingTitle = title;
            try
            {
                face.Instance.Draw();
            }
            catch (Exception ex)
            {
                face.Failures++;
                RynthLog.UI($"ImGuiPanelHost: embedded {title}.Draw threw {ex.GetType().Name}: {ex.Message} ({face.Failures}/{MaxFailures})");
                if (face.Failures >= MaxFailures)
                    face.Error = $"{title} stopped drawing after repeated errors: {ex.Message}";
            }
            finally
            {
                _inEmbed = false;
                DrawingTitle = outer;
            }
        }
        finally
        {
            ImGuiNET.ImGui.EndChild();
        }
    }

    /// <summary>Hides and drops embedded copies whose host stopped drawing them. AC thread.</summary>
    private static void SweepEmbedded()
    {
        if (EmbeddedFaces.Count == 0) return;
        long now = Environment.TickCount64;
        List<string>? stale = null;
        foreach (var kv in EmbeddedFaces)
            if (now - kv.Value.LastDrawnMs > EmbedIdleMs) (stale ??= new List<string>()).Add(kv.Key);
        if (stale == null) return;
        foreach (string key in stale)
        {
            EmbeddedFace face = EmbeddedFaces[key];
            EmbeddedFaces.Remove(key);
            try { face.Instance.OnHidden(); }
            catch (Exception ex) { RynthLog.UI($"ImGuiPanelHost: embedded {face.Title}.OnHidden threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    /// <summary>Stands in for a face that failed to open, so its error line can draw.</summary>
    private sealed class NullFace : IImGuiPanel
    {
        public void Draw() { }
    }

    private static void DrawBody(Entry entry)
    {
        DrawingTitle = entry.Title;
        try
        {
            entry.Instance!.Draw();
        }
        catch (Exception ex)
        {
            entry.Failures++;
            RynthLog.UI($"ImGuiPanelHost: {entry.Title}.Draw threw {ex.GetType().Name}: {ex.Message} ({entry.Failures}/{MaxFailures})");
            if (entry.Failures >= MaxFailures)
                entry.Error = $"{entry.Title} stopped drawing after repeated errors: {ex.Message}";
        }
        finally
        {
            DrawingTitle = null;
        }
    }

    private static float HeaderButtonsWidth(Entry entry, float s, bool popped)
    {
        float btn = 20f * s;
        bool second = popped || (PanelRouter.CanPopOut && !entry.Spec.NoPopOut);
        // close, [pop-out / dock back], text size
        return btn + 2 * s + (second ? btn + 2 * s : 0) + btn + 4 * s;
    }

    /// <summary>The standard header. Returns true when the panel was closed, popped out or docked back.</summary>
    private static bool DrawHeader(Entry entry, float s, bool bothTag, bool popped)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 winPos = ImGuiNET.ImGui.GetWindowPos();
        float width = ImGuiNET.ImGui.GetWindowWidth();
        float h = RynthTheme.HeaderHeight * s;
        float r = entry.Spec.Rounding;
        dl.AddRectFilled(winPos, winPos + new Vector2(width, h), RynthTheme.PanelHeader, r, ImDrawFlags.RoundCornersTop);

        float btn = 20f * s;
        // In Both (side-by-side) the Avalonia copy owns pop-out.
        bool canPopOut = popped || (PanelRouter.CanPopOut && !bothTag && !entry.Spec.NoPopOut);
        float buttonsWidth = HeaderButtonsWidth(entry, s, popped);

        // Drag surface: the header minus the buttons. Popped out, the window drags
        // itself from this band (LayeredWindow.CaptionHeight) and never shows it to ImGui.
        ImGuiNET.ImGui.SetCursorPos(Vector2.Zero);
        ImGuiNET.ImGui.InvisibleButton("##drag", new Vector2(Math.Max(1, width - buttonsWidth), h));
        if (!popped && ImGuiNET.ImGui.IsItemActive() && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            Vector2 next = ClampToDisplay(winPos + ImGuiNET.ImGui.GetIO().MouseDelta, ImGuiNET.ImGui.GetWindowSize());
            ImGuiNET.ImGui.SetWindowPos(next);
            entry.Place.MoveTo(next, ImGuiNET.ImGui.GetWindowSize(), ImGuiNET.ImGui.GetIO().DisplaySize);   // the player's spot
        }

        // Grip dots + DRAG (teal, 9) + title (10), vertically centred.
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        float dragY = (h - ImGuiNET.ImGui.GetTextLineHeight()) * 0.5f;
        dl.AddText(winPos + new Vector2(6 * s, dragY), RynthTheme.DragLabel, DragLabel);
        float dragW = ImGuiNET.ImGui.CalcTextSize(DragLabel).X - 2 * s;
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
        float titleY = (h - ImGuiNET.ImGui.GetTextLineHeight()) * 0.5f;
        string title = bothTag ? entry.BothTitle : entry.DisplayTitle;
        dl.AddText(winPos + new Vector2(8 * s + dragW + 6 * s, titleY), ImGuiNET.ImGui.GetColorU32(ImGuiCol.Text), title);

        bool gone = false;
        float y = (h - btn) * 0.5f;
        float x = width - 4 * s - btn;
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGuiNET.ImGui.SetCursorPos(new Vector2(x, y));
        if (HeaderIconButton("##close", PhosphorIcons.X, btn))
        {
            Close(entry.Title);
            gone = true;
        }
        ImGuiNET.ImGui.SetItemTooltip("Close");
        if (canPopOut)
        {
            ImGuiNET.ImGui.SetCursorPos(new Vector2(x - 2 * s - btn, y));
            if (popped)
            {
                if (HeaderIconButton("##redock", PhosphorIcons.ArrowSquareIn, btn))
                {
                    Redock(entry.Title);
                    gone = true;
                }
                ImGuiNET.ImGui.SetItemTooltip("Dock back into the game window");
            }
            else
            {
                if (HeaderIconButton("##popout", PhosphorIcons.ArrowSquareOut, btn))
                {
                    PopOut(entry.Title);
                    gone = true;
                }
                ImGuiNET.ImGui.SetItemTooltip("Pop out into a floating window");
            }
        }

        // Text size (the same per-panel setting as Settings > Interface).
        float tsX = x - (canPopOut ? btn + 2 * s : 0) - 2 * s - btn;
        ImGuiNET.ImGui.SetCursorPos(new Vector2(tsX, y));
        int step = PanelTextScale.Get(entry.Title);
        if (HeaderIconButton("##textsize", PhosphorIcons.TextAa, btn, step != 0 ? RynthTheme.DragLabel : (uint?)null))
            ImGuiNET.ImGui.OpenPopup(TextSizePopupId);
        if (ImGuiNET.ImGui.BeginItemTooltip())
        {
            ImGuiNET.ImGui.TextUnformatted("Text size: " + PanelTextScale.Describe(step));
            ImGuiNET.ImGui.TextDisabled("Click to change. Ctrl+wheel on the title bar steps it.");
            ImGuiNET.ImGui.EndTooltip();
        }
        ImGuiNET.ImGui.PopStyleVar();

        // Ctrl+wheel anywhere over the panel steps its size (StepSizeWithWheel, for every panel).

        // Under the header, right-aligned to the button: never over a pop-out's drag band.
        ImGuiNET.ImGui.SetNextWindowPos(winPos + new Vector2(tsX + btn, h), ImGuiCond.Appearing, new Vector2(1, 0));
        DrawTextSizePopup(entry.Title, s);

        ImGuiNET.ImGui.PopFont();
        return gone;
    }

    private const string TextSizePopupId = "##textsize_pop";

    private static string[]? _textSizeMenuLabels;

    /// <summary>
    /// A "Text size" submenu for a chromeless panel's own menu (it has no title bar
    /// to hold the button): the steps, then "Use for all windows". AC thread.
    /// </summary>
    public static void TextSizeMenu(string title)
    {
        if (!ImGuiNET.ImGui.BeginMenu("Text size")) return;
        try
        {
            string[] labels = _textSizeMenuLabels ??= BuildTextSizeMenuLabels();
            int step = PanelTextScale.Get(title);
            for (int i = 0; i < labels.Length; i++)
                if (ImGuiNET.ImGui.MenuItem(labels[i], "", i == step) && i != step)
                    PanelTextScale.SetForWindow(title, i);
            ImGuiNET.ImGui.Separator();
            if (ImGuiNET.ImGui.MenuItem("Use for all windows"))
                PanelTextScale.UseForAll(step);
        }
        finally
        {
            ImGuiNET.ImGui.EndMenu();
        }
    }

    private static string[] BuildTextSizeMenuLabels()
    {
        var labels = new string[PanelTextScale.SizeNames.Length];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = i == 0 ? "Normal" : PanelTextScale.Describe(i);
        return labels;
    }

    /// <summary>The title bar's text size popup: smaller / bigger, a slider over the steps, reset, use everywhere.</summary>
    private static void DrawTextSizePopup(string title, float s)
    {
        if (!ImGuiNET.ImGui.BeginPopup(TextSizePopupId)) return;
        try
        {
            TextSizeRows(title, s);
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }

    /// <summary>
    /// The text size popup's rows for <paramref name="title"/> at the cursor: smaller / bigger and the
    /// size, a slider over the steps, reset, use for all windows. The title bar's popup, and a chromeless
    /// panel's own popup (the RynthAi dashboard's title row). <paramref name="s"/> scales the buttons.
    /// AC thread, inside a popup.
    /// </summary>
    public static void TextSizeRows(string title, float s, string heading = "Text size")
    {
        int step = PanelTextScale.Get(title);
        int max = PanelTextScale.SizeNames.Length - 1;
        int all = PanelTextScale.GetAll();
        float btn = 20f * s;

        ImGuiNET.ImGui.TextUnformatted(heading);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGuiNET.ImGui.BeginDisabled(step <= 0);
        if (HeaderIconButton("##ts_smaller", PhosphorIcons.Minus, btn))
            PanelTextScale.SetForWindow(title, step - 1);
        ImGuiNET.ImGui.EndDisabled();
        ImGuiNET.ImGui.SetItemTooltip("Smaller");
        ImGuiNET.ImGui.SameLine(0, 4 * s);
        ImGuiNET.ImGui.BeginDisabled(step >= max);
        if (HeaderIconButton("##ts_bigger", PhosphorIcons.Plus, btn))
            PanelTextScale.SetForWindow(title, step + 1);
        ImGuiNET.ImGui.EndDisabled();
        ImGuiNET.ImGui.SetItemTooltip("Bigger");
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.SameLine(0, 8 * s);
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted(PanelTextScale.Describe(step));

        int pick = step;
        ImGuiNET.ImGui.SetNextItemWidth(btn * 2 + 4 * s + 8 * s + 110 * s);
        if (ImGuiNET.ImGui.SliderInt("##ts_step", ref pick, 0, max, "%d", ImGuiSliderFlags.AlwaysClamp) && pick != step)
            PanelTextScale.SetForWindow(title, pick);

        ImGuiNET.ImGui.BeginDisabled(step == all);
        if (ImGuiNET.ImGui.Button(PhosphorIcons.ArrowCounterClockwise + " Reset"))
            PanelTextScale.ResetWindow(title);
        ImGuiNET.ImGui.EndDisabled();
        SafeImGui.SetItemTooltip(all == 0 ? "Back to normal size" : "Back to the All Panels size (Settings > Interface)");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.Button("Use for all windows"))
            PanelTextScale.UseForAll(step);
        ImGuiNET.ImGui.SetItemTooltip("Every in-game window at this size (Settings > Interface > All Panels)");
    }

    private const string DragLabel = PhosphorIcons.DotsSixVertical + "DRAG";

    /// <summary>A header button (the theme's button look) with an icon centred on it.</summary>
    private static bool HeaderIconButton(string id, string icon, float size, uint? color = null)
    {
        Vector2 at = ImGuiNET.ImGui.GetCursorScreenPos();
        bool clicked = ImGuiNET.ImGui.Button(id, new Vector2(size, size));
        PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiFonts.Get(UiFont.Ui14), icon, at,
            new Vector2(size, size), color ?? ImGuiNET.ImGui.GetColorU32(ImGuiCol.Text));
        return clicked;
    }

    /// <summary>The resize grip at the bottom-right (16 px, teal, rounded outer corner).</summary>
    private static float GripSize(Entry entry, float s) => (entry.Spec.GoldGrip ? 14f : 16f) * s;

    /// <summary>The mouse within a grip's width of the grip (PanelSpec.GripOnHover shows it then).</summary>
    private static bool NearGrip(Vector2 gMin, float gSize) =>
        ImGuiNET.ImGui.IsMouseHoveringRect(gMin - new Vector2(gSize, gSize), gMin + new Vector2(2 * gSize, 2 * gSize), false);

    /// <summary>Top-left of the grip for a corner (bottom-right point).</summary>
    private static Vector2 GripMin(Entry entry, float s, Vector2 corner)
    {
        float size = GripSize(entry, s);
        float inset = (entry.Spec.GoldGrip ? 4f : 1f) * s;
        return corner - new Vector2(size + inset, size + inset);
    }

    private static bool DrawGrip(Entry entry, float s, Vector2 minSize)
    {
        Vector2 winPos = ImGuiNET.ImGui.GetWindowPos();
        Vector2 winSize = ImGuiNET.ImGui.GetWindowSize();
        Vector2 min = GripMin(entry, s, winPos + winSize);
        bool hot = GripInteract(entry, s, minSize, min, winPos, winSize, null);
        GripPaint(entry, s, min, hot, ImGuiNET.ImGui.GetWindowDrawList());
        return hot;
    }

    /// <summary>
    /// The grip's hit area at <paramref name="min"/>; dragging it resizes the panel
    /// window (<paramref name="windowName"/>, or the current window when null).
    /// Returns true while hovered or dragged.
    /// </summary>
    private static bool GripInteract(Entry entry, float s, Vector2 minSize, Vector2 min, Vector2 winPos, Vector2 winSize,
        string? windowName)
    {
        float size = GripSize(entry, s);
        ImGuiNET.ImGui.SetCursorScreenPos(min);
        ImGuiNET.ImGui.InvisibleButton("##grip", new Vector2(size, size));
        bool hot = ImGuiNET.ImGui.IsItemHovered() || ImGuiNET.ImGui.IsItemActive();
        if (hot) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNWSE);
        if (ImGuiNET.ImGui.IsItemActive() && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
            Vector2 maxSize = display.X > 1 ? display - winPos : new Vector2(float.MaxValue, float.MaxValue);
            Vector2 next = Vector2.Clamp(winSize + ImGuiNET.ImGui.GetIO().MouseDelta, minSize, Vector2.Max(minSize, maxSize));
            if (windowName != null) ImGuiNET.ImGui.SetWindowSize(windowName, next);
            else ImGuiNET.ImGui.SetWindowSize(next);
            // Resized where it draws: that spot is the player's now (a pulled window doesn't jump back mid-drag).
            entry.Place.MoveTo(winPos, next, display);
        }
        return hot;
    }

    private static void GripPaint(Entry entry, float s, Vector2 min, bool hot, ImDrawListPtr dl)
    {
        bool gold = entry.Spec.GoldGrip;
        float size = GripSize(entry, s);
        Vector2 max = min + new Vector2(size, size);
        uint accent = gold ? RynthTheme.RadarGold : RynthTheme.GripGlyph;
        uint fill = gold ? (hot ? RynthTheme.RadarGripHover : RynthTheme.RadarGripIdle) : (hot ? RynthTheme.GripHover : RynthTheme.GripIdle);
        dl.AddRectFilled(min, max, fill, gold ? 0f : 4f * s, ImDrawFlags.RoundCornersBottomRight);
        dl.AddLine(min, new Vector2(max.X, min.Y), accent);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.Resize, min, new Vector2(size, size), accent);
    }

    /// <summary>
    /// Saves open state and docked placement (converted back to Avalonia
    /// logical units), keeping the popped-out screen position. Written off
    /// AC's thread, in order with the router's own writes.
    /// </summary>
    /// <summary>
    /// Saves a popped-out panel: open state, its window's screen position and size
    /// (the size in Avalonia logical units, like the docked size), and the docked
    /// position it returns to. Written off AC's thread, in order.
    /// </summary>
    private static void PersistPopped(Entry entry, bool open, int left, int top, int width, int height, float placeScale, bool keepSize = false)
    {
        string title = entry.Title;
        Vector2 docked = DockedHome(entry) / placeScale;
        Vector2 size = new Vector2(width, height) / placeScale;
        UiBackgroundWriter.Enqueue($"persist {title}", () =>
        {
            PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry prior);
            double w = keepSize && prior.Width > 0 ? prior.Width : size.X;
            double h = keepSize && prior.Height > 0 ? prior.Height : size.Y;
            PanelStateStore.SetPanel(title, new PanelStateStore.PanelEntry(
                open, docked.X, docked.Y, w, h, true, left, top));
        });
    }

    /// <summary>
    /// The docked spot to save (client px): the panel's home (HomePlace), not where it draws: a pull onto
    /// a smaller screen or for a taller moment is never written over the saved spot.
    /// </summary>
    private static Vector2 DockedHome(Entry entry) => entry.Place.IsSet ? entry.Place.Home : entry.Pos;

    private static void Persist(Entry entry, bool open, float placeScale, bool floating = false)
    {
        string title = entry.Title;
        Vector2 pos = DockedHome(entry) / placeScale;
        Vector2 size = entry.Size / placeScale;
        if (PanelRouter.ResolveDockedFace(title) == PanelFace.Both)
            pos -= new Vector2(24, 24); // don't let the side-by-side offset drift the Avalonia face
        UiBackgroundWriter.Enqueue($"persist {title}", () =>
        {
            PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry prior);
            PanelStateStore.SetPanel(title, new PanelStateStore.PanelEntry(
                open, pos.X, pos.Y, size.X, size.Y, floating, prior.FloatingLeft, prior.FloatingTop));
        });
    }
}
