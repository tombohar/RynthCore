using System.Globalization;
using RynthCore.Engine.ImGuiBackend.Hud;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI
{
    /// <summary>Stub: counts saves, never runs them (no file I/O in the tests).</summary>
    internal static class UiBackgroundWriter
    {
        public static int Enqueued;
        public static void Enqueue(string label, Action work) => Enqueued++;
    }
}

namespace PlateSizingTests
{
    using S = MonsterHudSettings;

    /// <summary>
    /// The nameplate sizing settings (per plate type: bar width, bar height, text size) and the
    /// ArgbOpacity helper behind the Vision overlay opacity sliders; the portal names' classification and settings;
    /// the debuff icon sizes and the debuff row's lengths (DebuffRowMetrics).
    /// </summary>
    internal static class Program
    {
        private static int _checks, _failed;
        private static string _case = "";

        private static int Main()
        {
            DefaultsAreTodaysLook();
            RoundTrip();
            OldFileLoads();
            Clamping();
            ResetSizesOnlyTouchesSizes();
            ResetAllResetsSizes();
            RefactorKeepsEveryKey();
            OpacityDefaults();
            OpacityRoundTripsEveryAlpha();
            OpacitySetKeepsRgb();

            PortalsClassifyFromItemType();
            PortalNamesDefaultOn();
            PortalNamesOldFileLoadsOn();
            PortalNamesRoundTrip();
            PortalDistanceClamps();
            PortalNamesFollowAllAndReset();

            DebuffIconSizesDefault100();
            DebuffIconSizesOldFileLoads100();
            DebuffIconSizesClamp();
            DebuffIconSizesRoundTripApart();
            DebuffIconSizesReset();
            DebuffIconSizeChatForm();
            DebuffRowAt100IsTheOldRow();
            DebuffRowScalesWithTheSetting();
            DebuffRowWidthAndCover();

            HealthRefreshDefaultsOn();
            HealthRefreshRoundTripAndClamp();

            Console.WriteLine();
            Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
            Console.WriteLine(_failed == 0 ? "ALL PLATE SIZING TESTS PASSED." : $"{_failed} FAILED.");
            return _failed == 0 ? 0 : 1;
        }

        private static (string Key, Func<float> Get, Action<float> Set, float Min, float Max)[] Sizes => new (string, Func<float>, Action<float>, float, float)[]
        {
            ("monsterBarWidth", () => S.MonsterBarWidth, v => S.MonsterBarWidth = v, S.MinBarWidth, S.MaxBarWidth),
            ("monsterBarHeight", () => S.MonsterBarHeight, v => S.MonsterBarHeight = v, S.MinBarHeight, S.MaxBarHeight),
            ("monsterTextSize", () => S.MonsterTextSize, v => S.MonsterTextSize = v, S.MinTextSize, S.MaxTextSize),
            ("npcTextSize", () => S.NpcTextSize, v => S.NpcTextSize = v, S.MinTextSize, S.MaxTextSize),
            ("playerTextSize", () => S.PlayerTextSize, v => S.PlayerTextSize = v, S.MinTextSize, S.MaxTextSize),
            ("selfBarWidth", () => S.SelfBarWidth, v => S.SelfBarWidth = v, S.MinBarWidth, S.MaxBarWidth),
            ("selfBarHeight", () => S.SelfBarHeight, v => S.SelfBarHeight = v, S.MinBarHeight, S.MaxBarHeight),
            ("selfTextSize", () => S.SelfTextSize, v => S.SelfTextSize = v, S.MinTextSize, S.MaxTextSize),
        };

        private static string[] Written()
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            S.WriteAll(sw);
            return sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        }

        // ── nameplate sizes ─────────────────────────────────────────────────

        private static void DefaultsAreTodaysLook()
        {
            Case("defaults: every size multiplier is 1 (today's look), inside its range");
            foreach (var s in Sizes)
            {
                Check(s.Get() == 1.0f, $"{s.Key} defaults to 1");
                Check(s.Min <= 1.0f && 1.0f <= s.Max, $"{s.Key} range [{s.Min}, {s.Max}] holds 1");
            }
            string[] lines = Written();
            foreach (var s in Sizes)
                Check(lines.Contains(s.Key + "=1"), $"{s.Key}=1 written");
        }

        private static void RoundTrip()
        {
            Case("save/load: non-default sizes come back");
            float[] values = { 1.75f, 0.5f, 1.25f, 0.8f, 2f, 2.5f, 3f, 0.65f };
            var sizes = Sizes;
            for (int i = 0; i < sizes.Length; i++) sizes[i].Set(values[i]);
            S.Scale = 1.3f; S.SelfNumbers = true;
            string[] lines = Written();

            S.ResetToDefaults();
            Check(S.MonsterBarWidth == 1f && S.SelfTextSize == 1f, "reset before reload");
            S.ApplyLines(lines);
            for (int i = 0; i < sizes.Length; i++)
                Check(MathF.Abs(sizes[i].Get() - values[i]) < 0.001f, $"{sizes[i].Key} = {values[i]} after reload (got {sizes[i].Get()})");
            Check(MathF.Abs(S.Scale - 1.3f) < 0.001f && S.SelfNumbers, "other settings in the same file still load");
            S.ResetToDefaults();
        }

