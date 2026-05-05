// ============================================================================
//  RynthCore.Engine — UI/Panels/RadarPanel.cs
//  Avalonia replica of the ImGui RynthRadar (RynthRadarUi.cs).
//
//  Renders the same view, fed by the plugin's RynthPluginGetRadarSnapshot
//  export. Geometry is cached by MapVersion (= landblock); per-frame data
//  (player pose, visited overlay, markers) refreshes every 100 ms.
//
//  Render order matches the ImGui original:
//    1. Frame outer/inner/accent + canvas bg
//    2. Per-layer (curIdx-1, curIdx+1 dimmed, curIdx normal):
//         - fill strips (flat green / non-flat dim blue)
//         - wall segments (white, blue if cell visited & current)
//         - visited overlay (current layer only)
//    3. Markers (monsters, NPCs, portals, doors)
//    4. Player dot + heading arrow
//    5. Compass cardinals (N/S/E/W)
//    6. Player coord readout below
//
//  MVP scope: square mode, north-up. Rotate-with-player and circular mode
//  are wired but default off; settings popup, mouse-wheel zoom, and floor
//  textures are deferred.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

/// <summary>
/// Disk-backed persistence for radar settings (zoom, opacity, rotate, marker
/// visibility). Settings live under %LocalAppData%\RynthCore\radar_settings.txt
/// so they survive engine reloads, plugin reloads, and AC restarts.
/// </summary>
internal static class RadarSettingsStore
{
    private static readonly object _sync = new();
    private static bool _loaded;

    public static float Zoom = 1.5f;
    public static float Opacity = 1.0f;
    public static bool RotateWithPlayer;
    public static bool ShowGoldRim = true;
    public static bool CtrlGatedClickThrough = false;
    public static bool ShowMonsters = true;
    public static bool ShowNpcs = true;
    public static bool ShowPortals = true;
    public static bool ShowDoors = true;

    private static string FilePath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "RynthCore", "radar_settings.txt");
        }
    }

    public static void Load()
    {
        lock (_sync)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line[..eq].Trim();
                    string val = line[(eq + 1)..].Trim();
                    switch (key)
                    {
                        case "zoom":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                                Zoom = Math.Clamp(z, 0.5f, 6.0f);
                            break;
                        case "opacity":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float o))
                                Opacity = Math.Clamp(o, 0.05f, 1.0f);
                            break;
                        case "rotate":           RotateWithPlayer        = val == "1"; break;
                        case "showGoldRim":      ShowGoldRim             = val == "1"; break;
                        case "ctrlClickThrough": CtrlGatedClickThrough   = val == "1"; break;
                        case "showMonsters":     ShowMonsters            = val == "1"; break;
                        case "showNpcs":     ShowNpcs         = val == "1"; break;
                        case "showPortals":  ShowPortals      = val == "1"; break;
                        case "showDoors":    ShowDoors        = val == "1"; break;
                    }
                }
            }
            catch { /* best-effort: defaults already in place */ }
        }
    }

    public static void Save()
    {
        lock (_sync)
        {
            try
            {
                string path = FilePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var sw = new StreamWriter(path, append: false);
                sw.WriteLine("# RynthCore radar settings — auto-generated, hand-edits OK.");
                sw.WriteLine(FormattableString.Invariant($"zoom={Zoom:0.##}"));
                sw.WriteLine(FormattableString.Invariant($"opacity={Opacity:0.##}"));
                sw.WriteLine($"rotate={(RotateWithPlayer ? "1" : "0")}");
                sw.WriteLine($"showGoldRim={(ShowGoldRim ? "1" : "0")}");
                sw.WriteLine($"ctrlClickThrough={(CtrlGatedClickThrough ? "1" : "0")}");
                sw.WriteLine($"showMonsters={(ShowMonsters ? "1" : "0")}");
                sw.WriteLine($"showNpcs={(ShowNpcs ? "1" : "0")}");
                sw.WriteLine($"showPortals={(ShowPortals ? "1" : "0")}");
                sw.WriteLine($"showDoors={(ShowDoors ? "1" : "0")}");
            }
            catch { /* non-fatal */ }
        }
    }
}

