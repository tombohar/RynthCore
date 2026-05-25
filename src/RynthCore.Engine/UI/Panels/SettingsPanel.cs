// ============================================================================
//  RynthCore.Engine — UI/Panels/SettingsPanel.cs
//  Avalonia replica of LegacyAdvancedSettingsUi (RynthSuite plugin).
//
//  11 tabs: Display | UI | Misc | Recharge | Melee Combat | Spell Combat |
//           Ranges | Navigation | Buffing | Crafting | Looting
//
//  Plugin exports used:
//    RynthPluginGetSettingsJson  → polled every 5s for external edits
//    RynthPluginSetSettingsJson  → immediate write-back on any control change
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

[JsonSerializable(typeof(SettingsPanel.Settings))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false, IncludeFields = false)]
internal partial class SettingsPanelJsonContext : JsonSerializerContext { }

internal static class SettingsPanel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetSettingsJsonFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSettingsJsonFn(IntPtr ansiJson);

    private static GetSettingsJsonFn? _getSettingsJson;
    private static SetSettingsJsonFn? _setSettingsJson;

    // ── Color palette (shared with other panels) ─────────────────────────────
    private static readonly IBrush ColTeal      = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush ColAmber     = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static readonly IBrush ColGreen     = new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x66));
    private static readonly IBrush ColMute      = new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static readonly IBrush ColTextDim   = new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2));
    private static readonly IBrush ColShellBg   = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    private static readonly IBrush ColPanelBg   = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush ColBtnFill   = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord   = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    private static readonly IBrush ColToggleOn  = new SolidColorBrush(Color.FromRgb(0x33, 0xFF, 0x33));
    private static readonly IBrush ColToggleOff = new SolidColorBrush(Color.FromRgb(0x33, 0x44, 0x55));
    private static readonly IBrush ColTabActive = new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42));

    private static readonly string[] Tabs =
        { "Display", "UI", "Misc", "Recharge", "Melee Combat", "Spell Combat",
          "Ranges", "Navigation", "Buffing", "Crafting", "Looting" };

    private static readonly string[] AttackHeights   = { "Low", "Medium", "High" };
    private static readonly string[] LootOwnershipModes = { "My Kills Only", "Fellowship Kills", "All Corpses" };
    private static readonly string[] MovementModes   = { "Legacy (Autorun)", "Tier 1 (CM_Movement)", "Tier 2 (MoveToPosition)" };

    // ── Settings mirror (matches SettingsBridgePayload on plugin side) ────────
    internal sealed class Settings
    {
        // Display
        public bool ShowTargetStaminaMana { get; set; }
        // UI
        public bool SuppressRetailRadar { get; set; }
        public bool ShowRynthRadar { get; set; }
        public bool RadarClickThrough { get; set; }
        public bool SuppressRetailChat { get; set; }
        public bool ShowRynthChat { get; set; }
        public bool ChatClickThrough { get; set; }
        public bool SuppressRetailPowerbar { get; set; }
        // Misc
        public bool EnableFPSLimit { get; set; }
        public int TargetFPSFocused { get; set; } = 60;
        public int TargetFPSBackground { get; set; } = 30;
        public bool EnableAutocram { get; set; }
        public bool PeaceModeWhenIdle { get; set; }
        public bool StartMacroOnLogin { get; set; }
        public bool PatrolOnLogin { get; set; }
        public bool EnableRaycasting { get; set; }
        public bool UseArcs { get; set; }
        public float BowArcVelocity { get; set; } = 25f;
        public float CrossbowArcVelocity { get; set; } = 40f;
        public float AtlatlArcVelocity { get; set; } = 22f;
        public float MagicArcVelocity { get; set; } = 25f;
        public int BlacklistAttempts { get; set; } = 3;
        public int BlacklistTimeoutSec { get; set; } = 30;
        public int TargetNoProgressTimeoutSec { get; set; }
        public int GiveQueueIntervalMs { get; set; } = 150;
        // Recharge
        public int HealAt { get; set; } = 60;
        public int RestamAt { get; set; } = 30;
        public int GetManaAt { get; set; } = 40;
        public int TopOffHP { get; set; } = 95;
        public int TopOffStam { get; set; } = 95;
        public int TopOffMana { get; set; } = 95;
        public int HealOthersAt { get; set; } = 50;
        public int RestamOthersAt { get; set; } = 10;
        public int InfuseOthersAt { get; set; } = 10;
        // Melee Combat
        public bool UseRecklessness { get; set; }
        public int MeleeAttackPower { get; set; } = -1;
        public int MeleeAttackHeight { get; set; } = 1;
        public int MissileAttackPower { get; set; } = -1;
        public int MissileAttackHeight { get; set; } = 1;
        public bool UseNativeAttack { get; set; } = true;
        public bool SummonPets { get; set; }
        public int PetMinMonsters { get; set; } = 1;
        // Spell Combat
        public int SpellCastIntervalMs { get; set; } = 400;
        public bool CastDispelSelf { get; set; }
        public int MinRingTargets { get; set; } = 4;
        public int MinSkillLevelTier1 { get; set; } = 35;
        public int MinSkillLevelTier2 { get; set; } = 85;
        public int MinSkillLevelTier3 { get; set; } = 135;
        public int MinSkillLevelTier4 { get; set; } = 185;
        public int MinSkillLevelTier5 { get; set; } = 235;
        public int MinSkillLevelTier6 { get; set; } = 285;
        public int MinSkillLevelTier7 { get; set; } = 335;
        public int MinSkillLevelTier8 { get; set; } = 435;
        // Ranges
        public int MonsterRange { get; set; } = 50;
        public int RingRange { get; set; } = 5;
        public int ApproachRange { get; set; } = 4;
        public double CorpseApproachRangeMax { get; set; } = 10.0;
        public double CorpseApproachRangeMin { get; set; } = 2.0;
        // Navigation
        public bool BoostNavPriority { get; set; }
        public float FollowNavMin { get; set; } = 1.5f;
        public float NavRingThickness { get; set; } = 6f;
        public float NavLineThickness { get; set; } = 6f;
        public float NavHeightOffset { get; set; } = 0.05f;
        public float NavSlopeSink { get; set; } = 1.5f;
        public bool ShowTerrainPassability { get; set; } = true;
        public bool OpenDoors { get; set; }
        public float OpenDoorRange { get; set; } = 5f;
        public bool AutoUnlockDoors { get; set; }
        public int MovementMode { get; set; }
        public float NavStopTurnAngle { get; set; } = 20f;
        public float NavResumeTurnAngle { get; set; } = 10f;
        public float NavDeadZone { get; set; } = 4f;
        public float NavSweepMult { get; set; } = 2.5f;
        public float NavLookaheadYards { get; set; } = 4f;
        public float NavTurnRateDegPerSec { get; set; } = 270f;
        public float NavTier1TurnSpeed { get; set; } = 3f;
        public float PostPortalDelaySec { get; set; } = 4f;
        public float T2Speed { get; set; } = 1f;
        public float T2WalkWithinYd { get; set; } = 5f;
        public float T2DistanceTo { get; set; } = 0.5f;
        public float T2ReissueMs { get; set; } = 2000f;
        public float T2MaxRangeYd { get; set; } = 500f;
        public int T2MaxLandblocks { get; set; } = 3;
        // Buffing
        public bool EnableBuffing { get; set; } = true;
        public bool RebuffWhenIdle { get; set; }
        public int RebuffSecondsRemaining { get; set; } = 300;
        public int BuffMinSkillLevelTier1 { get; set; } = 35;
        public int BuffMinSkillLevelTier2 { get; set; } = 85;
        public int BuffMinSkillLevelTier3 { get; set; } = 135;
        public int BuffMinSkillLevelTier4 { get; set; } = 185;
        public int BuffMinSkillLevelTier5 { get; set; } = 235;
        public int BuffMinSkillLevelTier6 { get; set; } = 285;
        public int BuffMinSkillLevelTier7 { get; set; } = 335;
        public int BuffMinSkillLevelTier8 { get; set; } = 435;
        // Crafting
        public bool EnableMissileCrafting { get; set; } = true;
        public string MissileCraftingState { get; set; } = string.Empty;
        public bool MissileCraftingActive { get; set; }
        public string MissileCraftingStatus { get; set; } = string.Empty;
        // Looting
        public bool EnableLooting { get; set; }
        public bool BoostLootPriority { get; set; }
        public bool LootOnlyRareCorpses { get; set; }
        public bool LootJumpEnabled { get; set; }
        public int LootJumpHeight { get; set; } = 10;
        public int LootOwnership { get; set; }
        public bool EnableAutostack { get; set; } = true;
        public bool EnableCombineSalvage { get; set; } = true;
        public bool CombineBagsDuringSalvage { get; set; } = true;
        public int LootInterItemDelayMs { get; set; } = 50;
        public int LootContentSettleMs { get; set; } = 100;
        public int LootEmptyCorpseMs { get; set; } = 300;
        public int LootClosingDelayMs { get; set; } = 200;
        public int LootAssessWindowMs { get; set; } = 200;
        public int LootRetryTimeoutMs { get; set; } = 500;
        public int LootOpenRetryMs { get; set; } = 1500;
        public int LootCorpseTimeoutMs { get; set; } = 12000;
        public int SalvageOpenDelayFirstMs { get; set; } = 400;
        public int SalvageOpenDelayFastMs { get; set; } = 50;
        public int SalvageAddDelayFirstMs { get; set; } = 600;
        public int SalvageAddDelayFastMs { get; set; } = 50;
        public int SalvageSalvageDelayMs { get; set; } = 50;
        public int SalvageResultDelayFirstMs { get; set; } = 1000;
        public int SalvageResultDelayFastMs { get; set; } = 250;
    }

    private sealed class PanelState
    {
        public Settings Data = new();
        public int SelectedTab;
        public bool Dirty;
    }

    // ── Picker overlay state (shared across tab renders) ─────────────────────
    private sealed class PickerState
    {
        public Canvas Canvas = null!;
        public Border? ActivePicker;
        public Button? ActiveAnchor;
        public Control? Root;

        public void Close()
        {
            if (ActivePicker != null) { Canvas.Children.Remove(ActivePicker); ActivePicker = null; }
            ActiveAnchor = null;
            Canvas.IsHitTestVisible = false;
        }

        public void Show(Button anchor, string[] items, int selected, Action<int> onPick)
        {
            if (ReferenceEquals(anchor, ActiveAnchor)) { Close(); return; }
            Close();
            ActiveAnchor = anchor;

            var stack = new StackPanel { Spacing = 1 };
            for (int i = 0; i < items.Length; i++)
            {
                int captured = i;
                var entry = new Button
                {
                    Content = items[i],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = i == selected ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42)) : ColBtnFill,
                    Foreground = i == selected ? ColTeal : ColTextDim,
                    BorderBrush = ColBtnBord,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2),
                    FontSize = 10,
                    Height = 20,
                };
                entry.Click += (_, _) => { onPick(captured); Close(); };
                stack.Children.Add(entry);
            }

            const double pickerWidth = 220;
            Point ap = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), Canvas) ?? new Point(8, 8);
            double rootWidth = Root?.Bounds.Width ?? 400;
            double rootHeight = Root?.Bounds.Height ?? 300;
            double left = Math.Clamp(ap.X, 4, Math.Max(4, rootWidth - pickerWidth - 4));
            double top = ap.Y;
            double maxH = Math.Min(items.Length * 22 + 8, Math.Max(80, rootHeight - top - 4));

            var picker = new Border
            {
                Width = pickerWidth,
                MaxHeight = maxH,
                Background = ColShellBg,
                BorderBrush = ColTeal,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = stack,
                },
            };
            Canvas.Children.Add(picker);
            Avalonia.Controls.Canvas.SetLeft(picker, left);
            Avalonia.Controls.Canvas.SetTop(picker, top);
            ActivePicker = picker;
            Canvas.IsHitTestVisible = true;
        }
    }

    public static Control Create()
    {
        TryBind();
        var state = new PanelState();

        var root = new Border
        {
            Background = ColShellBg,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };

        var rootGrid = new Grid();
        root.Child = rootGrid;

        var pickerCanvas = new Canvas
        {
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
        };

        var picker = new PickerState { Canvas = pickerCanvas };

        // Main layout: 150px sidebar + * content area
        var mainGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*"),
        };
        rootGrid.Children.Add(mainGrid);
        rootGrid.Children.Add(pickerCanvas);

        pickerCanvas.PointerPressed += (_, e) =>
        {
            if (picker.ActivePicker == null || !ReferenceEquals(e.Source, pickerCanvas)) return;
            e.Handled = true;
            picker.Close();
        };

        // ── Sidebar ───────────────────────────────────────────────────────────
        var sidebarScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Background = ColPanelBg,
        };
        Grid.SetColumn(sidebarScroll, 0);
        mainGrid.Children.Add(sidebarScroll);

        var sidebarStack = new StackPanel { Spacing = 1, Margin = new Thickness(2) };
        sidebarScroll.Content = sidebarStack;

        // ── Content area ──────────────────────────────────────────────────────
        var contentScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Background = ColShellBg,
            Padding = new Thickness(8, 6),
        };
        Grid.SetColumn(contentScroll, 1);
        mainGrid.Children.Add(contentScroll);

        var contentStack = new StackPanel { Spacing = 0 };
        contentScroll.Content = contentStack;

        // ── Tab buttons ───────────────────────────────────────────────────────
        var tabButtons = new List<Button>();
        for (int i = 0; i < Tabs.Length; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Content = Tabs[i],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = ColBtnFill,
                Foreground = ColMute,
                BorderBrush = ColBtnBord,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4),
                FontSize = 11,
                Height = 26,
                Margin = new Thickness(0, 1, 0, 0),
            };
            btn.Click += (_, _) =>
            {
                picker.Close();
                state.SelectedTab = idx;
                UpdateTabHighlight(tabButtons, idx);
                RebuildContent(state, contentStack, picker);
            };
            tabButtons.Add(btn);
            sidebarStack.Children.Add(btn);
        }

        // ── Initial data load ─────────────────────────────────────────────────
        if (TryFetch(out var initial)) state.Data = initial;
        picker.Root = root;
        UpdateTabHighlight(tabButtons, 0);
        RebuildContent(state, contentStack, picker);

        // ── Poll timer ────────────────────────────────────────────────────────
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            if (_getSettingsJson == null) TryBind();
            if (state.Dirty) return;
            if (!TryFetch(out var fresh)) return;
            state.Data = fresh;
            RebuildContent(state, contentStack, picker);
        };
        timer.Start();

        return root;
    }

    private static void UpdateTabHighlight(List<Button> buttons, int selected)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i].Background = i == selected ? ColTabActive : ColBtnFill;
            buttons[i].Foreground = i == selected ? ColTeal : ColMute;
        }
    }

    private static void RebuildContent(PanelState state, StackPanel panel, PickerState picker)
    {
        panel.Children.Clear();

        // Tab header
        var header = new TextBlock
        {
            Text = $"Advanced Settings > {Tabs[state.SelectedTab]}",
            Foreground = ColTeal,
            FontSize = 11,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        panel.Children.Add(header);
        panel.Children.Add(new Border { Height = 1, Background = ColBtnBord, Margin = new Thickness(0, 0, 0, 6) });

        switch (state.SelectedTab)
        {
            case 0: BuildDisplayTab(state, panel, picker); break;
            case 1: BuildUITab(state, panel, picker); break;
            case 2: BuildMiscTab(state, panel, picker); break;
            case 3: BuildRechargeTab(state, panel, picker); break;
            case 4: BuildMeleeCombatTab(state, panel, picker); break;
            case 5: BuildSpellCombatTab(state, panel, picker); break;
            case 6: BuildRangesTab(state, panel, picker); break;
            case 7: BuildNavigationTab(state, panel, picker); break;
            case 8: BuildBuffingTab(state, panel, picker); break;
            case 9: BuildCraftingTab(state, panel, picker); break;
            case 10: BuildLootingTab(state, panel, picker); break;
        }
    }

    // =========================================================================
    //  Tab content builders
    // =========================================================================

    private static void BuildDisplayTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(BoolRow("Show Target Stamina / Mana",
            state.Data.ShowTargetStaminaMana,
            v => { state.Data.ShowTargetStaminaMana = v; Push(state); },
            "When enabled, displays stamina and mana bars for the selected target (requires appraisal data)."));
    }

    private static void BuildUITab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(SectionHeader("Radar"));
        p.Children.Add(BoolRow("Get Rid of Retail Radar", state.Data.SuppressRetailRadar,
            v => { state.Data.SuppressRetailRadar = v; Push(state); },
            "Suppress the game's built-in radar (bezel, compass, coords, blips)."));
        p.Children.Add(BoolRow("Show RynthRadar", state.Data.ShowRynthRadar,
            v => { state.Data.ShowRynthRadar = v; Push(state); },
            "Render the custom square radar widget (indoor walls + dot markers, works outdoors)."));
        p.Children.Add(BoolRow("Radar Click-Through", state.Data.RadarClickThrough,
            v => { state.Data.RadarClickThrough = v; Push(state); },
            "Mouse events over the radar pass through to the game instead of the widget."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Chat"));
        p.Children.Add(BoolRow("Get Rid of Retail Chatbox", state.Data.SuppressRetailChat,
            v => { state.Data.SuppressRetailChat = v; Push(state); },
            "Hide the game's built-in chat window.\nThe AC chat input still works underneath — your typed commands and tells function unchanged."));
        p.Children.Add(BoolRow("Show RynthChat", state.Data.ShowRynthChat,
            v => { state.Data.ShowRynthChat = v; Push(state); },
            "Render the custom chat viewer (scrollback + channel coloring)."));
        p.Children.Add(BoolRow("Chat Click-Through", state.Data.ChatClickThrough,
            v => { state.Data.ChatClickThrough = v; Push(state); },
            "Mouse events over the chat pass through to the game. The gear button stays clickable."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Power Bar"));
        p.Children.Add(BoolRow("Get Rid of Retail Power Bar", state.Data.SuppressRetailPowerbar,
            v => { state.Data.SuppressRetailPowerbar = v; Push(state); },
            "Hide the vanilla attack/magic power bar that appears under the cursor while charging.\nThe bar's underlying combat state still works — only the on-screen widget is hidden."));
    }

    private static void BuildMiscTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(BoolRow("Enable FPS Limit", state.Data.EnableFPSLimit,
            v => { state.Data.EnableFPSLimit = v; Push(state); }));
        if (state.Data.EnableFPSLimit)
        {
            p.Children.Add(IntRow("Focused FPS", state.Data.TargetFPSFocused, 10, 240, 1,
                v => { state.Data.TargetFPSFocused = v; Push(state); }));
            p.Children.Add(IntRow("Background FPS", state.Data.TargetFPSBackground, 5, 60, 1,
                v => { state.Data.TargetFPSBackground = v; Push(state); }));
        }

        p.Children.Add(Spacer());
        p.Children.Add(BoolRow("Auto Cram", state.Data.EnableAutocram,
            v => { state.Data.EnableAutocram = v; Push(state); },
            "Automatically moves items from your main pack into side packs.\nNote: recently used weapons stay in the main pack."));
        p.Children.Add(BoolRow("Peace Mode When Idle", state.Data.PeaceModeWhenIdle,
            v => { state.Data.PeaceModeWhenIdle = v; Push(state); }));
        p.Children.Add(BoolRow("Start Macro On Login", state.Data.StartMacroOnLogin,
            v => { state.Data.StartMacroOnLogin = v; Push(state); },
            "Automatically starts the macro when RynthAi loads."));
        p.Children.Add(BoolRow("Patrol On Login", state.Data.PatrolOnLogin,
            v => { state.Data.PatrolOnLogin = v; Push(state); },
            "Automatically starts dungeon patrol when RynthAi loads."));
        p.Children.Add(BoolRow("Enable Raycasting", state.Data.EnableRaycasting,
            v => { state.Data.EnableRaycasting = v; Push(state); }));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Missile Arc Velocities (m/s)"));
        p.Children.Add(BoolRow("Use Arcs for Missile LoS", state.Data.UseArcs,
            v => { state.Data.UseArcs = v; Push(state); },
            "When off, all missile LoS checks are linear (eye-to-eye)."));
        if (state.Data.UseArcs)
        {
            p.Children.Add(FloatRow("Bow",      state.Data.BowArcVelocity,      10f, 60f, 0.5f, v => { state.Data.BowArcVelocity = v; Push(state); }, "Bow projectile speed (m/s). Lower = higher arc."));
            p.Children.Add(FloatRow("Crossbow", state.Data.CrossbowArcVelocity, 10f, 80f, 0.5f, v => { state.Data.CrossbowArcVelocity = v; Push(state); }));
            p.Children.Add(FloatRow("Atlatl",   state.Data.AtlatlArcVelocity,   10f, 60f, 0.5f, v => { state.Data.AtlatlArcVelocity = v; Push(state); }));
            p.Children.Add(FloatRow("Magic Arc", state.Data.MagicArcVelocity,   10f, 60f, 0.5f, v => { state.Data.MagicArcVelocity = v; Push(state); }));
        }

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Monster Blacklist"));
        p.Children.Add(IntRow("Attempts Before Blacklist", state.Data.BlacklistAttempts, 1, 20, 1,
            v => { state.Data.BlacklistAttempts = v; Push(state); },
            "How many failed attack attempts on a mob before it gets blacklisted."));
        p.Children.Add(IntRow("Blacklist Timeout (sec)", state.Data.BlacklistTimeoutSec, 5, 120, 5,
            v => { state.Data.BlacklistTimeoutSec = v; Push(state); },
            "How long a blacklisted mob is ignored before re-trying."));
        p.Children.Add(IntRow("No Progress Timeout (sec)", state.Data.TargetNoProgressTimeoutSec, 0, 300, 10,
            v => { state.Data.TargetNoProgressTimeoutSec = v; Push(state); },
            "Blacklist a target after being engaged this many seconds without dealing damage.\n0 = disabled. Default 60s."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Give Queue"));
        p.Children.Add(IntRow("Give Interval (ms)", state.Data.GiveQueueIntervalMs, 50, 2000, 50,
            v => { state.Data.GiveQueueIntervalMs = v; Push(state); },
            "Minimum delay between each item sent by /ra givea commands."));
    }

    private static void BuildRechargeTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(SectionHeader("In Combat — target within Monster Range (%)"));
        p.Children.Add(IntRow("Heal At",    state.Data.HealAt,   0, 100, 1, v => { state.Data.HealAt   = v; Push(state); },
            "While a target is within Monster Range, cast Heal Self when HP < this %."));
        p.Children.Add(IntRow("Re-stam At", state.Data.RestamAt, 0, 100, 1, v => { state.Data.RestamAt = v; Push(state); },
            "While a target is within Monster Range, cast Revitalize Self when Stamina < this %."));
        p.Children.Add(IntRow("Get Mana At", state.Data.GetManaAt, 0, 100, 1, v => { state.Data.GetManaAt = v; Push(state); },
            "While a target is within Monster Range, cast Stamina to Mana Self when Mana < this % (needs stam > 15%)."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Idle Top-Off — no targets in range (%)"));
        p.Children.Add(IntRow("Top HP",   state.Data.TopOffHP,   0, 100, 1, v => { state.Data.TopOffHP   = v; Push(state); },
            "When no targets are within Monster Range, heal up to this HP %. Usually set higher than Heal At."));
        p.Children.Add(IntRow("Top Stam", state.Data.TopOffStam, 0, 100, 1, v => { state.Data.TopOffStam = v; Push(state); },
            "When no targets are within Monster Range, re-stam up to this %."));
        p.Children.Add(IntRow("Top Mana", state.Data.TopOffMana, 0, 100, 1, v => { state.Data.TopOffMana = v; Push(state); },
            "When no targets are within Monster Range, recharge mana up to this %."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Helper Settings (%) — NOT WIRED YET"));
        p.Children.Add(IntRow("Heal Others",    state.Data.HealOthersAt,    0, 100, 1, v => { state.Data.HealOthersAt   = v; Push(state); },
            "Intended: cast Heal Other on a fellow when their HP < this %. Not implemented yet — slider is inert."));
        p.Children.Add(IntRow("Re-stam Others", state.Data.RestamOthersAt,  0, 100, 1, v => { state.Data.RestamOthersAt = v; Push(state); },
            "Intended: cast Revitalize Other on a fellow when their Stamina < this %. Not implemented yet — slider is inert."));
        p.Children.Add(IntRow("Infuse Others",  state.Data.InfuseOthersAt,  0, 100, 1, v => { state.Data.InfuseOthersAt = v; Push(state); },
            "Intended: cast Infuse Mana Other on a fellow when their Mana < this %. Not implemented yet — slider is inert."));
    }

    private static void BuildMeleeCombatTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(SectionHeader("Attack Power & Height"));
        p.Children.Add(BoolRow("Use Recklessness", state.Data.UseRecklessness,
            v => { state.Data.UseRecklessness = v; Push(state); },
            "When enabled and Recklessness is trained, auto power uses 80% instead of 100%."));

        p.Children.Add(Spacer());
        bool meleeAuto = state.Data.MeleeAttackPower < 0;
        p.Children.Add(BoolRow("Melee Auto Power", meleeAuto, v =>
        {
            state.Data.MeleeAttackPower = v ? -1 : 100;
            Push(state);
        }));
        if (!meleeAuto)
        {
            p.Children.Add(IntRow("Melee Power %", state.Data.MeleeAttackPower, 0, 100, 5,
                v => { state.Data.MeleeAttackPower = v; Push(state); }));
        }
        p.Children.Add(ComboRow("Melee Attack Height", AttackHeights, state.Data.MeleeAttackHeight,
            v => { state.Data.MeleeAttackHeight = v; Push(state); }, picker));

        p.Children.Add(Spacer());
        bool missileAuto = state.Data.MissileAttackPower < 0;
        p.Children.Add(BoolRow("Missile Auto Power", missileAuto, v =>
        {
            state.Data.MissileAttackPower = v ? -1 : 100;
            Push(state);
        }));
        if (!missileAuto)
        {
            p.Children.Add(IntRow("Missile Power %", state.Data.MissileAttackPower, 0, 100, 5,
                v => { state.Data.MissileAttackPower = v; Push(state); }));
        }
        p.Children.Add(ComboRow("Missile Attack Height", AttackHeights, state.Data.MissileAttackHeight,
            v => { state.Data.MissileAttackHeight = v; Push(state); }, picker));

        p.Children.Add(Spacer());
        p.Children.Add(BoolRow("Use Native Attack", state.Data.UseNativeAttack,
            v => { state.Data.UseNativeAttack = v; Push(state); },
            "Uses the client's combat pipeline for attacks.\nThe client handles turn-to-face naturally (no backwards arrows)."));

        p.Children.Add(Spacer());
        p.Children.Add(BoolRow("Summon Pets", state.Data.SummonPets,
            v => { state.Data.SummonPets = v; Push(state); }));
        p.Children.Add(IntRow("Pet Min Monsters", state.Data.PetMinMonsters, 1, 20, 1,
            v => { state.Data.PetMinMonsters = v; Push(state); }));
    }

    private static void BuildSpellCombatTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(SectionHeader("War/Void Casting Settings"));
        p.Children.Add(IntRow("Spell Interval (ms)", state.Data.SpellCastIntervalMs, 100, 1500, 50,
            v => { state.Data.SpellCastIntervalMs = v; Push(state); },
            "Delay between spell casts (buffing and combat).\nLower = faster spell chains. 400ms is a good balance.\nBelow 200ms may cause fizzles or dropped casts on laggy servers."));
        p.Children.Add(BoolRow("Cast Dispel Self", state.Data.CastDispelSelf,
            v => { state.Data.CastDispelSelf = v; Push(state); }));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Ring Spell Override"));
        p.Children.Add(IntRow("Min Ring Targets", state.Data.MinRingTargets, 1, 20, 1,
            v => { state.Data.MinRingTargets = v; Push(state); },
            "If this many monsters are within ring range, ring spells are used instead of arc/bolt/streak."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Spell Difficulty (Min Buffed Skill)"));
        p.Children.Add(IntRow("Level 1", state.Data.MinSkillLevelTier1, 1,  500, 5, v => { state.Data.MinSkillLevelTier1 = v; Push(state); }));
        p.Children.Add(IntRow("Level 2", state.Data.MinSkillLevelTier2, 1,  500, 5, v => { state.Data.MinSkillLevelTier2 = v; Push(state); }));
        p.Children.Add(IntRow("Level 3", state.Data.MinSkillLevelTier3, 1,  500, 5, v => { state.Data.MinSkillLevelTier3 = v; Push(state); }));
        p.Children.Add(IntRow("Level 4", state.Data.MinSkillLevelTier4, 1,  500, 5, v => { state.Data.MinSkillLevelTier4 = v; Push(state); }));
        p.Children.Add(IntRow("Level 5", state.Data.MinSkillLevelTier5, 1,  500, 5, v => { state.Data.MinSkillLevelTier5 = v; Push(state); }));
        p.Children.Add(IntRow("Level 6", state.Data.MinSkillLevelTier6, 1,  500, 5, v => { state.Data.MinSkillLevelTier6 = v; Push(state); }));
        p.Children.Add(IntRow("Level 7", state.Data.MinSkillLevelTier7, 1,  500, 5, v => { state.Data.MinSkillLevelTier7 = v; Push(state); }));
        p.Children.Add(IntRow("Level 8", state.Data.MinSkillLevelTier8, 1,  500, 5, v => { state.Data.MinSkillLevelTier8 = v; Push(state); }));
    }

    private static void BuildRangesTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(SectionHeader("Standard Ranges (Yards)"));
        p.Children.Add(IntRow("Monster Range",  state.Data.MonsterRange,  1, 200, 1, v => { state.Data.MonsterRange  = v; Push(state); }));
        p.Children.Add(IntRow("Ring Range",     state.Data.RingRange,     1,  50, 1, v => { state.Data.RingRange     = v; Push(state); }));
        p.Children.Add(IntRow("Approach Range", state.Data.ApproachRange, 1,  50, 1, v => { state.Data.ApproachRange = v; Push(state); }));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Corpse Acquisition (Yards)"));
        p.Children.Add(DoubleRow("Corpse Max (yd)", state.Data.CorpseApproachRangeMax, 0.5, 50.0, 0.5,
            v => { state.Data.CorpseApproachRangeMax = v; Push(state); }));
        p.Children.Add(DoubleRow("Corpse Min (yd)", state.Data.CorpseApproachRangeMin, 0.5, 20.0, 0.5,
            v => { state.Data.CorpseApproachRangeMin = v; Push(state); }));
    }

    private static void BuildNavigationTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(BoolRow("Boost Nav Priority", state.Data.BoostNavPriority,
            v => { state.Data.BoostNavPriority = v; Push(state); }));
        p.Children.Add(FloatRow("Follow/Nav Min (yd)", state.Data.FollowNavMin, 0.5f, 20f, 0.1f,
            v => { state.Data.FollowNavMin = v; Push(state); },
            "Arrival distance in yards. Also sets the nav marker ring radius."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Nav Marker Display"));
        p.Children.Add(FloatRow("Ring Thickness", state.Data.NavRingThickness, 1f, 16f, 1f,
            v => { state.Data.NavRingThickness = v; Push(state); }));
        p.Children.Add(FloatRow("Line Thickness", state.Data.NavLineThickness, 1f, 16f, 1f,
            v => { state.Data.NavLineThickness = v; Push(state); }));
        p.Children.Add(FloatRow("Height Offset", state.Data.NavHeightOffset, -5f, 5f, 0.05f,
            v => { state.Data.NavHeightOffset = v; Push(state); },
            "Vertical offset for nav markers above the ground. Negative = lower."));
        p.Children.Add(FloatRow("Slope Sink", state.Data.NavSlopeSink, 0f, 8f, 0.1f,
            v => { state.Data.NavSlopeSink = v; Push(state); },
            "Extra downward offset on slopes only, per unit of terrain steepness. 0 = off; flat ground is unaffected. ~1.5 cancels the float for a default-radius ring."));
        p.Children.Add(BoolRow("Show Terrain Passability", state.Data.ShowTerrainPassability,
            v => { state.Data.ShowTerrainPassability = v; Push(state); },
            "Highlight impassable terrain triangles in red."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Doors"));
        p.Children.Add(BoolRow("Open Doors While Navigating", state.Data.OpenDoors,
            v => { state.Data.OpenDoors = v; Push(state); },
            "Automatically open doors encountered during navigation."));
        if (state.Data.OpenDoors)
        {
            p.Children.Add(FloatRow("Door Detection Range (yd)", state.Data.OpenDoorRange, 0.1f, 70f, 1f,
                v => { state.Data.OpenDoorRange = v; Push(state); }));
            p.Children.Add(BoolRow("Auto-Unlock Doors", state.Data.AutoUnlockDoors,
                v => { state.Data.AutoUnlockDoors = v; Push(state); },
                "If a door is locked, try to use a lockpick from your Consumable Items list."));
        }

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Movement Engine"));
        p.Children.Add(ComboRow("Mode", MovementModes, state.Data.MovementMode,
            v => { state.Data.MovementMode = v; Push(state); }, picker,
            "Legacy: SetAutorun + smooth heading servo (TurnToHeading)\nTier 1: SetAutorun + CM_Movement turn commands (DoMovement)\nTier 2: Client physics MoveToPosition (not built yet — falls back to Legacy)"));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Steering"));
        p.Children.Add(FloatRow("Stop & Turn Angle", state.Data.NavStopTurnAngle, 1f, 90f, 1f,
            v => { state.Data.NavStopTurnAngle = v; Push(state); },
            "Stop forward motion and turn in place when heading error exceeds this."));
        p.Children.Add(FloatRow("Resume Run Angle", state.Data.NavResumeTurnAngle, 1f, 45f, 1f,
            v => { state.Data.NavResumeTurnAngle = v; Push(state); },
            "Resume running once the turn-in-place error drops below this."));
        p.Children.Add(FloatRow("Dead Zone", state.Data.NavDeadZone, 0.5f, 20f, 0.5f,
            v => { state.Data.NavDeadZone = v; Push(state); },
            "Ignore heading corrections smaller than this."));
        p.Children.Add(FloatRow("Sweep Detect Mult", state.Data.NavSweepMult, 0.5f, 10f, 0.1f,
            v => { state.Data.NavSweepMult = v; Push(state); },
            "Closest-approach detection radius multiplier."));
        p.Children.Add(FloatRow("Lookahead (yd)", state.Data.NavLookaheadYards, 0f, 30f, 0.5f,
            v => { state.Data.NavLookaheadYards = MathF.Max(0f, v); Push(state); },
            "Within this distance of a waypoint, blend the aim point toward the next one to cut corners smoothly. 0 = off."));
        p.Children.Add(FloatRow("Turn Rate (deg/s)", state.Data.NavTurnRateDegPerSec, 30f, 720f, 15f,
            v => { state.Data.NavTurnRateDegPerSec = v; Push(state); },
            "Legacy / Mode 0 heading-servo max turn speed. Ignored by Tier 1 and Tier 2."));
        p.Children.Add(FloatRow("Tier1 Turn Speed", state.Data.NavTier1TurnSpeed, 0.5f, 15f, 0.5f,
            v => { state.Data.NavTier1TurnSpeed = v; Push(state); },
            "Tier 1 (CM_Movement) turn-command speed = magnitude of the client turn_speed. Higher = snappier turns. Only used in Tier 1 mode."));
        p.Children.Add(FloatRow("Post-Portal Delay (s)", state.Data.PostPortalDelaySec, 0f, 30f, 0.25f,
            v => { state.Data.PostPortalDelaySec = MathF.Max(0f, v); Push(state); },
            "Seconds to settle after any portal/recall teleport before nav resumes."));

        if (state.Data.MovementMode == 2)
        {
            p.Children.Add(Spacer());
            p.Children.Add(SectionHeader("Tier 2 Tuning"));
            p.Children.Add(FloatRow("Speed",               state.Data.T2Speed,       0.1f, 5f,    0.1f, v => { state.Data.T2Speed       = v; Push(state); }));
            p.Children.Add(FloatRow("Walk Within (yd)",    state.Data.T2WalkWithinYd, 1f,  50f,   1f,   v => { state.Data.T2WalkWithinYd = v; Push(state); }));
            p.Children.Add(FloatRow("Stop Distance (yd)",  state.Data.T2DistanceTo,  0.1f, 10f,   0.1f, v => { state.Data.T2DistanceTo   = v; Push(state); }));
            p.Children.Add(FloatRow("Reissue Timeout (ms)",state.Data.T2ReissueMs,   100f, 10000f,100f,  v => { state.Data.T2ReissueMs    = v; Push(state); }));
            p.Children.Add(FloatRow("Max Range (yd)",      state.Data.T2MaxRangeYd,   50f, 2000f,  50f, v => { state.Data.T2MaxRangeYd   = v; Push(state); }));
            p.Children.Add(IntRow("Max Landblock Dist",    state.Data.T2MaxLandblocks, 1, 20, 1,         v => { state.Data.T2MaxLandblocks = v; Push(state); }));
        }
    }

    private static void BuildBuffingTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(BoolRow("Enable Buffing", state.Data.EnableBuffing,
            v => { state.Data.EnableBuffing = v; Push(state); }));
        p.Children.Add(BoolRow("Rebuff When Idle", state.Data.RebuffWhenIdle,
            v => { state.Data.RebuffWhenIdle = v; Push(state); }));
        p.Children.Add(IntRow("Rebuff With (seconds left)", state.Data.RebuffSecondsRemaining, 30, 1800, 30,
            v => { state.Data.RebuffSecondsRemaining = v; Push(state); },
            "Recast a self buff when its remaining duration drops below this value.\nDefault 300 (5 minutes). Lower values rebuff more eagerly."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Buff Difficulty (Min Buffed Skill)"));
        p.Children.Add(IntRow("Level 1", state.Data.BuffMinSkillLevelTier1, 1, 500, 5, v => { state.Data.BuffMinSkillLevelTier1 = v; Push(state); }));
        p.Children.Add(IntRow("Level 2", state.Data.BuffMinSkillLevelTier2, 1, 500, 5, v => { state.Data.BuffMinSkillLevelTier2 = v; Push(state); }));
        p.Children.Add(IntRow("Level 3", state.Data.BuffMinSkillLevelTier3, 1, 500, 5, v => { state.Data.BuffMinSkillLevelTier3 = v; Push(state); }));
        p.Children.Add(IntRow("Level 4", state.Data.BuffMinSkillLevelTier4, 1, 500, 5, v => { state.Data.BuffMinSkillLevelTier4 = v; Push(state); }));
        p.Children.Add(IntRow("Level 5", state.Data.BuffMinSkillLevelTier5, 1, 500, 5, v => { state.Data.BuffMinSkillLevelTier5 = v; Push(state); }));
        p.Children.Add(IntRow("Level 6", state.Data.BuffMinSkillLevelTier6, 1, 500, 5, v => { state.Data.BuffMinSkillLevelTier6 = v; Push(state); }));
        p.Children.Add(IntRow("Level 7", state.Data.BuffMinSkillLevelTier7, 1, 500, 5, v => { state.Data.BuffMinSkillLevelTier7 = v; Push(state); }));
        p.Children.Add(IntRow("Level 8", state.Data.BuffMinSkillLevelTier8, 1, 500, 5, v => { state.Data.BuffMinSkillLevelTier8 = v; Push(state); }));
    }

    private static void BuildCraftingTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(SectionHeader("Missile Ammo Crafting"));
        p.Children.Add(BoolRow("Enable Missile Crafting", state.Data.EnableMissileCrafting,
            v => { state.Data.EnableMissileCrafting = v; Push(state); },
            "Auto-manage missile ammo when the ammo slot is empty or low."));

        if (!state.Data.EnableMissileCrafting)
        {
            p.Children.Add(new TextBlock { Text = "(Disabled)", Foreground = ColMute, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
            return;
        }

        p.Children.Add(Spacer());
        if (!string.IsNullOrEmpty(state.Data.MissileCraftingState))
        {
            var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 2) };
            stateRow.Children.Add(new TextBlock { Text = "State:", Foreground = ColMute, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            stateRow.Children.Add(new TextBlock
            {
                Text = state.Data.MissileCraftingState,
                Foreground = state.Data.MissileCraftingActive ? ColAmber : ColMute,
                FontSize = 11,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            p.Children.Add(stateRow);

            if (!string.IsNullOrEmpty(state.Data.MissileCraftingStatus))
            {
                p.Children.Add(new TextBlock
                {
                    Text = state.Data.MissileCraftingStatus,
                    Foreground = ColTextDim,
                    FontSize = 10,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
        }
    }

    private static void BuildLootingTab(PanelState state, StackPanel p, PickerState picker)
    {
        p.Children.Add(BoolRow("Enable Looting", state.Data.EnableLooting,
            v => { state.Data.EnableLooting = v; Push(state); }));
        p.Children.Add(BoolRow("Boost Loot Priority", state.Data.BoostLootPriority,
            v => { state.Data.BoostLootPriority = v; Push(state); }));
        p.Children.Add(BoolRow("Loot Only Rare Corpses", state.Data.LootOnlyRareCorpses,
            v => { state.Data.LootOnlyRareCorpses = v; Push(state); }));
        p.Children.Add(BoolRow("Jump When Looting", state.Data.LootJumpEnabled,
            v => { state.Data.LootJumpEnabled = v; Push(state); }));
        if (state.Data.LootJumpEnabled)
        {
            p.Children.Add(IntRow("Jump Height", state.Data.LootJumpHeight, 1, 100, 5,
                v => { state.Data.LootJumpHeight = v; Push(state); }));
        }

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Corpse Ownership"));
        p.Children.Add(ComboRow("Loot From", LootOwnershipModes, state.Data.LootOwnership,
            v => { state.Data.LootOwnership = v; Push(state); }, picker));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Inventory Management"));
        p.Children.Add(BoolRow("Enable Autostack", state.Data.EnableAutostack,
            v => { state.Data.EnableAutostack = v; Push(state); }));
        p.Children.Add(BoolRow("Enable Autocram", state.Data.EnableAutocram,
            v => { state.Data.EnableAutocram = v; Push(state); },
            "Automatically moves items from your main pack into side packs.\nNote: recently used weapons stay in the main pack."));
        p.Children.Add(BoolRow("Combine Salvage Bags", state.Data.EnableCombineSalvage,
            v => { state.Data.EnableCombineSalvage = v; Push(state); },
            "After the salvage queue empties, move same-name bags together so the server merges them."));
        p.Children.Add(BoolRow("Combine Bags During Salvage", state.Data.CombineBagsDuringSalvage,
            v => { state.Data.CombineBagsDuringSalvage = v; Push(state); },
            "When salvaging an item, also add any under-full salvage bag of the same material to the salvage panel."));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Loot Timers (ms)"));
        p.Children.Add(IntRow("Inter-Item Delay",   state.Data.LootInterItemDelayMs, 0, 5000, 25,  v => { state.Data.LootInterItemDelayMs  = v; Push(state); }));
        p.Children.Add(IntRow("Content Settle",     state.Data.LootContentSettleMs,  0, 5000, 25,  v => { state.Data.LootContentSettleMs   = v; Push(state); }));
        p.Children.Add(IntRow("Empty Corpse Wait",  state.Data.LootEmptyCorpseMs,    0, 5000, 25,  v => { state.Data.LootEmptyCorpseMs     = v; Push(state); }));
        p.Children.Add(IntRow("Closing Delay",      state.Data.LootClosingDelayMs,   0, 5000, 25,  v => { state.Data.LootClosingDelayMs    = v; Push(state); }));
        p.Children.Add(IntRow("Assess Window",      state.Data.LootAssessWindowMs,   0, 5000, 25,  v => { state.Data.LootAssessWindowMs    = v; Push(state); }));
        p.Children.Add(IntRow("Loot Retry Timeout", state.Data.LootRetryTimeoutMs,   0, 10000, 100, v => { state.Data.LootRetryTimeoutMs   = v; Push(state); }));
        p.Children.Add(IntRow("Corpse Open Retry",  state.Data.LootOpenRetryMs,      0, 10000, 100, v => { state.Data.LootOpenRetryMs      = v; Push(state); }));
        p.Children.Add(IntRow("Corpse Timeout",     state.Data.LootCorpseTimeoutMs,  0, 60000, 500, v => { state.Data.LootCorpseTimeoutMs  = v; Push(state); }));

        p.Children.Add(Spacer());
        p.Children.Add(SectionHeader("Salvage Timers (ms) - First / Fast"));
        p.Children.Add(IntRow("Open (First)",       state.Data.SalvageOpenDelayFirstMs,   0, 5000, 50, v => { state.Data.SalvageOpenDelayFirstMs   = v; Push(state); }));
        p.Children.Add(IntRow("Open (Fast)",        state.Data.SalvageOpenDelayFastMs,    0, 2000, 25, v => { state.Data.SalvageOpenDelayFastMs    = v; Push(state); }));
        p.Children.Add(IntRow("Add Item (First)",   state.Data.SalvageAddDelayFirstMs,    0, 5000, 50, v => { state.Data.SalvageAddDelayFirstMs    = v; Push(state); }));
        p.Children.Add(IntRow("Add Item (Fast)",    state.Data.SalvageAddDelayFastMs,     0, 2000, 25, v => { state.Data.SalvageAddDelayFastMs     = v; Push(state); }));
        p.Children.Add(IntRow("Salvage Click",      state.Data.SalvageSalvageDelayMs,     0, 2000, 25, v => { state.Data.SalvageSalvageDelayMs     = v; Push(state); }));
        p.Children.Add(IntRow("Result (First)",     state.Data.SalvageResultDelayFirstMs, 0, 5000, 50, v => { state.Data.SalvageResultDelayFirstMs = v; Push(state); }));
        p.Children.Add(IntRow("Result (Fast)",      state.Data.SalvageResultDelayFastMs,  0, 2000, 25, v => { state.Data.SalvageResultDelayFastMs  = v; Push(state); }));
    }

    // =========================================================================
    //  Control builders
    // =========================================================================

    private static Control BoolRow(string label, bool value, Action<bool> onChange, string? tooltip = null)
    {
        bool current = value;
        var dot = new Border
        {
            Width = 12, Height = 12,
            Background = current ? ColToggleOn : ColToggleOff,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var lbl = new TextBlock
        {
            Text = label,
            Foreground = ColTextDim,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        row.Children.Add(dot);
        row.Children.Add(lbl);

        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                current = !current;
                dot.Background = current ? ColToggleOn : ColToggleOff;
                onChange(current);
            }
        };

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }

        return row;
    }

    private static Control IntRow(string label, int value, int min, int max, int step, Action<int> onChange, string? tooltip = null)
    {
        int current = Math.Clamp(value, min, max);

        var valueText = new TextBlock
        {
            Text = current.ToString(),
            Foreground = ColAmber,
            FontSize = 11,
            Width = 48,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var minusBtn = StepButton("-");
        var plusBtn  = StepButton("+");

        minusBtn.Click += (_, _) =>
        {
            current = Math.Max(min, current - step);
            valueText.Text = current.ToString();
            onChange(current);
        };
        plusBtn.Click += (_, _) =>
        {
            current = Math.Min(max, current + step);
            valueText.Text = current.ToString();
            onChange(current);
        };

        // TextBox for direct entry
        var tb = new TextBox
        {
            Text = current.ToString(),
            FontSize = 11,
            Width = 52,
            Height = 20,
            Padding = new Thickness(2),
            Background = ColPanelBg,
            Foreground = ColAmber,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        DisableInnerScroll(tb);
        tb.GotFocus += (_, _) => { /* suppress poll while typing */ };
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int parsed))
            {
                current = Math.Clamp(parsed, min, max);
                tb.Text = current.ToString();
                valueText.Text = current.ToString();
                onChange(current);
            }
            else
            {
                tb.Text = current.ToString();
            }
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                if (int.TryParse(tb.Text, out int parsed))
                {
                    current = Math.Clamp(parsed, min, max);
                    tb.Text = current.ToString();
                    valueText.Text = current.ToString();
                    onChange(current);
                }
                else tb.Text = current.ToString();
            }
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,20,56,20"),
            Margin = new Thickness(0, 2, 0, 2),
            Height = 22,
        };
        var lblBlock = new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lblBlock, 0);
        Grid.SetColumn(minusBtn, 1);
        Grid.SetColumn(tb,       2);
        Grid.SetColumn(plusBtn,  3);
        row.Children.Add(lblBlock);
        row.Children.Add(minusBtn);
        row.Children.Add(tb);
        row.Children.Add(plusBtn);

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }
        return row;
    }

    private static Control FloatRow(string label, float value, float min, float max, float step, Action<float> onChange, string? tooltip = null)
    {
        float current = Math.Clamp(value, min, max);

        var tb = new TextBox
        {
            Text = current.ToString("G4", CultureInfo.InvariantCulture),
            FontSize = 11,
            Width = 64,
            Height = 20,
            Padding = new Thickness(2),
            Background = ColPanelBg,
            Foreground = ColAmber,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        DisableInnerScroll(tb);

        void Commit()
        {
            if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                current = Math.Clamp(parsed, min, max);
                tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
                onChange(current);
            }
            else tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
        }

        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };

        var minusBtn = StepButton("-");
        var plusBtn  = StepButton("+");
        minusBtn.Click += (_, _) =>
        {
            current = Math.Clamp(current - step, min, max);
            tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
            onChange(current);
        };
        plusBtn.Click += (_, _) =>
        {
            current = Math.Clamp(current + step, min, max);
            tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
            onChange(current);
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,20,68,20"),
            Margin = new Thickness(0, 2, 0, 2),
            Height = 22,
        };
        var lblBlock = new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lblBlock, 0);
        Grid.SetColumn(minusBtn, 1);
        Grid.SetColumn(tb,       2);
        Grid.SetColumn(plusBtn,  3);
        row.Children.Add(lblBlock);
        row.Children.Add(minusBtn);
        row.Children.Add(tb);
        row.Children.Add(plusBtn);

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }
        return row;
    }

    private static Control DoubleRow(string label, double value, double min, double max, double step, Action<double> onChange, string? tooltip = null)
    {
        double current = Math.Clamp(value, min, max);

        var tb = new TextBox
        {
            Text = current.ToString("G4", CultureInfo.InvariantCulture),
            FontSize = 11,
            Width = 64,
            Height = 20,
            Padding = new Thickness(2),
            Background = ColPanelBg,
            Foreground = ColAmber,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        DisableInnerScroll(tb);

        void Commit()
        {
            if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                current = Math.Clamp(parsed, min, max);
                tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
                onChange(current);
            }
            else tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
        }

        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };

        var minusBtn = StepButton("-");
        var plusBtn  = StepButton("+");
        minusBtn.Click += (_, _) =>
        {
            current = Math.Clamp(current - step, min, max);
            tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
            onChange(current);
        };
        plusBtn.Click += (_, _) =>
        {
            current = Math.Clamp(current + step, min, max);
            tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
            onChange(current);
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,20,68,20"),
            Margin = new Thickness(0, 2, 0, 2),
            Height = 22,
        };
        var lblBlock = new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lblBlock, 0);
        Grid.SetColumn(minusBtn, 1);
        Grid.SetColumn(tb,       2);
        Grid.SetColumn(plusBtn,  3);
        row.Children.Add(lblBlock);
        row.Children.Add(minusBtn);
        row.Children.Add(tb);
        row.Children.Add(plusBtn);

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }
        return row;
    }

    private static Control ComboRow(string label, string[] items, int index, Action<int> onChange, PickerState picker, string? tooltip = null)
    {
        int current = Math.Clamp(index, 0, items.Length - 1);
        var btn = new Button
        {
            Content = items[current],
            FontSize = 10,
            Height = 20,
            Padding = new Thickness(4, 1),
            Background = ColBtnFill,
            Foreground = ColTextDim,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        btn.Click += (_, _) =>
            picker.Show(btn, items, current, idx =>
            {
                current = idx;
                btn.Content = items[idx];
                onChange(idx);
            });

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 2),
            Height = 22,
        };
        var lblBlock = new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        btn.MinWidth = 160;
        Grid.SetColumn(lblBlock, 0);
        Grid.SetColumn(btn,      1);
        row.Children.Add(lblBlock);
        row.Children.Add(btn);

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }
        return row;
    }

    // SimpleTheme wraps TextBox content in a ScrollViewer, and its vertical
    // scrollbar's up RepeatButton is a filled triangle Path. On these short
    // (Height=20) single-line numeric fields that scrollbar shows and the
    // triangle lands on top of the digits. These fields never scroll —
    // disable both inner scrollbars so the glyph is gone.
    private static void DisableInnerScroll(Control c)
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(c, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(c, ScrollBarVisibility.Disabled);
    }

    private static Button StepButton(string label) => new Button
    {
        Content = label,
        Width = 18,
        Height = 20,
        FontSize = 10,
        Padding = new Thickness(0),
        Background = ColBtnFill,
        Foreground = ColTextDim,
        BorderBrush = ColBtnBord,
        BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static Border SectionHeader(string text) => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42)),
        Padding = new Thickness(4, 2),
        Margin = new Thickness(0, 4, 0, 2),
        Child = new TextBlock
        {
            Text = text,
            Foreground = ColAmber,
            FontSize = 11,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        },
    };

    private static Border Spacer() => new Border { Height = 6 };

    // =========================================================================
    //  Plugin bridge
    // =========================================================================

    private static void TryBind()
    {
        var plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        if (_getSettingsJson == null)
        {
            IntPtr p1 = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetSettingsJson");
            if (p1 != IntPtr.Zero)
                _getSettingsJson = Marshal.GetDelegateForFunctionPointer<GetSettingsJsonFn>(p1);
        }
        if (_setSettingsJson == null)
        {
            IntPtr p2 = GetProcAddress(plugin.ModuleHandle, "RynthPluginSetSettingsJson");
            if (p2 != IntPtr.Zero)
                _setSettingsJson = Marshal.GetDelegateForFunctionPointer<SetSettingsJsonFn>(p2);
        }
    }

    private static bool TryFetch(out Settings settings)
    {
        settings = new Settings();
        if (_getSettingsJson == null) return false;
        try
        {
            IntPtr ptr = _getSettingsJson();
            if (ptr == IntPtr.Zero) return false;
            string? json = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrEmpty(json)) return false;
            var parsed = JsonSerializer.Deserialize(json, SettingsPanelJsonContext.Default.Settings);
            if (parsed == null) return false;
            settings = parsed;
            return true;
        }
        catch { return false; }
    }

    private static void Push(PanelState state)
    {
        if (_setSettingsJson == null) TryBind();
        if (_setSettingsJson == null) return;
        IntPtr ansi = IntPtr.Zero;
        try
        {
            string json = JsonSerializer.Serialize(state.Data, SettingsPanelJsonContext.Default.Settings);
            ansi = Marshal.StringToHGlobalAnsi(json);
            _setSettingsJson(ansi);
            state.Dirty = false;
        }
        catch { }
        finally
        {
            if (ansi != IntPtr.Zero) Marshal.FreeHGlobal(ansi);
        }
    }
}