        private static void OldFileLoads()
        {
            Case("a file from before 2026-10-05 (no size keys) loads with sizes at 1");
            S.MonsterBarWidth = 2f; S.NpcTextSize = 1.5f;
            S.ResetToDefaults();
            S.ApplyLines(new[]
            {
                "# RynthCore monster nameplates - auto-generated, hand-edits OK. /rc plates in game.",
                "enabled=1", "scale=1.2", "opacity=0.8", "selfNumbers=1", "npcNames=1",
            });
            foreach (var s in Sizes) Check(s.Get() == 1f, $"{s.Key} stays 1");
            Check(MathF.Abs(S.Scale - 1.2f) < 0.001f, "scale from the old file");
        }

        private static void Clamping()
        {
            Case("load: out-of-range values clamp, junk is ignored");
            S.ResetToDefaults();
            S.ApplyLines(new[]
            {
                "monsterBarWidth=9", "monsterBarHeight=0.01", "monsterTextSize=-3",
                "npcTextSize=abc", "playerTextSize=", "selfBarWidth=0.1", "selfBarHeight=99", "selfTextSize=1e9",
            });
            Check(S.MonsterBarWidth == S.MaxBarWidth, "bar width capped");
            Check(S.MonsterBarHeight == S.MinBarHeight, "bar height floored");
            Check(S.MonsterTextSize == S.MinTextSize, "negative text size floored");
            Check(S.NpcTextSize == 1f, "junk ignored (NPC)");
            Check(S.PlayerTextSize == 1f, "empty ignored (player)");
            Check(S.SelfBarWidth == S.MinBarWidth, "self width floored");
            Check(S.SelfBarHeight == S.MaxBarHeight, "self height capped");
            Check(S.SelfTextSize == S.MaxTextSize, "self text capped");
            S.ResetToDefaults();
        }

        private static void ResetSizesOnlyTouchesSizes()
        {
            Case("Reset bar and text sizes: sizes back to 1, nothing else, saved");
            S.ResetToDefaults();
            foreach (var s in Sizes) s.Set(1.5f);
            S.Scale = 1.4f; S.MaxPlates = 7; S.SelfName = true;
            int saves = RynthCore.Engine.UI.UiBackgroundWriter.Enqueued;
            S.ResetSizes();
            foreach (var s in Sizes) Check(s.Get() == 1f, $"{s.Key} back to 1");
            Check(MathF.Abs(S.Scale - 1.4f) < 0.001f && S.MaxPlates == 7 && S.SelfName, "other settings kept");
            Check(RynthCore.Engine.UI.UiBackgroundWriter.Enqueued == saves + 1, "one save queued");
            S.ResetToDefaults();
        }

        private static void ResetAllResetsSizes()
        {
            Case("Reset all to defaults also resets the sizes");
            foreach (var s in Sizes) s.Set(0.7f);
            S.ResetToDefaults();
            foreach (var s in Sizes) Check(s.Get() == 1f, $"{s.Key} back to 1");
        }

        private static void RefactorKeepsEveryKey()
        {
            Case("write -> apply -> write is stable (the load/save split kept every key)");
            S.ResetToDefaults();
            S.Enabled = false; S.MaxDistance = 50f; S.Filter = PlateFilter.Engaged; S.SelfPlacement = SelfPlacement.Follow;
            S.SelfPosition = SelfPlatePosition.Left; S.GainTime = 2.25f; S.NumSize = 1.5f; S.SelfFixedX = 0.25f;
            S.MonsterBarHeight = 2f; S.PlayerTextSize = 0.75f;
            string[] first = Written();
            S.ResetToDefaults();
            S.ApplyLines(first);
            string[] second = Written();
            Check(first.SequenceEqual(second), "identical text after a reload");
            Check(first.Length == second.Length && first.Length > 50, $"{first.Length} lines written");
            S.ResetToDefaults();
        }

        // ── overlay opacity (colour alpha) ───────────────────────────────────

        private static readonly (string Name, uint Argb, int Pct)[] TodaysColours =
        {
            ("slopes (impassable terrain)", 0x60FF2020u, 38),
            ("water", 0x600060FFu, 38),
            ("radar ring", 0x80FFD000u, 50),
        };