internal static partial class RadarPanel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetRadarSnapshotFn(uint mapVersion);

    private static GetRadarSnapshotFn? _getRadarSnapshot;

    // ── Chrome-less integration hooks ───────────────────────────────────────
    // The radar lives inside the panel system but visually has no window
    // chrome — just the gold-bordered square + coords below. The overlay
    // sets these before invoking the factory so the panel can wire drag and
    // expose popout/redock/close on a right-click context menu.
    public static Action<Avalonia.Input.InputElement>? AttachDragHandle;
    public static Action? RequestPopOut;     // overlay → PopOutPanel("Radar")
    public static Action? RequestRedock;     // overlay → RedockPanel("Radar")
    public static Action? RequestClose;      // overlay → close active panel
    public static Func<bool>? IsFloatingNow; // tells the panel which menu items to show
    // Settings popup hosting: the overlay parents the popup to _desktopCanvas
    // as a sibling of the radar instead of an in-grid child, so its size is
    // independent of the radar's size (Canvas children get unbounded
    // available space, sized to their natural content). The overlay positions
    // the popup relative to the radar and tears it down when the radar
    // closes / pops out.
    public static Action<Control>? AttachSettingsPopup;

    // Popout-mode click forwarders. Avalonia's WM_LBUTTONDOWN dispatch fails
    // for off-canvas panels (panels parked at canvas X ≈ 2500), so the
    // FloatingPanelHost intercepts mouse events, hit-tests via InputHitTest
    // (which walks the visual tree directly), and invokes these forwarders
    // by Tag when it finds a radar button. Fired on the UI thread.
    public static Action? OnGearClicked;
    public static Action? OnPopoutClicked;
    public static Action? OnRedockClicked;
    // Popout-mode wheel forwarder. WM_MOUSEWHEEL on the LayeredWindow goes
    // to FloatingPanelHost.ForwardInput, which invokes this with the
    // Avalonia-equivalent Delta.Y (wheel ticks; one notch = 1.0). Mirrors
    // the surface.PointerWheelChanged handler used in docked mode.
    public static Action<double>? OnWheelDelta;

    // Button bounds snapshot for popout-mode click routing. Refreshed on
    // every layout update on the UI thread; read atomically (volatile array
    // reference) by FloatingPanelHost.ForwardInput on the WndProc thread.
    public readonly record struct ButtonHit(string Tag, double X, double Y, double W, double H);
    public static volatile ButtonHit[]? ButtonBoundsSnapshot;

    // True while the gear-popup settings panel is open. FloatingPanelHost
    // checks this to shrink the LayeredWindow's HTCAPTION drag region —
    // otherwise the OS swallows clicks on the settings checkboxes/sliders
    // because they sit inside the radar's caption-drag zone.
    public static volatile bool IsSettingsVisible;

    // ── Color palette — direct mapping from RynthRadarUi.cs Vector4 floats
    //    (×255, rounded). Alpha channels carried explicitly on the brush.
    // The two backdrop fills (frame outer black + canvas bg) are kept as raw
    // colours so Render can multiply their alpha by the opacity setting; the
    // gold rim and world details use IBrush directly and stay fully opaque.
    private static readonly Color  ColFrameOuterRgb  = Color.FromArgb(0xFF, 0x0A, 0x0D, 0x14);
    private static readonly Color  ColCanvasBgRgb    = Color.FromArgb(0xFF, 0x0A, 0x12, 0x1A);
    private static readonly IBrush ColFrameInner  = new SolidColorBrush(Color.FromArgb(0xFF, 0x9E, 0x85, 0x3D));
    private static readonly IBrush ColFrameAccent = new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0xC7, 0x66));
    private static readonly IBrush ColCrosshair   = new SolidColorBrush(Color.FromArgb(0x99, 0x4D, 0x66, 0x80));
    private static readonly IBrush ColCardinal    = Brushes.White;
    private static readonly IBrush ColCoordText   = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF8, 0xFF));
    private static readonly IBrush ColTextOutline = new SolidColorBrush(Color.FromArgb(0xF2, 0x00, 0x00, 0x00));

    // Wall colors
    private static readonly Color ColWallUnseen  = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color ColWallVisited = Color.FromArgb(0xFF, 0x40, 0x99, 0xFF);

    // Floor fill colors. Slope tints match DungeonMapUi.cs:
    //   SlopeUp   (0.55, 0.12, 0.08, 0.40) → leads to higher floor (red)
    //   SlopeDown (0.10, 0.50, 0.15, 0.40) → leads to lower floor  (green)
    private static readonly Color ColFloorFlatUnvisited = Color.FromArgb(0x8C, 0x2E, 0x8C, 0x38);
    private static readonly Color ColFloorSlopeUp       = Color.FromArgb(0x66, 0x8C, 0x1F, 0x14);
    private static readonly Color ColFloorSlopeDown     = Color.FromArgb(0x66, 0x1A, 0x80, 0x26);
    private static readonly Color ColFloorVisited       = Color.FromArgb(0xA6, 0x4D, 0x6B, 0x94);

    // Markers
    private static readonly IBrush ColPlayer  = new SolidColorBrush(Color.FromRgb(0x33, 0xFF, 0x66));
    private static readonly IBrush ColMonster = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x33));
    private static readonly IBrush ColNpc     = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x33));
    private static readonly IBrush ColPortal  = new SolidColorBrush(Color.FromRgb(0xBF, 0x4D, 0xFF));
    private static readonly IBrush ColDoor    = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x1A));
    private static readonly IBrush ColDoorEdge = new SolidColorBrush(Color.FromRgb(0x66, 0x33, 0x00));

    // ── Snapshot DTO (mirrors RynthCore.Plugin.RynthAi.LegacyUi.RadarSnapshotPayload) ──
    public sealed class Snapshot
    {
        [JsonPropertyName("mapVersion")]       public uint MapVersion       { get; set; }
        [JsonPropertyName("geometryIncluded")] public bool GeometryIncluded { get; set; }
        [JsonPropertyName("isIndoor")]         public bool IsIndoor         { get; set; }
        [JsonPropertyName("player")]           public PlayerInfo Player    { get; set; } = new();
        [JsonPropertyName("ns")]               public double Ns             { get; set; } = double.NaN;
        [JsonPropertyName("ew")]               public double Ew             { get; set; } = double.NaN;
        [JsonPropertyName("layerZs")]          public List<float> LayerZs   { get; set; } = new();
        [JsonPropertyName("currentLayerZ")]    public float CurrentLayerZ   { get; set; }
        [JsonPropertyName("walls")]            public List<WallLayer> Walls { get; set; } = new();
        [JsonPropertyName("fills")]            public List<FillLayer> Fills { get; set; } = new();
        [JsonPropertyName("visited")]          public List<VisitedLayer> Visited { get; set; } = new();
        [JsonPropertyName("markers")]          public List<Marker> Markers { get; set; } = new();
    }

    public sealed class PlayerInfo
    {
        [JsonPropertyName("cellId")]    public uint  CellId    { get; set; }
        [JsonPropertyName("landblock")] public uint  Landblock { get; set; }
        [JsonPropertyName("x")]         public float X         { get; set; }
        [JsonPropertyName("y")]         public float Y         { get; set; }
        [JsonPropertyName("z")]         public float Z         { get; set; }
        [JsonPropertyName("worldX")]    public float WorldX    { get; set; }
        [JsonPropertyName("worldY")]    public float WorldY    { get; set; }
        [JsonPropertyName("heading")]   public float Heading   { get; set; }
    }

    public sealed class WallLayer
    {
        [JsonPropertyName("z")]        public float   Z        { get; set; }
        [JsonPropertyName("segments")] public float[] Segments { get; set; } = Array.Empty<float>();
    }

    public sealed class FillLayer
    {
        [JsonPropertyName("z")]      public float   Z      { get; set; }
        [JsonPropertyName("strips")] public float[] Strips { get; set; } = Array.Empty<float>();
    }

    public sealed class VisitedLayer
    {
        [JsonPropertyName("z")]      public float   Z      { get; set; }
        [JsonPropertyName("strips")] public float[] Strips { get; set; } = Array.Empty<float>();
    }

    public sealed class Marker
    {
        [JsonPropertyName("kind")]  public byte    Kind  { get; set; }
        [JsonPropertyName("x")]     public float   X     { get; set; }
        [JsonPropertyName("y")]     public float   Y     { get; set; }
        [JsonPropertyName("z")]     public float   Z     { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
    }

    [JsonSerializable(typeof(Snapshot))]
    [JsonSourceGenerationOptions(WriteIndented = false)]
    internal partial class RadarJsonContext : JsonSerializerContext { }

    // ── Per-instance state, captured by the surface control ────────────────
    private sealed class State
    {
        // Live data (refreshed every poll).
        public Snapshot Live = new();

        // Cached geometry, indexed by MapVersion. Walls/fills don't change
        // until the player crosses landblocks — store and reuse.
        public uint CachedMapVersion;
        public List<WallLayer> CachedWalls = new();
        public List<FillLayer> CachedFills = new();

        // User-controlled settings — driven by the gear popup. Loaded from
        // RadarSettingsStore on construction; Persist() writes back.
        public float Zoom;                   // wheel + slider; 0.5..6.0
        public float Opacity;                // slider; 0.05..1.0 — applied to the canvas backdrop only
        public bool RotateWithPlayer;        // false: north-up
        public bool ShowGoldRim;             // false: no gold/black frame, canvas fills the panel
        public bool CtrlGatedClickThrough;   // true: radar interactive only while Ctrl is held
        public bool ShowMonsters;
        public bool ShowNpcs;
        public bool ShowPortals;
        public bool ShowDoors;

        public State()
        {
            RadarSettingsStore.Load();
            Zoom                  = RadarSettingsStore.Zoom;
            Opacity               = RadarSettingsStore.Opacity;
            RotateWithPlayer      = RadarSettingsStore.RotateWithPlayer;
            ShowGoldRim           = RadarSettingsStore.ShowGoldRim;
            CtrlGatedClickThrough = RadarSettingsStore.CtrlGatedClickThrough;
            ShowMonsters          = RadarSettingsStore.ShowMonsters;
            ShowNpcs              = RadarSettingsStore.ShowNpcs;
            ShowPortals           = RadarSettingsStore.ShowPortals;
            ShowDoors             = RadarSettingsStore.ShowDoors;
            // Mirror the click-through flag where the input plumbing reads it.
            AvaloniaOverlay.RadarCtrlGatedClickThrough = CtrlGatedClickThrough;
        }

        /// <summary>Copy current settings into the store and write them to disk.</summary>
        public void Persist()
        {
            RadarSettingsStore.Zoom                  = Zoom;
            RadarSettingsStore.Opacity               = Opacity;
            RadarSettingsStore.RotateWithPlayer      = RotateWithPlayer;
            RadarSettingsStore.ShowGoldRim           = ShowGoldRim;
            RadarSettingsStore.CtrlGatedClickThrough = CtrlGatedClickThrough;
            RadarSettingsStore.ShowMonsters          = ShowMonsters;
            RadarSettingsStore.ShowNpcs              = ShowNpcs;
            RadarSettingsStore.ShowPortals           = ShowPortals;
            RadarSettingsStore.ShowDoors             = ShowDoors;
            RadarSettingsStore.Save();
            AvaloniaOverlay.RadarCtrlGatedClickThrough = CtrlGatedClickThrough;
        }
    }

    // Settings + cached geometry persist across rebuilds (every dock⇄undock
    // transition rebuilds the panel from the factory, so without this the
    // user's zoom/rotate/visibility toggles would reset on every redock).
    private static State? _persistentState;

    public static Control Create()
    {
        TryBind();
        State state = _persistentState ??= new State();

        var surface = new RadarSurface(state);
        // Coord readout lives inside the radar surface (bottom-center, on the
        // canvas) — see RadarSurface.DrawCoordReadout. No separate TextBlock,
        // so the radar fills the entire panel edge-to-edge.

        // Top-right overlay buttons (inside the gold frame, so the radar can
        // be docked at the far edge of the AC client without buttons getting
        // clipped): pop-out / redock (mode-aware) + gear (always visible).
        //
        // Native LayeredWindow close/redock zones are disabled for Radar
        // (CloseButtonWidthPx = 0, RedockButtonWidthPx = 0 — see PopOutPanel),
        // so all clicks flow through Avalonia. Each handler logs once so we
        // can verify off-canvas hit-test routing in popout.
        var redockBtn = BuildIconButton("↙", "Re-dock into the game overlay");
        redockBtn.Tag = "radar.redock";
        redockBtn.IsVisible = false;
        redockBtn.Click += (_, _) =>
        {
            RynthLog.UI("RadarPanel: redock button clicked.");
            RequestRedock?.Invoke();
        };

        var popoutBtn = BuildIconButton("↗", "Pop out into a floating window");
        popoutBtn.Tag = "radar.popout";
        popoutBtn.Click += (_, _) =>
        {
            RynthLog.UI("RadarPanel: popout button clicked.");
            RequestPopOut?.Invoke();
        };

        var gearBtn = BuildIconButton("⚙", "Radar settings");
        gearBtn.Tag = "radar.gear";

        // Settings popup — hosted by the overlay as a sibling of the radar
        // (parented to _desktopCanvas via AttachSettingsPopup), NOT as a
        // child of radarGrid. Canvas children get unbounded available size,
        // so the popup is its natural content size regardless of the radar's
        // current dimensions.
        var settingsBorder = new Border
        {
            // Always fully opaque — settings UI must stay readable regardless
            // of the radar's opacity slider (which fades the radar backdrop).
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x12, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            // Opacity changes only redraw — the actual fade is applied to the
            // backdrop fills inside Render, so the world details stay opaque.
            Child = BuildSettingsPanel(state, surface, () => surface.InvalidateVisual()),
            IsVisible = false,
        };
        Action toggleSettings = () =>
        {
            settingsBorder.IsVisible = !settingsBorder.IsVisible;
            IsSettingsVisible = settingsBorder.IsVisible;
            RynthLog.UI($"RadarPanel: settings panel toggled (visible={settingsBorder.IsVisible}).");
        };
        gearBtn.Click += (_, _) =>
        {
            RynthLog.UI("RadarPanel: gear button clicked.");
            toggleSettings();
        };

        // Wire popout-mode click forwarders so FloatingPanelHost can route
        // clicks here when Avalonia's hit-test fails. Always Post to the UI
        // thread because the WndProc path runs on the LayeredWindow thread.
        OnGearClicked   = () => Dispatcher.UIThread.Post(toggleSettings);
        OnPopoutClicked = () => Dispatcher.UIThread.Post(() => RequestPopOut?.Invoke());
        OnRedockClicked = () => Dispatcher.UIThread.Post(() => RequestRedock?.Invoke());

        var btnStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Spacing = 4,
        };
        btnStack.Children.Add(redockBtn);
        btnStack.Children.Add(popoutBtn);
        btnStack.Children.Add(gearBtn);

        var radarGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        radarGrid.Children.Add(surface);
        radarGrid.Children.Add(btnStack);
        // settingsBorder is NOT added to radarGrid — it lives in
        // _desktopCanvas via AttachSettingsPopup so it's not constrained by
        // the radar's measure pass.
        AttachSettingsPopup?.Invoke(settingsBorder);

        // Gold frame fills the whole panel (2 px outer margin); buttons hug
        // the top-right interior corner with a constant inset.
        void PositionButtonsInsideFrame()
        {
            if (surface.Bounds.Width <= 0 || surface.Bounds.Height <= 0) return;
            const double rightInset = 6;
            const double topInset   = 6;
            btnStack.Margin = new Thickness(0, topInset, rightInset, 0);
        }
        // Publish the settings popup's physical-pixel rect to AvaloniaOverlay
        // so clicks on its overhanging area (extending past the radar's
        // bounds) get forwarded to Avalonia. Cleared when popup hides.
        void RefreshSettingsPopupRect()
        {
            if (!settingsBorder.IsVisible || settingsBorder.Bounds.Width <= 0)
            {
                AvaloniaOverlay.PublishRadarSettingsPopupRect(null);
                return;
            }
            // Translate popup bounds to physical AC client coords. The popup
            // lives inside the radar's visual tree; TranslatePoint up to the
            // overlay root yields its canvas-relative origin, which we then
            // scale by InputScale to match the Win32 pixel hit-test.
            var topLevel = TopLevel.GetTopLevel(settingsBorder);
            if (topLevel == null) { AvaloniaOverlay.PublishRadarSettingsPopupRect(null); return; }
            Point? origin = settingsBorder.TranslatePoint(default, topLevel);
            if (origin == null) { AvaloniaOverlay.PublishRadarSettingsPopupRect(null); return; }
            float s = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
            AvaloniaOverlay.PublishRadarSettingsPopupRect((
                origin.Value.X * s,
                origin.Value.Y * s,
                settingsBorder.Bounds.Width  * s,
                settingsBorder.Bounds.Height * s));
        }
        settingsBorder.LayoutUpdated += (_, _) => RefreshSettingsPopupRect();
        settingsBorder.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty)
                RefreshSettingsPopupRect();
        };

        Button[] hittableButtons = { redockBtn, popoutBtn, gearBtn };
        int lastSnapshotCount = -1;
        void RefreshButtonSnapshot()
        {
            var list = new List<ButtonHit>(hittableButtons.Length);
            foreach (var btn in hittableButtons)
            {
                if (!btn.IsVisible || btn.Bounds.Width <= 0 || btn.Tag is not string tag) continue;
                Point? origin = btn.TranslatePoint(default, radarGrid);
                if (origin == null) continue;
                list.Add(new ButtonHit(tag, origin.Value.X, origin.Value.Y,
                                       btn.Bounds.Width, btn.Bounds.Height));
            }
            var arr = list.ToArray();
            ButtonBoundsSnapshot = arr;
            if (arr.Length != lastSnapshotCount)
            {
                lastSnapshotCount = arr.Length;
                var summary = string.Join(", ", arr.Select(b => $"{b.Tag}@({b.X:F0},{b.Y:F0})+{b.W:F0}x{b.H:F0}"));
                RynthLog.UI($"RadarPanel: snapshot updated → [{summary}]");
            }
        }
        surface.LayoutUpdated += (_, _) => { PositionButtonsInsideFrame(); RefreshButtonSnapshot(); };
        btnStack.LayoutUpdated += (_, _) => RefreshButtonSnapshot();

        // Mouse wheel → zoom (matches ImGui radar's settings range 0.5..6.0).
        Action<double> applyWheel = deltaY =>
        {
            float delta = (float)deltaY * 0.15f;
            float prev = state.Zoom;
            state.Zoom = Math.Clamp(state.Zoom + delta, 0.5f, 6.0f);
            if (state.Zoom != prev) state.Persist();
            surface.InvalidateVisual();
        };
        surface.PointerWheelChanged += (_, e) =>
        {
            applyWheel(e.Delta.Y);
            e.Handled = true;
        };
        // Popout-mode wheel: FloatingPanelHost intercepts WM_MOUSEWHEEL and
        // forwards the delta here. Avalonia's hit-test drops PostMessage'd
        // wheel events at off-bounds canvas coords (same root cause as the
        // click-routing problem), so we route directly.
        OnWheelDelta = d => Dispatcher.UIThread.Post(() => applyWheel(d));

        // Left-click + drag = move the radar. Wired via the overlay's
        // AttachDragHandle hook so the docked-panel drag pipeline owns it
        // (BringPanelToFront, persistence, hit-test refresh).
        AttachDragHandle?.Invoke(surface);

        // Right-click context menu — replaces the chrome buttons. Items:
        //   • Pop Out / Re-dock (mode-aware)
        //   • Close
        //   • Rotate with player (toggle)
        var menu = new ContextMenu();
        var miPopOut = new MenuItem { Header = "Pop out" };
        miPopOut.Click += (_, _) => RequestPopOut?.Invoke();
        var miRedock = new MenuItem { Header = "Re-dock" };
        miRedock.Click += (_, _) => RequestRedock?.Invoke();
        var miClose = new MenuItem { Header = "Close" };
        miClose.Click += (_, _) => RequestClose?.Invoke();
        var miRotate = new MenuItem { Header = "Rotate with player" };
        miRotate.Click += (_, _) =>
        {
            state.RotateWithPlayer = !state.RotateWithPlayer;
            state.Persist();
            miRotate.Icon = state.RotateWithPlayer
                ? new TextBlock { Text = "✓", Foreground = ColCardinal, FontSize = 12 }
                : null;
            surface.InvalidateVisual();
        };
        menu.Items.Add(miPopOut);
        menu.Items.Add(miRedock);
        menu.Items.Add(new Separator());
        menu.Items.Add(miRotate);
        menu.Items.Add(new Separator());
        menu.Items.Add(miClose);
        menu.Opening += (_, _) =>
        {
            bool floating = IsFloatingNow?.Invoke() ?? false;
            miPopOut.IsVisible = !floating;
            miRedock.IsVisible = floating;
            miRotate.Icon = state.RotateWithPlayer
                ? new TextBlock { Text = "✓", Foreground = ColCardinal, FontSize = 12 }
                : null;
        };
        surface.ContextMenu = menu;

        // 33 ms snapshot poll (~30 Hz). The payload is small (markers +
        // visited strips); cached geometry skips wall serialization on
        // unchanged landblocks. This matches the perceived smoothness of
        // the per-frame ImGui radar without crushing the plugin thread.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            if (TryFetch(state.CachedMapVersion, out Snapshot? fresh) && fresh != null)
            {
                state.Live = fresh;
                if (fresh.GeometryIncluded)
                {
                    state.CachedMapVersion = fresh.MapVersion;
                    state.CachedWalls = fresh.Walls;
                    state.CachedFills = fresh.Fills;
                }
                else if (fresh.MapVersion != state.CachedMapVersion && fresh.MapVersion != 0 && !fresh.IsIndoor)
                {
                    // Outdoor landblocks: no geometry sent, but version still
                    // tracks landblock — clear stale walls so they don't bleed
                    // through when transitioning indoor→outdoor.
                    state.CachedMapVersion = fresh.MapVersion;
                    state.CachedWalls = new List<WallLayer>();
                    state.CachedFills = new List<FillLayer>();
                }

                // First poll after login can land before raycast is ready, so
                // the plugin returns an indoor snapshot with empty walls. Once
                // raycast comes online, we have to re-ask with version=0 or
                // the plugin sees a matching version and skips the geometry
                // payload — leaving the radar permanently blank until the
                // user manually closes/reopens it. Force a re-fetch next tick
                // whenever we're indoors but lack walls.
                if (state.Live.IsIndoor && state.CachedWalls.Count == 0)
                    state.CachedMapVersion = 0;
            }
            // Sync overlay button visibility with current pop-out state.
            //   docked → [↗ popout, ⚙ gear]
            //   popped → [↙ redock, ⚙ gear]
            bool floating = IsFloatingNow?.Invoke() ?? false;
            if (redockBtn.IsVisible != floating)  redockBtn.IsVisible  = floating;
            if (popoutBtn.IsVisible == floating)  popoutBtn.IsVisible  = !floating;
            // Always refresh the snapshot from the timer tick — LayoutUpdated
            // doesn't reliably fire for off-canvas panels in popout, so we
            // can't depend on it alone.
            RefreshButtonSnapshot();
            surface.InvalidateVisual();
        };
        timer.Start();
        surface.AttachedToVisualTree   += (_, _) => { if (!timer.IsEnabled) timer.Start(); };
        surface.DetachedFromVisualTree += (_, _) => timer.Stop();

        return radarGrid;
    }

    // ── Custom-rendered surface ─────────────────────────────────────────────
    private sealed class RadarSurface : Control
    {
        private readonly State _state;

        public RadarSurface(State state)
        {
            _state = state;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            MinWidth = 100;
            MinHeight = 100;
            // ClipToBounds left OFF: cardinals at the gold-frame perimeter can
            // extend ~7 px past the surface bounds (their glyph half-height).
            // The popout chrome's body Border has Padding(0,0,0,18) reserved
            // for the resize grip, so the surface ends up 18 px shorter than
            // the docked equivalent — clipping S/E labels. World geometry is
            // still confined via PushClip below; cardinals fly free.
            ClipToBounds = false;
            // Aliased rendering: adjacent fill rects tile pixel-perfectly with
            // no seam. With AA on, overlapping by 0.5 px paints overlap pixels
            // at ~0.80 alpha vs 0.55 for non-overlapped — the visible "cross
            // lines" between strips. Aliased trades sub-pixel smoothness for
            // crisp strip edges (matches ImGui radar's look).
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            // …but apply standard antialias to text so bold cardinals /
            // coord readout don't look chunky. EdgeMode.Aliased above only
            // affects geometry; text gets its own renderer mode. Use plain
            // Antialias instead of SubpixelAntialias because subpixel
            // requires a known opaque background — on the radar's
            // semi-transparent canvas, subpixel produced colored fringes
            // that shifted on every render, reading as a "flash" when the
            // overlay re-captured.
            RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        }

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);

            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w < 16 || h < 16) return;

            // The gold rim now fills the full panel rectangle (2 px inset for
            // a crisp edge) — no more centred-square letterboxing on
            // non-square panels. Inside the rim we reserve a small bottom
            // strip for the coord readout so it doesn't overlap the world.
            // Layout: when the gold rim is on, the rim hugs the panel edge
            // (frameInset=0) and the canvas sits 5 px inside the rim. With
            // the rim hidden, canvas fills the whole panel — no chrome.
            bool showRim = _state.ShowGoldRim;
            const double frameInset = 0;
            double canvasInset = showRim ? 5 : 0;
            const double coordStripHeight = 14;

            double frameLeft = frameInset, frameTop = frameInset;
            double frameRight = w - frameInset, frameBottom = h - frameInset;
            double frameW = frameRight - frameLeft, frameH = frameBottom - frameTop;

            // Backdrop opacity multiplier — applied only to the two dark
            // fills (outer black + canvas bg) so the gold rim and the world
            // details (walls, markers, player dot, cardinals, coords) stay
            // fully visible regardless of slider position.
            float bgOpacity = Math.Clamp(_state.Opacity, 0.0f, 1.0f);
            var canvasBgBrush = new SolidColorBrush(MulAlpha(ColCanvasBgRgb, bgOpacity));

            // ── Frame: outer black (fades) + gold inner border + accent ──
            // Whole frame skipped when ShowGoldRim is off — leaves a clean
            // canvas-only view.
            if (showRim)
            {
                var frameOuterBrush = new SolidColorBrush(MulAlpha(ColFrameOuterRgb, bgOpacity));
                ctx.FillRectangle(frameOuterBrush, new Rect(frameLeft, frameTop, frameW, frameH));
                ctx.DrawRectangle(null, new Pen(ColFrameInner, 2),
                    new Rect(frameLeft + 1, frameTop + 1, frameW - 2, frameH - 2));
                ctx.DrawRectangle(null, new Pen(ColFrameAccent, 1),
                    new Rect(frameLeft + 3, frameTop + 3, frameW - 6, frameH - 6));
            }

            // ── Canvas (interior playfield, fades with opacity) ──────────
            double canvasLeft = frameLeft + canvasInset, canvasTop = frameTop + canvasInset;
            double canvasRight = frameRight - canvasInset, canvasBottom = frameBottom - canvasInset;
            ctx.FillRectangle(canvasBgBrush,
                new Rect(canvasLeft, canvasTop, canvasRight - canvasLeft, canvasBottom - canvasTop));

            // World playfield = canvas minus the bottom coord strip. Centre
            // the world (player marker, crosshair) on the world strip — not
            // the panel — so the player sits visually at the middle of the
            // rendered radar.
            double worldBottom = canvasBottom - coordStripHeight;
            var worldMin = new Point(canvasLeft, canvasTop);
            var worldMax = new Point(canvasRight, worldBottom);
            var centre = new Point((worldMin.X + worldMax.X) * 0.5,
                                   (worldMin.Y + worldMax.Y) * 0.5);
            double worldHalfW = (worldMax.X - worldMin.X) * 0.5;
            double worldHalfH = (worldMax.Y - worldMin.Y) * 0.5;

            // Crosshair (clipped to world playfield so it doesn't draw into
            // the coord strip).
            var chPen = new Pen(ColCrosshair, 1);
            ctx.DrawLine(chPen, new Point(centre.X, worldMin.Y), new Point(centre.X, worldMax.Y));
            ctx.DrawLine(chPen, new Point(worldMin.X, centre.Y), new Point(worldMax.X, centre.Y));

            using (ctx.PushClip(new Rect(worldMin, worldMax)))
            {
                DrawWorld(ctx, centre, worldMin, worldMax);
            }

            // Coord readout sits in the reserved strip, centered horizontally.
            DrawCoordReadout(ctx, (canvasLeft + canvasRight) * 0.5, canvasBottom);

            // ── Cardinals at world playfield perimeter (inside the rim) ──
            DrawCardinals(ctx, centre, worldHalfW, worldHalfH);
        }

        private void DrawCoordReadout(DrawingContext ctx, double centreX, double bottomY)
        {
            string coords = FormatCoords(_state.Live);
            if (string.IsNullOrEmpty(coords)) return;

            var ft = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
            var main = new FormattedText(coords, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, ft, 11, ColCoordText);
            var outline = new FormattedText(coords, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, ft, 11, ColTextOutline);
            double x = centreX - main.Width * 0.5;
            double y = bottomY - main.Height - 1;
            ctx.DrawText(outline, new Point(x - 1, y));
            ctx.DrawText(outline, new Point(x + 1, y));
            ctx.DrawText(outline, new Point(x, y - 1));
            ctx.DrawText(outline, new Point(x, y + 1));
            ctx.DrawText(main, new Point(x, y));
        }

        private void DrawWorld(DrawingContext ctx, Point centre, Point clipMin, Point clipMax)
        {
            var live = _state.Live;
            var p = live.Player;

            // Heading: AC convention 0°=east, 90°=north (CCW from east).
            // ProjectWorld rotates by (90°-heading) so facing → screen-up.
            float hRad = p.Heading * MathF.PI / 180f;
            float sinH = MathF.Sin(hRad);
            float cosH = MathF.Cos(hRad);
            bool rotate = _state.RotateWithPlayer;
            float zoom = MathF.Max(0.5f, _state.Zoom);

            float pwx = p.WorldX;
            float pwy = p.WorldY;

            Point Project(float wx, float wy)
            {
                float dx = wx - pwx;
                float dy = wy - pwy;
                if (rotate)
                {
                    float right   = sinH * dx - cosH * dy;
                    float forward = cosH * dx + sinH * dy;
                    return new Point(centre.X + right * zoom, centre.Y - forward * zoom);
                }
                return new Point(centre.X + dx * zoom, centre.Y - dy * zoom);
            }

            // Layer ordering — match ImGui radar exactly: curIdx-1, curIdx+1, curIdx.
            // Layers below/above current are dimmed (alpha 0.30); current is full alpha.
            var layers = live.LayerZs;
            float curZ = live.CurrentLayerZ;
            int curIdx = -1;
            if (layers.Count > 0)
            {
                curIdx = 0;
                float bestDelta = MathF.Abs(layers[0] - curZ);
                for (int i = 1; i < layers.Count; i++)
                {
                    float d = MathF.Abs(layers[i] - curZ);
                    if (d < bestDelta) { bestDelta = d; curIdx = i; }
                }
            }

            Span<(int idx, float alpha, bool isCurrent)> order = stackalloc (int, float, bool)[3];
            int n = 0;
            if (curIdx - 1 >= 0)              order[n++] = (curIdx - 1, 0.30f, false);
            if (curIdx + 1 < layers.Count)    order[n++] = (curIdx + 1, 0.30f, false);
            if (curIdx >= 0)                  order[n++] = (curIdx,     1.00f, true);

            // Index cached walls/fills/visited by Z so we don't repeat-scan.
            var wallsByZ = new Dictionary<float, float[]>();
            foreach (var wl in _state.CachedWalls) wallsByZ[wl.Z] = wl.Segments;
            var fillsByZ = new Dictionary<float, float[]>();
            foreach (var fl in _state.CachedFills) fillsByZ[fl.Z] = fl.Strips;
            var visitedByZ = new Dictionary<float, float[]>();
            foreach (var vl in live.Visited) visitedByZ[vl.Z] = vl.Strips;

            // For wall visited tinting we need the visited-cell set on the current layer.
            // The snapshot only sends merged strips, not raw cells — so we approximate:
            // a wall is "visited" when its midpoint falls inside any visited strip on
            // the same layer. Cheap point-in-rect test, runs once per wall on the
            // current layer.
            float[]? visitedCurStrips = null;
            if (curIdx >= 0 && visitedByZ.TryGetValue(layers[curIdx], out var vcs)) visitedCurStrips = vcs;

            for (int li = 0; li < n; li++)
            {
                float layerZ = layers[order[li].idx];
                float a = order[li].alpha;
                bool current = order[li].isCurrent;

                // Floor fill strips (fallback path — no baked textures yet).
                // CellType 0=Flat, 1=SlopeUp, 2=SlopeDown — match DungeonMapUi colors.
                // Adjacent rects expand by 0.5px to overlap and hide AA seams that
                // otherwise paint a thin half-alpha line between touching strips.
                if (fillsByZ.TryGetValue(layerZ, out var strips))
                {
                    var brushFlat = new SolidColorBrush(MulAlpha(ColFloorFlatUnvisited, a));
                    var brushUp   = new SolidColorBrush(MulAlpha(ColFloorSlopeUp,       a));
                    var brushDown = new SolidColorBrush(MulAlpha(ColFloorSlopeDown,     a));

                    for (int i = 0; i < strips.Length; i += 5)
                    {
                        float x0 = strips[i], y0 = strips[i + 1];
                        float x1 = strips[i + 2], y1 = strips[i + 3];
                        int type = (int)strips[i + 4];
                        IBrush fill = type switch
                        {
                            1 => brushUp,
                            2 => brushDown,
                            _ => brushFlat,
                        };

                        Point p00 = Project(x0, y0);
                        Point p11 = Project(x1, y1);

                        if (rotate)
                        {
                            Point p10 = Project(x1, y0);
                            Point p01 = Project(x0, y1);
                            DrawQuad(ctx, fill, p00, p10, p11, p01);
                        }
                        else
                        {
                            ctx.FillRectangle(fill, NormalizeRect(p00, p11));
                        }
                    }
                }

                // Walls (visited tinting only for current layer).
                if (wallsByZ.TryGetValue(layerZ, out var wallSegs))
                {
                    var unseenPen = new Pen(new SolidColorBrush(MulAlpha(ColWallUnseen, a)), current ? 1.5 : 1.0);
                    var visitedPen = new Pen(new SolidColorBrush(MulAlpha(ColWallVisited, a)), current ? 1.5 : 1.0);

                    for (int i = 0; i < wallSegs.Length; i += 6)
                    {
                        float ax = wallSegs[i], ay = wallSegs[i + 1];
                        float bx = wallSegs[i + 2], by = wallSegs[i + 3];
                        // wallSegs[i+4]/[i+5] = grid cell — used for visited test below.

                        Point pa = Project(ax, ay);
                        Point pb = Project(bx, by);

                        if (Math.Max(pa.X, pb.X) < clipMin.X || Math.Min(pa.X, pb.X) > clipMax.X ||
                            Math.Max(pa.Y, pb.Y) < clipMin.Y || Math.Min(pa.Y, pb.Y) > clipMax.Y) continue;

                        bool visited = false;
                        if (current && visitedCurStrips != null)
                        {
                            float mx = (ax + bx) * 0.5f;
                            float my = (ay + by) * 0.5f;
                            visited = PointInStrips(mx, my, visitedCurStrips);
                        }
                        ctx.DrawLine(visited ? visitedPen : unseenPen, pa, pb);
                    }
                }

                // Visited overlay (current layer only).
                if (current && visitedByZ.TryGetValue(layerZ, out var vStrips))
                {
                    var visitedFill = new SolidColorBrush(MulAlpha(ColFloorVisited, a));
                    for (int i = 0; i < vStrips.Length; i += 4)
                    {
                        float x0 = vStrips[i], y0 = vStrips[i + 1];
                        float x1 = vStrips[i + 2], y1 = vStrips[i + 3];
                        Point p00 = Project(x0, y0);
                        Point p11 = Project(x1, y1);

                        if (rotate)
                        {
                            Point p10 = Project(x1, y0);
                            Point p01 = Project(x0, y1);
                            DrawQuad(ctx, visitedFill, p00, p10, p11, p01);
                        }
                        else
                        {
                            ctx.FillRectangle(visitedFill, NormalizeRect(p00, p11));
                        }
                    }
                }
            }

            // ── Markers ──────────────────────────────────────────────────
            foreach (var m in live.Markers)
            {
                bool show = m.Kind switch
                {
                    0 => _state.ShowMonsters,
                    1 => _state.ShowNpcs,
                    2 => _state.ShowPortals,
                    3 => _state.ShowDoors,
                    _ => false,
                };
                if (!show) continue;

                Point pp = Project(m.X, m.Y);
                if (pp.X < clipMin.X || pp.X > clipMax.X || pp.Y < clipMin.Y || pp.Y > clipMax.Y) continue;

                switch (m.Kind)
                {
                    case 0: ctx.DrawEllipse(ColMonster, null, pp, 3, 3); break;
                    case 1: ctx.DrawEllipse(ColNpc,     null, pp, 3, 3); break;
                    case 2:
                    {
                        const double s = 4;
                        DrawQuad(ctx, ColPortal,
                            new Point(pp.X,     pp.Y - s),
                            new Point(pp.X + s, pp.Y),
                            new Point(pp.X,     pp.Y + s),
                            new Point(pp.X - s, pp.Y));
                        // Skipping the destination label for MVP — the ImGui
                        // version draws it above the diamond. Wire later.
                        break;
                    }
                    case 3:
                    {
                        const double s = 3.5;
                        var r = new Rect(pp.X - s, pp.Y - s, s * 2, s * 2);
                        ctx.FillRectangle(ColDoor, r);
                        ctx.DrawRectangle(null, new Pen(ColDoorEdge, 1), r);
                        break;
                    }
                }
            }

            // ── Player + heading arrow ───────────────────────────────────
            ctx.DrawEllipse(ColPlayer, null, centre, 3.5, 3.5);
            // Arrow length 10px. In rotate mode the player always faces up.
            // In north-up mode arrow points along world heading.
            double ax2, ay2;
            if (rotate)
            {
                ax2 = centre.X;
                ay2 = centre.Y - 10;
            }
            else
            {
                ax2 = centre.X + cosH * 10;
                ay2 = centre.Y - sinH * 10;
            }
            ctx.DrawLine(new Pen(ColPlayer, 2), centre, new Point(ax2, ay2));
        }

        private void DrawCardinals(DrawingContext ctx, Point centre, double halfW, double halfH)
        {
            // Labels sit just inside the world playfield's rectangular
            // perimeter (not the gold rim, since that would let the south
            // label collide with the coord strip). Inset by `sz` so glyphs
            // stay clear of the canvas edge.
            var ft = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
            const double sz = 14;
            float hRad = _state.Live.Player.Heading * MathF.PI / 180f;
            float sinH = MathF.Sin(hRad);
            float cosH = MathF.Cos(hRad);
            bool rotate = _state.RotateWithPlayer;

            // Compass entries are world directions: AC world Y is north, X is
            // east. Mapping world→screen must match DrawWorld.Project() exactly
            // so cardinals and the world geometry agree on which way is up.
            (string label, double worldX, double worldY)[] compass =
            {
                ("N",  0,  1),
                ("E",  1,  0),
                ("S",  0, -1),
                ("W", -1,  0),
            };

            double effW = Math.Max(1, halfW - sz);
            double effH = Math.Max(1, halfH - sz);

            foreach (var c in compass)
            {
                double dirX, dirY;
                if (rotate)
                {
                    // World→screen using the same rotation as Project():
                    //   screen.x =  sinH*world.x - cosH*world.y
                    //   screen.y = -(cosH*world.x + sinH*world.y)
                    dirX =  sinH * c.worldX - cosH * c.worldY;
                    dirY = -(cosH * c.worldX + sinH * c.worldY);
                }
                else
                {
                    // North-up: screen X = world X, screen Y = -world Y.
                    dirX =  c.worldX;
                    dirY = -c.worldY;
                }

                // Project onto the rectangular perimeter (half-extents
                // effW/effH after the sz inset). Solve for t such that the
                // point sits on the rect: max(|dx*t|/effW, |dy*t|/effH) = 1.
                double mNorm = Math.Max(Math.Abs(dirX) / effW, Math.Abs(dirY) / effH);
                if (mNorm < 1e-4) continue;
                double t = 1 / mNorm;
                double cx = centre.X + dirX * t;
                double cy = centre.Y + dirY * t;

                var ft2 = new FormattedText(c.label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, ft, sz, ColCardinal);
                var origin = new Point(cx - ft2.Width * 0.5, cy - ft2.Height * 0.5);
                // Outline — 4-way 1px offsets for legibility against any bg.
                var outline = new FormattedText(c.label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, ft, sz, ColTextOutline);
                ctx.DrawText(outline, new Point(origin.X - 1, origin.Y));
                ctx.DrawText(outline, new Point(origin.X + 1, origin.Y));
                ctx.DrawText(outline, new Point(origin.X, origin.Y - 1));
                ctx.DrawText(outline, new Point(origin.X, origin.Y + 1));
                ctx.DrawText(ft2, origin);
            }
        }

        private static void DrawQuad(DrawingContext ctx, IBrush fill, Point a, Point b, Point c, Point d)
        {
            var geom = new StreamGeometry();
            using (var gctx = geom.Open())
            {
                gctx.BeginFigure(a, isFilled: true);
                gctx.LineTo(b);
                gctx.LineTo(c);
                gctx.LineTo(d);
                gctx.EndFigure(true);
            }
            ctx.DrawGeometry(fill, null, geom);
        }

        private static Rect NormalizeRect(Point a, Point b)
        {
            double x0 = Math.Min(a.X, b.X);
            double x1 = Math.Max(a.X, b.X);
            double y0 = Math.Min(a.Y, b.Y);
            double y1 = Math.Max(a.Y, b.Y);
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }


        private static bool PointInStrips(float x, float y, float[] strips)
        {
            for (int i = 0; i < strips.Length; i += 4)
            {
                if (x >= strips[i] && x <= strips[i + 2] && y >= strips[i + 1] && y <= strips[i + 3])
                    return true;
            }
            return false;
        }
    }

    // ── Overlay corner button + settings flyout ────────────────────────────
    private static Button BuildIconButton(string glyph, string tooltip)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = 22,
            Height = 22,
            FontSize = 12,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x14, 0x1F, 0x29)),
            Foreground = ColCoordText,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        // Tooltip parameter intentionally unused: ToolTip.SetTip() schedules
        // an Avalonia ToolTip Popup whose hover-enter side effects flash the
        // entire overlay capture pipeline (a one-frame invalidation that's
        // very visible against AC's frame). Glyphs are self-descriptive.
        _ = tooltip;
        // Eat pointer events so they don't bubble through to the radar
        // surface's drag handler beneath.
        btn.PointerPressed += (_, e) => e.Handled = true;
        return btn;
    }

    private static Control BuildSettingsPanel(State state, RadarSurface surface, Action onOpacityChanged)
    {
        // Auto-size the column to its widest child — no fixed Width or
        // MinWidth, so the popup is exactly as wide as the longest label
        // (e.g. "Click-through (hold Ctrl to interact)"). settingsBorder
        // .MaxWidth caps it against the radar's bounds, and the ScrollViewer
        // adds horizontal scroll if a small radar forces a clamp below the
        // natural content width.
        var col = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };

        // Title + separator
        col.Children.Add(new TextBlock
        {
            Text = "Radar Settings",
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Foreground = ColCoordText,
        });
        col.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59)),
            Margin = new Thickness(0, 2, 0, 4),
        });

        CheckBox MakeCheck(string label, string? tip, Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = get(),
                Foreground = ColCoordText,
                FontSize = 11,
            };
            // tip parameter intentionally unused — see BuildIconButton.
            _ = tip;
            cb.IsCheckedChanged += (_, _) =>
            {
                set(cb.IsChecked == true);
                state.Persist();
                surface.InvalidateVisual();
            };
            return cb;
        }

        col.Children.Add(MakeCheck("Rotate with player",
            "On: facing points up and the compass rotates.\nOff: north is always up.",
            () => state.RotateWithPlayer, v => state.RotateWithPlayer = v));
        col.Children.Add(MakeCheck("Show gold rim",
            "Toggle the gold-on-black radar frame. Off = canvas-only, no chrome.",
            () => state.ShowGoldRim, v => state.ShowGoldRim = v));
        col.Children.Add(MakeCheck("Click-through (hold Ctrl to interact)",
            "On: clicks pass through to the game; hold Ctrl to drag, gear, popout, or resize.\nOff: radar is always interactive.",
            () => state.CtrlGatedClickThrough, v => state.CtrlGatedClickThrough = v));

        // Opacity affects the dark backdrop only — gold rim and world details
        // stay fully visible. Zoom range matches the ImGui radar's settings.
        col.Children.Add(MakeSliderRow("Opacity", state.Opacity, 0.05, 1.0, "0.00",
            v => { state.Opacity = (float)v; state.Persist(); onOpacityChanged(); }));
        col.Children.Add(MakeSliderRow("Zoom", state.Zoom, 0.5, 6.0, "0.0",
            v => { state.Zoom = (float)v; state.Persist(); surface.InvalidateVisual(); }));

        col.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59)),
            Margin = new Thickness(0, 4, 0, 2),
        });
        col.Children.Add(new TextBlock
        {
            Text = "Show:",
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            Foreground = ColCoordText,
        });
        col.Children.Add(MakeCheck("Monsters", null,
            () => state.ShowMonsters, v => state.ShowMonsters = v));
        col.Children.Add(MakeCheck("NPCs", null,
            () => state.ShowNpcs, v => state.ShowNpcs = v));
        col.Children.Add(MakeCheck("Portals", null,
            () => state.ShowPortals, v => state.ShowPortals = v));
        col.Children.Add(MakeCheck("Doors", null,
            () => state.ShowDoors, v => state.ShowDoors = v));

        // Wrap in a ScrollViewer so the popup never gets clipped by the
        // radar panel bounds — small radars get a scrollbar instead of
        // missing the bottom rows.
        return new ScrollViewer
        {
            Content = col,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static Control MakeSliderRow(string label, double initial, double min, double max, string fmt, Action<double> onChanged)
    {
        var dock = new DockPanel { LastChildFill = true };
        var labelBlock = new TextBlock
        {
            Text = label,
            Width = 60,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = ColCoordText,
        };
        var valueBlock = new TextBlock
        {
            Text = initial.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture),
            Width = 36,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = ColCoordText,
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = initial,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            valueBlock.Text = e.NewValue.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            onChanged(e.NewValue);
        };

        DockPanel.SetDock(labelBlock, Dock.Left);
        DockPanel.SetDock(valueBlock, Dock.Right);
        dock.Children.Add(labelBlock);
        dock.Children.Add(valueBlock);
        dock.Children.Add(slider);
        return dock;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    private static Color MulAlpha(Color c, float mul)
    {
        int a = Math.Clamp((int)MathF.Round(c.A * mul), 0, 255);
        return Color.FromArgb((byte)a, c.R, c.G, c.B);
    }
    private static float AlphaToFloat(byte a) => a == 0 ? 1f : a / 255f;

    private static string FormatCoords(Snapshot s)
    {
        // Match the ImGui radar's footer behaviour exactly:
        //   indoor  → "LB AABB  Z 12.3"   (landblock id, no NS/EW)
        //   outdoor → "33.1N  44.8E  Z 12.3"  (when NavCoordinateHelper resolves)
        var p = s.Player;
        if (s.IsIndoor)
            return $"LB {p.Landblock:X4}  Z {p.Z:0.0}";

        if (!double.IsNaN(s.Ns) && !double.IsNaN(s.Ew))
            return $"{Math.Abs(s.Ns):0.0}{(s.Ns >= 0 ? "N" : "S")}  {Math.Abs(s.Ew):0.0}{(s.Ew >= 0 ? "E" : "W")}  Z {p.Z:0.0}";

        return $"Z {p.Z:0.0}";
    }

    // ── Plugin export binding ───────────────────────────────────────────────
    private static void TryBind()
    {
        if (_getRadarSnapshot != null) return;
        var plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        IntPtr p1 = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetRadarSnapshot");
        if (p1 != IntPtr.Zero)
            _getRadarSnapshot = Marshal.GetDelegateForFunctionPointer<GetRadarSnapshotFn>(p1);
    }

    private static bool TryFetch(uint engineKnownVersion, out Snapshot? snapshot)
    {
        snapshot = null;
        if (_getRadarSnapshot == null) TryBind();
        if (_getRadarSnapshot == null) return false;
        try
        {
            IntPtr ptr = _getRadarSnapshot(engineKnownVersion);
            if (ptr == IntPtr.Zero) return false;
            string? json = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrEmpty(json) || json == "{}") return false;
            snapshot = JsonSerializer.Deserialize(json, RadarJsonContext.Default.Snapshot);
            return snapshot != null;
        }
        catch
        {
            return false;
        }
    }
}