        private static void OpacityDefaults()
        {
            Case("opacity: today's colours show today's opacity, untouched they never change");
            foreach (var c in TodaysColours)
            {
                float p = ArgbOpacity.Percent(c.Argb);
                Check((int)MathF.Round(p) == c.Pct, $"{c.Name} shows {c.Pct}% (got {p:0.##})");
                Check(ArgbOpacity.WithPercent(c.Argb, p) == c.Argb, $"{c.Name} unchanged when re-applied");
            }
            Check(ArgbOpacity.Percent(0x00123456) == 0f && ArgbOpacity.Percent(0xFF123456) == 100f, "0 and 100 at the ends");
        }

        private static void OpacityRoundTripsEveryAlpha()
        {
            Case("opacity: every alpha byte survives percent and back");
            int bad = 0;
            for (uint a = 0; a <= 255; a++)
            {
                uint c = (a << 24) | 0x00ABCDEF;
                if (ArgbOpacity.WithPercent(c, ArgbOpacity.Percent(c)) != c) bad++;
            }
            Check(bad == 0, $"{bad} of 256 alpha values changed");
        }

        private static void OpacitySetKeepsRgb()
        {
            Case("opacity: setting it changes only the alpha, clamped to 0..100");
            Check(ArgbOpacity.WithPercent(0x60FF2020, 100f) == 0xFFFF2020, "100% = solid, RGB kept");
            Check(ArgbOpacity.WithPercent(0x60FF2020, 0f) == 0x00FF2020, "0% = invisible, RGB kept");
            Check(ArgbOpacity.WithPercent(0x80FFD000, 25f) == 0x40FFD000, "25% = 0x40");
            Check(ArgbOpacity.WithPercent(0x80FFD000, 150f) == 0xFFFFD000, "above 100 clamps");
            Check(ArgbOpacity.WithPercent(0x80FFD000, -5f) == 0x00FFD000, "below 0 clamps");
            Check(ArgbOpacity.WithPercent(0x80FFD000, float.NaN) == 0x00FFD000, "NaN is 0");
        }

        // ── portal names (2026-10-28) ───────────────────────────────────────

        private static void PortalsClassifyFromItemType()
        {
            Case("classification: ITEM_TYPE portal is a portal, a creature bit wins, everything else gets nothing");
            Check(PlateClass.FromItemType(0x10000) == PlateClass.Portal, "TYPE_PORTAL (0x10000) -> portal");
            Check(PlateClass.FromItemType(PlateClass.TypePortal) == PlateClass.Portal, "PlateClass.TypePortal -> portal");
            Check(PlateClass.TypePortal == 0x10000u && PlateClass.TypeCreature == 0x10u, "the AC ITEM_TYPE values");
            Check(PlateClass.FromItemType(0x10) == PlateClass.Creature, "TYPE_CREATURE (0x10) -> creature (its PWD decides the rest)");
            Check(PlateClass.FromItemType(0x10010) == PlateClass.Creature, "creature + portal bits -> creature (never loses a monster plate)");
            Check(PlateClass.FromItemType(0x10000 | 0x80) == PlateClass.Portal, "portal + another non-creature bit -> portal");
            // Weapons, armour, clothing, jewelry, food, money, misc, containers, gems, keys, lifestones, doors (misc)...
            uint[] others = { 0, 0x1, 0x2, 0x4, 0x8, 0x20, 0x40, 0x80, 0x100, 0x200, 0x400, 0x800, 0x2000, 0x8000, 0x20000, 0x80000, 0x100000, 0x80000000 };
            foreach (uint t in others)
                Check(PlateClass.FromItemType(t) == PlateClass.Other, $"type 0x{t:X} -> nothing");
            byte[] all = { PlateClass.Unknown, PlateClass.Other, PlateClass.Creature, PlateClass.Vendor, PlateClass.Player,
                           PlateClass.Pet, PlateClass.CreatureNoPwd, PlateClass.Portal };
            Check(all.Distinct().Count() == all.Length, "every class value is distinct (the per-id cache keeps them apart)");
            Check(PlateClass.Unknown == 0, "Unknown is 0 (a fresh cache entry means 'not classified yet')");
        }

        private static void PortalNamesDefaultOn()
        {
            Case("defaults: Portal names on, 60 yd, inside the slider's range; written to the file");
            S.ResetToDefaults();
            Check(S.PortalNames, "PortalNames defaults on");
            Check(S.PortalMaxDistance == 60f && S.DefaultPortalDistance == 60f, $"distance defaults to 60 (got {S.PortalMaxDistance})");
            Check(S.MinPortalDistance <= 60f && 60f <= S.MaxPortalDistanceLimit, "the default is inside [min, max]");
            Check(S.MaxPortalLabels > 0, "a positive cap of its own");
            string[] lines = Written();
            Check(lines.Contains("portalNames=1"), "portalNames=1 written");
            Check(lines.Contains("portalDistance=60"), "portalDistance=60 written");
            Check(S.Describe().Contains("portal names on (60yd)"), "the status line names it");
        }

        private static void PortalNamesOldFileLoadsOn()
        {
            Case("a file from before portal names (no portal keys) loads them on at 60 yd, with its own values");
            S.ResetToDefaults();
            S.NpcNames = false; S.PlayerNames = true; S.NpcMaxDistance = 45f; S.Scale = 1.2f;
            // 2026.10.28.1's file = today's lines without the two new keys.
            string[] old = Written().Where(l => !l.StartsWith("portalNames=", StringComparison.Ordinal) &&
                                                !l.StartsWith("portalDistance=", StringComparison.Ordinal)).ToArray();
            Check(old.Length == Written().Length - 2, "the old file lacks exactly the two keys");
            S.PortalNames = false; S.PortalMaxDistance = 15f;
            S.ResetToDefaults();   // the loader starts from the defaults
            S.ApplyLines(old);
            Check(S.PortalNames, "portal names on after loading the old file");
            Check(S.PortalMaxDistance == 60f, "portal distance 60 after loading the old file");
            Check(!S.NpcNames && S.PlayerNames && S.NpcMaxDistance == 45f && MathF.Abs(S.Scale - 1.2f) < 0.001f,
                "the old file's own values still load");

            S.ResetToDefaults();
            S.ApplyLines(new[] { "# RynthCore monster nameplates", "enabled=1", "npcNames=0" });
            Check(S.PortalNames && S.PortalMaxDistance == 60f, "on after a hand-written file without them (NPC names off there)");
            S.ResetToDefaults();
        }

        private static void PortalNamesRoundTrip()
        {
            Case("save/load: off and a distance come back; back on too; write -> apply -> write is stable");
            S.ResetToDefaults();
            S.PortalNames = false; S.PortalMaxDistance = 85f;
            string[] lines = Written();
            Check(lines.Contains("portalNames=0") && lines.Contains("portalDistance=85"), "portalNames=0, portalDistance=85 written");
            S.ResetToDefaults();
            S.ApplyLines(lines);
            Check(!S.PortalNames, "off after reload");
            Check(S.PortalMaxDistance == 85f, $"85 yd after reload (got {S.PortalMaxDistance})");
            Check(S.NpcNames && S.NpcMaxDistance == 30f && S.MaxNpcLabels == 15, "NPC names untouched by the portal keys");
            Check(lines.SequenceEqual(Written()), "identical text after a reload");

            S.PortalNames = true; S.PortalMaxDistance = 12.5f;
            S.ApplyLines(Written());
            Check(S.PortalNames && MathF.Abs(S.PortalMaxDistance - 12.5f) < 0.001f, "back on, 12.5 yd (one decimal kept)");
            Check(S.Describe().Contains("portal names on (12yd)") || S.Describe().Contains("portal names on (13yd)"), "status line shows the distance");
            S.ResetToDefaults();
        }

        private static void PortalDistanceClamps()
        {
            Case("load: the portal distance clamps to the slider's range, junk is ignored");
            S.ResetToDefaults();
            S.ApplyLines(new[] { "portalDistance=500" });
            Check(S.PortalMaxDistance == S.MaxPortalDistanceLimit, $"500 capped at {S.MaxPortalDistanceLimit}");
            S.ApplyLines(new[] { "portalDistance=1" });
            Check(S.PortalMaxDistance == S.MinPortalDistance, $"1 floored at {S.MinPortalDistance}");
            S.ApplyLines(new[] { "portalDistance=-40" });
            Check(S.PortalMaxDistance == S.MinPortalDistance, "negative floored");
            S.PortalMaxDistance = 70f;
            S.ApplyLines(new[] { "portalDistance=abc", "portalDistance=", "portalNames=yes" });
            Check(S.PortalMaxDistance == 70f, "junk and empty values leave the distance alone");
            Check(!S.PortalNames, "a value other than 1 is off (as every switch in the file)");
            Check(S.MinPortalDistance < S.MaxPortalDistanceLimit && S.MaxPortalDistanceLimit <= S.MaxDistanceLimit,
                "a sane range, no further than the monster plates' limit");
            S.ResetToDefaults();
        }

        private static void PortalNamesFollowAllAndReset()
        {
            Case("all off / all on (/rv plates all) and Reset all include portal names");
            S.ResetToDefaults();
            S.SetAll(false);
            Check(!S.PortalNames && !S.NpcNames && !S.Enabled, "all off turns portal names off with NPC names");
            S.SetAll(true);
            Check(S.PortalNames && S.NpcNames && S.Enabled, "all on turns them back on");
            S.PortalNames = false; S.PortalMaxDistance = 100f;
            S.ResetToDefaults();
            Check(S.PortalNames && S.PortalMaxDistance == 60f, "Reset all: on, 60 yd");
            S.PortalMaxDistance = 100f;
            S.ResetSizes();
            Check(S.PortalMaxDistance == 100f, "Reset bar and text sizes leaves the portal distance alone");
            S.ResetToDefaults();
        }

        // ── debuff icon sizes (2026-10-28) ──────────────────────────────────

        private static (string Key, Func<int> Get, Action<int> Set)[] IconSizes => new (string, Func<int>, Action<int>)[]
        {
            ("monsterDebuffIconSize", () => S.MonsterDebuffIconSize, v => S.MonsterDebuffIconSize = v),
            ("selfDebuffIconSize", () => S.SelfDebuffIconSize, v => S.SelfDebuffIconSize = v),
        };

        private static void DebuffIconSizesDefault100()
        {
            Case("defaults: both debuff icon sizes are 100 % (today's icons), inside 50-200; written to the file");
            S.ResetToDefaults();
            Check(S.DefaultDebuffIconSize == 100, "the default is 100");
            Check(S.MinDebuffIconSize == 50 && S.MaxDebuffIconSize == 200, $"range 50-200 (got {S.MinDebuffIconSize}-{S.MaxDebuffIconSize})");
            string[] lines = Written();
            foreach (var s in IconSizes)
            {
                Check(s.Get() == 100, $"{s.Key} defaults to 100 (got {s.Get()})");
                Check(lines.Contains(s.Key + "=100"), $"{s.Key}=100 written");
            }
            Check(S.Describe().Contains("text 1 debuff icons 100%") && S.Describe().EndsWith("debuff icons 100%."),
                "the status line names both");
        }

        private static void DebuffIconSizesOldFileLoads100()
        {
            Case("a file from 2026.10.28.1 (no icon size keys) loads both at 100, with its own values");
            S.ResetToDefaults();
            S.MonsterTextSize = 1.5f; S.SelfTextSize = 0.75f; S.Scale = 1.3f; S.SelfDebuffs = false;
            string[] old = Written().Where(l => !IconSizes.Any(s => l.StartsWith(s.Key + "=", StringComparison.Ordinal))).ToArray();
            Check(old.Length == Written().Length - 2, "the old file lacks exactly the two keys");
            foreach (var s in IconSizes) s.Set(170);
            S.ResetToDefaults();   // the loader starts from the defaults
            S.ApplyLines(old);
            foreach (var s in IconSizes) Check(s.Get() == 100, $"{s.Key} is 100 after loading the old file (got {s.Get()})");
            Check(S.MonsterTextSize == 1.5f && S.SelfTextSize == 0.75f && MathF.Abs(S.Scale - 1.3f) < 0.001f && !S.SelfDebuffs,
                "the old file's own values (text sizes included) still load");

            S.ResetToDefaults();
            S.ApplyLines(new[] { "# RynthCore monster nameplates", "enabled=1", "monsterTextSize=2", "selfTextSize=2" });
            foreach (var s in IconSizes) Check(s.Get() == 100, $"{s.Key} 100 after a hand-written file with big text (text never sets it)");
            S.ResetToDefaults();
        }

        private static void DebuffIconSizesClamp()
        {
            Case("load: icon sizes clamp to 50-200, junk is ignored");
            foreach (var s in IconSizes)
            {
                S.ResetToDefaults();
                S.ApplyLines(new[] { s.Key + "=500" });
                Check(s.Get() == 200, $"{s.Key}=500 capped at 200 (got {s.Get()})");
                S.ApplyLines(new[] { s.Key + "=10" });
                Check(s.Get() == 50, $"{s.Key}=10 floored at 50 (got {s.Get()})");
                S.ApplyLines(new[] { s.Key + "=-40" });
                Check(s.Get() == 50, $"{s.Key}=-40 floored at 50");
                S.ApplyLines(new[] { s.Key + "=200" });
                Check(s.Get() == 200, $"{s.Key}=200 kept (the top of the range)");
                S.ApplyLines(new[] { s.Key + "=50" });
                Check(s.Get() == 50, $"{s.Key}=50 kept (the bottom of the range)");
                s.Set(130);
                S.ApplyLines(new[] { s.Key + "=abc", s.Key + "=", s.Key + "=1.5", s.Key + "=99999999999" });
                Check(s.Get() == 130, $"{s.Key}: junk, empty, a fraction and an overflow leave it alone (got {s.Get()})");
            }
            S.ResetToDefaults();
        }

        private static void DebuffIconSizesRoundTripApart()
        {
            Case("save/load: each icon size comes back on its own, apart from the other and from the text sizes");
            foreach (var s in IconSizes)
            {
                S.ResetToDefaults();
                s.Set(165);
                string[] lines = Written();
                Check(lines.Contains(s.Key + "=165"), $"{s.Key}=165 written");
                S.ResetToDefaults();
                S.ApplyLines(lines);
                Check(s.Get() == 165, $"{s.Key} 165 after reload (got {s.Get()})");
                foreach (var o in IconSizes)
                    if (o.Key != s.Key) Check(o.Get() == 100, $"{o.Key} still 100 when only {s.Key} changed");
                Check(S.MonsterTextSize == 1f && S.SelfTextSize == 1f, $"text sizes untouched by {s.Key}");
            }
            S.ResetToDefaults();
            S.MonsterDebuffIconSize = 60; S.SelfDebuffIconSize = 185; S.MonsterTextSize = 1.75f; S.SelfTextSize = 0.6f;
            string[] first = Written();
            S.ResetToDefaults();
            S.ApplyLines(first);
            Check(S.MonsterDebuffIconSize == 60 && S.SelfDebuffIconSize == 185, "both together");
            Check(MathF.Abs(S.MonsterTextSize - 1.75f) < 0.001f && MathF.Abs(S.SelfTextSize - 0.6f) < 0.001f, "text sizes keep their own values");
            Check(first.SequenceEqual(Written()), "write -> apply -> write is stable");
            Check(S.Describe().Contains("debuff icons 60%") && S.Describe().Contains("debuff icons 185%"), "the status line shows them");
            S.ResetToDefaults();
        }

        private static void DebuffIconSizesReset()
        {
            Case("Reset bar, text and icon sizes and Reset all put both back to 100");
            S.ResetToDefaults();
            foreach (var s in IconSizes) s.Set(150);
            S.Scale = 1.4f;
            int saves = RynthCore.Engine.UI.UiBackgroundWriter.Enqueued;
            S.ResetSizes();
            foreach (var s in IconSizes) Check(s.Get() == 100, $"{s.Key} back to 100 (sizes reset)");
            Check(MathF.Abs(S.Scale - 1.4f) < 0.001f, "Scale kept");
            Check(RynthCore.Engine.UI.UiBackgroundWriter.Enqueued == saves + 1, "one save queued");
            foreach (var s in IconSizes) s.Set(70);
            S.ResetToDefaults();
            foreach (var s in IconSizes) Check(s.Get() == 100, $"{s.Key} back to 100 (reset all)");
            S.SetAll(false);
            S.SetAll(true);
            foreach (var s in IconSizes) Check(s.Get() == 100, $"{s.Key} untouched by all off / on");
            S.ResetToDefaults();
        }

        private static void DebuffIconSizeChatForm()
        {
            Case("chat: /rv plates [self] debuffs size takes a percent or a factor, clamped; junk refused");
            (string Text, int Pct)[] good =
            {
                ("150", 150), ("150%", 150), (" 150 % ", 150), ("1.5", 150), ("1.5x", 150), ("1.25X", 125), ("2", 200),
                ("100", 100), ("1", 100), ("75", 75), ("0.5", 50), ("500", 200), ("10", 50), ("0", 50), ("-3", 50), ("122.6", 123),
            };
            foreach (var g in good)
                Check(S.TryParseDebuffIconSize(g.Text, out int p) && p == g.Pct,
                    $"\"{g.Text}\" -> {g.Pct} (got {(S.TryParseDebuffIconSize(g.Text, out int q) ? q.ToString(CultureInfo.InvariantCulture) : "refused")})");
            foreach (string bad in new[] { null!, "", " ", "abc", "%", "x", "NaN", "Infinity", "1e999" })
                Check(!S.TryParseDebuffIconSize(bad, out _), $"\"{bad}\" refused");
        }

        // The debuff row's lengths as the plates computed them before the setting (2026.10.28.1).
        private static float OldIcon(float k) => MathF.Round(13f * k);
        private static float OldRowHeight(float k) => OldIcon(k) + MathF.Round(3f * k) + 3f;   // MonsterHud.Measure / PlayerPlate.Measure
        private static float OldGap(float k) => MathF.Max(2f, MathF.Round(2f * k));
        private static float OldRowWidth(float k, int slots, bool more, float moreTextW)
        {
            float sz = OldIcon(k), gap = OldGap(k);
            float moreW = more ? moreTextW + 4f * k : 0f;
            return slots * sz + Math.Max(0, slots - 1) * gap + (more ? gap + moreW : 0f);
        }

        /// <summary>Plate scales seen in game: Look > Scale x UI scale x the distance shrink and target pop, and a sweep.</summary>
        private static IEnumerable<float> Ks()
        {
            for (int i = 30; i <= 400; i++) yield return i / 100f;
            float[] scale = { 0.6f, 0.75f, 0.9f, 1f, 1.1f, 1.3f, 1.5f, 2f };
            float[] ui = { 1f, 1.25f, 1.5f, 1.75f, 2f };
            float[] dist = { 1f, 0.97f, 0.85f, 0.72f, 1.12f, 0.72f * 1.12f };
            foreach (float s in scale)
                foreach (float u in ui)
                    foreach (float d in dist)
                    {
                        yield return MathF.Max(0.3f, s * d) * u;   // a monster plate
                        yield return s * u;                         // your plate
                    }
        }

        private static void DebuffRowAt100IsTheOldRow()
        {
            Case("100 %: every length of the row is the old expression exactly, at every plate scale");
            int bad = 0, n = 0;
            foreach (float k in Ks())
            {
                n++;
                var r = new DebuffRowMetrics(k, 100);
                bool ok = r.Scale == 1f && r.K == k && r.Icon == OldIcon(k) && r.Height == OldRowHeight(k)
                          && r.Above == MathF.Round(3f * k) && r.Below == 3f && r.Gap == OldGap(k)
                          && r.Glyph == MathF.Round(OldIcon(k) * 0.8f) && r.LineGap == 1f && r.LineH == MathF.Max(1f, MathF.Round(1.5f * k))
                          && r.Round == 2f * k && r.DigitDx == 1f && r.DigitDy == 2f
                          && r.Height == r.Above + r.Icon + r.Below;
                for (int slots = 0; slots <= 6 && ok; slots++)
                    ok = r.Width(slots, false, 0f) == OldRowWidth(k, slots, false, 0f)
                         && r.Width(slots, true, 11.37f) == OldRowWidth(k, slots, true, 11.37f);
                foreach (float t in new[] { 7f, 9f, 11f, 14f, 18f })
                    ok &= r.TextSize(t) == t;   // the digit and "+N" keep the plate's own text size
                ok &= r.CoverWidth(57f, 300f, 8f * k) == 57f && r.Overhang(300f, 48f) == 0f;   // the box is left as it was
                if (!ok)
                {
                    bad++;
                    if (bad <= 5) Console.WriteLine($"    k={k:R}: icon {r.Icon} vs {OldIcon(k)}, height {r.Height} vs {OldRowHeight(k)}");
                }
            }
            Check(bad == 0, $"{bad} of {n} scales differ from the old row");
            var none = new DebuffRowMetrics(1.3f, 0);
            Check(none.Scale == 1f && none.Height == OldRowHeight(1.3f), "a size of 0 (never set) reads as 100");
            Check(default(DebuffRowMetrics).Height == 0f, "an empty row (name labels) takes no room");
        }

        private static void DebuffRowScalesWithTheSetting()
        {
            Case("the row's height grows with the setting, apart from the text size");
            var r1 = new DebuffRowMetrics(1f, 100);
            var r2 = new DebuffRowMetrics(1f, 200);
            var rh = new DebuffRowMetrics(1f, 50);
            Check(r1.Height == 19f, $"100 % at k 1: 3 + 13 + 3 = 19 (got {r1.Height})");
            Check(r2.Icon == 26f && r2.Height == 38f, $"200 %: a 26 px icon, 38 px row (got {r2.Icon}, {r2.Height})");
            Check(rh.Icon == 6f && rh.Height == 10f, $"50 %: a 6 px icon, 10 px row (got {rh.Icon}, {rh.Height})");
            Check(r2.Glyph == 21f && r2.Gap == 4f && r2.LineH == 3f && r2.LineGap == 2f, "200 %: glyph, gap and countdown line scale");
            Check(r2.TextSize(9f) == 18f && rh.TextSize(10f) == 5f && r2.TextSize(11f) == 22f, "200 % / 50 %: the digit and \"+N\" text scale");
            Check(r2.DigitDx == 2f && r2.DigitDy == 4f, "200 %: the digit's corner offset scales");
            Check(r2.LineGap + r2.LineH <= r2.Below + 1f, "200 %: the countdown line fits its room");

            foreach (float k in new[] { 0.6f, 1f, 1.25f, 1.5f, 2f, 2.6f })
            {
                float prevH = 0f, prevIcon = 0f;
                bool mono = true;
                for (int pct = 50; pct <= 200; pct += 5)
                {
                    var r = new DebuffRowMetrics(k, pct);
                    mono &= r.Height >= prevH && r.Icon >= prevIcon && r.Height == r.Above + r.Icon + r.Below;
                    mono &= MathF.Abs(r.Icon - 13f * k * pct / 100f) <= 0.5f;   // the icon follows k x the setting
                    prevH = r.Height; prevIcon = r.Icon;
                }
                Check(mono, $"k {k}: height and icon never shrink as the setting grows; the icon is 13 x k x %");
                Check(new DebuffRowMetrics(k, 200).Height > new DebuffRowMetrics(k, 100).Height &&
                      new DebuffRowMetrics(k, 100).Height > new DebuffRowMetrics(k, 50).Height, $"k {k}: 50 % < 100 % < 200 %");
            }
        }

        private static void DebuffRowWidthAndCover()
        {
            Case("an enlarged row wider than the bar grows the plate box; a smaller or default one never does");
            const float k = 1f;
            var r = new DebuffRowMetrics(k, 200);
            float six = r.Width(6, false, 0f);   // five icons and the "debuffed by others" marker
            Check(six == 6 * 26f + 5 * 4f, $"six 26 px squares, five 4 px gaps (got {six})");
            Check(r.Width(5, true, 12f) == 5 * 26f + 4 * 4f + 4f + 12f + 4f * 2f, "with \"+N\": a gap, the text and its room");
            float box = 120f;   // a monster plate: bar 112 + 8
            Check(r.CoverWidth(box, six, 8f * k) == six + 8f, $"the box holds the row (got {r.CoverWidth(box, six, 8f)})");
            Check(r.CoverWidth(500f, six, 8f) == 500f, "a box already wider stays");
            Check(r.Overhang(six, 96f) == MathF.Ceiling((six - 96f) / 2f), "past your 96 px bars: half the difference each side");
            Check(r.Overhang(50f, 96f) == 0f, "a row narrower than the bars reaches past nothing");
            var small = new DebuffRowMetrics(k, 75);
            Check(small.CoverWidth(box, 999f, 8f) == box && small.Overhang(999f, 10f) == 0f, "75 %: the box is left as it was");
        }

        // ── harness ─────────────────────────────────────────────────────────

        // ── refresh others' health (HealthProbe) ────────────────────────────

        private static void HealthRefreshDefaultsOn()
        {
            Case("refresh others' health: on at 1 s by default, and after loading a file from before it");
            S.ResetToDefaults();
            Check(S.RefreshOthersHealth, "on by default");
            Check(S.HealthRefreshSec == HealthProbe.DefaultIntervalSec && HealthProbe.DefaultIntervalSec == 1f, "1 s by default");
            Check(HealthProbe.MinIntervalSec <= 1f && 1f <= HealthProbe.MaxIntervalSec, "the range holds the default");
            string[] old = Written().Where(l => !l.StartsWith("refreshOthersHealth=", StringComparison.Ordinal) &&
                                                !l.StartsWith("healthRefreshSec=", StringComparison.Ordinal)).ToArray();
            Check(old.Length == Written().Length - 2, "the old file lacks exactly the two keys");
            S.RefreshOthersHealth = false; S.HealthRefreshSec = 4f;
            S.ResetToDefaults();
            S.ApplyLines(old);
            Check(S.RefreshOthersHealth && S.HealthRefreshSec == 1f, "on at 1 s after loading the old file");
            Check(S.Describe().Contains("refresh others' health every 1.0 s"), "status line: " + S.Describe());
        }

        private static void HealthRefreshRoundTripAndClamp()
        {
            Case("refresh others' health: off and an interval round-trip, the interval clamps, reset restores");
            S.ResetToDefaults();
            S.RefreshOthersHealth = false; S.HealthRefreshSec = 2.5f;
            string[] lines = Written();
            Check(lines.Contains("refreshOthersHealth=0") && lines.Contains("healthRefreshSec=2.5"), "refreshOthersHealth=0, healthRefreshSec=2.5 written");
            S.ResetToDefaults();
            S.ApplyLines(lines);
            Check(!S.RefreshOthersHealth && S.HealthRefreshSec == 2.5f, "off, 2.5 s after reload");
            Check(lines.SequenceEqual(Written()), "identical text after a reload");
            Check(S.Describe().Contains("refresh others' health off"), "status line says off");
            S.ApplyLines(new[] { "healthRefreshSec=0.01" });
            Check(S.HealthRefreshSec == HealthProbe.MinIntervalSec, $"0.01 floored at {HealthProbe.MinIntervalSec}");
            S.ApplyLines(new[] { "healthRefreshSec=60" });
            Check(S.HealthRefreshSec == HealthProbe.MaxIntervalSec, $"60 capped at {HealthProbe.MaxIntervalSec}");
            S.ApplyLines(new[] { "healthRefreshSec=abc", "healthRefreshSec=" });
            Check(S.HealthRefreshSec == HealthProbe.MaxIntervalSec, "junk leaves it alone");
            S.ResetToDefaults();
            Check(S.RefreshOthersHealth && S.HealthRefreshSec == 1f, "reset: on at 1 s");
            S.ResetToDefaults();
        }

        private static void Case(string name)
        {
            _case = name;
            Console.WriteLine("- " + name);
        }

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (ok) return;
            _failed++;
            Console.WriteLine($"  FAIL [{_case}] {what}");
        }
    }
}
