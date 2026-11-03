using System.Globalization;
using RynthCore.Engine.ImGuiBackend.Hud;

namespace RynthCore.Engine.UI
{
    /// <summary>Stub: counts saves, never runs them (no file I/O in the tests).</summary>
    internal static class UiBackgroundWriter
    {
        public static int Enqueued;
        public static void Enqueue(string label, Action work) => Enqueued++;
    }
}

namespace SelfPlateTests
{
    using S = MonsterHudSettings;

    /// <summary>Your own plate's look switches, the lock from Follow mode, the "Debuffs on you" selection, and its icon size.</summary>
    internal static class Program
    {
        private static int _checks, _failed;
        private static string _case = "";

        private static int Main()
        {
            LookDefaultsOn();
            OldFileLoadsThemOn();
            EachToggleRoundTrips();
            ResetTurnsThemBackOn();
            UnlockFromFixed();
            UnlockFromFollowFixesIt();
            LockFromFollowStaysFollow();

            HarmfulOnly();
            CooldownsAndZeroIdsDropped();
            ExpiredAndEndlessDropped();
            NoServerTimeOrNoSnapshot();
            SameCategoryMerges();
            KindMergesCategories();
            KindOrder();
            OverflowCount();
            TicksAndDuration();
            CategoryZeroStandsAlone();
            ShortArraysAndHugeExpiry();

            SelfIconSizeSetting();
            SelfPlateAt100IsTheOldPlate();
            MeasuredHeightIncludesTheScaledRow();
            ClampKeepsTheRowOnScreen();

            NoDebuffsNoLoss();
            AttributeDebuffs();
            VitalDebuffs();
            MixedBuffsAndDebuffs();
            StrongestOfACategory();
            SkippedEntries();

            Console.WriteLine();
            Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
            Console.WriteLine(_failed == 0 ? "ALL SELF PLATE TESTS PASSED." : $"{_failed} FAILED.");
            return _failed == 0 ? 0 : 1;
        }

        // ── settings ─────────────────────────────────────────────────────────

        private static (string Key, Func<bool> Get, Action<bool> Set)[] Toggles => new (string, Func<bool>, Action<bool>)[]
        {
            ("selfBarTrack", () => S.SelfBarTrack, v => S.SelfBarTrack = v),
            ("selfBarChip", () => S.SelfBarChip, v => S.SelfBarChip = v),
            ("selfTextOutline", () => S.SelfTextOutline, v => S.SelfTextOutline = v),
            ("selfNumberBand", () => S.SelfNumberBand, v => S.SelfNumberBand = v),
            ("selfDebuffs", () => S.SelfDebuffs, v => S.SelfDebuffs = v),
            ("selfDebuffLoss", () => S.SelfDebuffLoss, v => S.SelfDebuffLoss = v),
        };

        private static string[] Written()
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            S.WriteAll(sw);
            return sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        }

        private static void LookDefaultsOn()
        {
            Case("defaults: the four look switches and Debuffs on you are on (the look before they existed)");
            foreach (var t in Toggles) Check(t.Get(), $"{t.Key} defaults on");
            string[] lines = Written();
            foreach (var t in Toggles) Check(lines.Contains(t.Key + "=1"), $"{t.Key}=1 written");
        }

        private static void OldFileLoadsThemOn()
        {
            Case("a file from 2026.10.27.1 (no new keys) loads them on, and its other values");
            // That release's file = today's lines without the five new keys.
            S.ResetToDefaults();
            S.SelfNumbers = true; S.Scale = 1.4f; S.SelfPlacement = SelfPlacement.Follow; S.SelfLocked = false;
            string[] old = Written().Where(l => !Toggles.Any(t => l.StartsWith(t.Key + "=", StringComparison.Ordinal))).ToArray();
            Check(old.Length == Written().Length - Toggles.Length, "the old file lacks exactly the new keys");
            S.ResetToDefaults();
            S.ApplyLines(old);
            foreach (var t in Toggles) Check(t.Get(), $"{t.Key} on after loading the old file");
            Check(S.SelfNumbers && MathF.Abs(S.Scale - 1.4f) < 0.001f && S.SelfPlacement == SelfPlacement.Follow && !S.SelfLocked,
                "the old file's own values still load");

            S.ResetToDefaults();
            S.ApplyLines(new[] { "# RynthCore monster nameplates", "selfPlate=1", "selfNumbers=1" });
            foreach (var t in Toggles) Check(t.Get(), $"{t.Key} on after a hand-written two-line file");
            S.ResetToDefaults();
        }

        private static void EachToggleRoundTrips()
        {
            Case("each switch saves off and loads off, alone, and back on");
            var toggles = Toggles;
            foreach (var t in toggles)
            {
                S.ResetToDefaults();
                t.Set(false);
                string[] lines = Written();
                Check(lines.Contains(t.Key + "=0"), $"{t.Key}=0 written");
                S.ResetToDefaults();
                S.ApplyLines(lines);
                Check(!t.Get(), $"{t.Key} off after reload");
                foreach (var o in toggles)
                    if (o.Key != t.Key) Check(o.Get(), $"{o.Key} still on when only {t.Key} is off");
                Check(S.ShowDebuffs && S.Enabled, $"monster plates' switches untouched by {t.Key}");
                t.Set(true);
                S.ApplyLines(Written());
                Check(t.Get(), $"{t.Key} back on");
            }
            Case("all off together round-trips; write -> apply -> write is stable");
            foreach (var t in toggles) t.Set(false);
            string[] first = Written();
            S.ResetToDefaults();
            S.ApplyLines(first);
            foreach (var t in toggles) Check(!t.Get(), $"{t.Key} off");
            Check(first.SequenceEqual(Written()), "identical text after a reload");
            Check(S.Describe().Contains("debuffs on you off") && S.Describe().Contains("text outline off"), "status line names them");
            S.ResetToDefaults();
        }

        private static void ResetTurnsThemBackOn()
        {
            Case("Reset all to defaults turns them back on");
            foreach (var t in Toggles) t.Set(false);
            S.ResetToDefaults();
            foreach (var t in Toggles) Check(t.Get(), $"{t.Key} on after reset");
        }

        private static void UnlockFromFixed()
        {
            Case("unlock a fixed plate: stays fixed, unlocked, movable; nothing saved by the setter");
            S.ResetToDefaults();
            int saves = RynthCore.Engine.UI.UiBackgroundWriter.Enqueued;
            Check(!S.SelfMovable, "locked by default");
            bool switched = S.SetSelfLocked(false);
            Check(!switched, "no placement switch");
            Check(S.SelfPlacement == SelfPlacement.Fixed && !S.SelfLocked && S.SelfMovable, "fixed, unlocked, movable");
            S.SetSelfLocked(true);
            Check(S.SelfLocked && !S.SelfMovable, "locked again");
            Check(RynthCore.Engine.UI.UiBackgroundWriter.Enqueued == saves, "the setter queues no save (callers save)");
        }

        private static void UnlockFromFollowFixesIt()
        {
            Case("unlock a plate that follows you: fixed on screen at its saved spot, then movable");
            S.ResetToDefaults();
            S.SelfPlacement = SelfPlacement.Follow;
            S.SelfFixedX = 0.3f; S.SelfFixedY = 0.2f;
            Check(!S.SelfMovable, "a following plate isn't movable");
            bool switched = S.SetSelfLocked(false);
            Check(switched, "reports the switch to fixed");
            Check(S.SelfPlacement == SelfPlacement.Fixed && !S.SelfLocked && S.SelfMovable, "fixed and unlocked");
            Check(S.SelfFixedX == 0.3f && S.SelfFixedY == 0.2f, "the saved spot is kept");
            S.ResetToDefaults();
        }

        private static void LockFromFollowStaysFollow()
        {
            Case("lock while following: stays following");
            S.ResetToDefaults();
            S.SelfPlacement = SelfPlacement.Follow;
            S.SelfLocked = false;
            bool switched = S.SetSelfLocked(true);
            Check(!switched && S.SelfPlacement == SelfPlacement.Follow && S.SelfLocked, "still follows, locked");
            S.ResetToDefaults();
        }

        // ── Debuffs on you: selection ───────────────────────────────────────

        private const long Freq = 1000;          // ticks per second in these checks
        private const long Now = 5_000_000;      // Stopwatch ticks
        private const double ServerNow = 100_000;

        // Fake spell table: harmful spells only (as PortalSpellTable keeps), with a kind and a base length.
        private static readonly Dictionary<uint, (DebuffKind Kind, float Dur)> Harmful = new()
        {
            [1] = (DebuffKind.Imperil, 60f),
            [2] = (DebuffKind.VulnFire, 45f),
            [3] = (DebuffKind.Attributes, 120f),
            [4] = (DebuffKind.Attributes, 90f),
            [5] = (DebuffKind.MagicYield, 30f),
            [6] = (DebuffKind.Skills, 240f),
            [7] = (DebuffKind.Dot, 20f),
            [8] = (DebuffKind.Movement, 50f),
            [9] = (DebuffKind.VulnCold, 45f),
            [10] = (DebuffKind.Imperil, 75f),
            [0x8001] = (DebuffKind.Other, 10f),   // would be harmful, but ids from 0x8000 are cooldowns
        };

        private static bool Lookup(uint id, out DebuffKind kind, out float dur)
        {
            if (Harmful.TryGetValue(id, out var h)) { kind = h.Kind; dur = h.Dur; return true; }
            kind = DebuffKind.Other; dur = 0;
            return false;
        }

        /// <summary>One snapshot entry: spell id, seconds left (server time), spell category.</summary>
        private readonly record struct E(uint Id, double Left, uint Cat);

        private static int Run(E[] entries, out DebuffIcon[] icons, int dstLen = 5, double serverNow = ServerNow, int? n = null)
        {
            var ids = entries.Select(e => e.Id).ToArray();
            var exp = entries.Select(e => e.Left >= SelfDebuffs.NoEnd ? double.MaxValue : serverNow + e.Left).ToArray();
            var cats = entries.Select(e => e.Cat).ToArray();
            icons = new DebuffIcon[dstLen];
            return SelfDebuffs.Select(ids, exp, cats, n ?? entries.Length, serverNow, Now, Freq, Lookup, icons);
        }

        private static void HarmfulOnly()
        {
            Case("harmful only: buffs (not in the table) never show");
            int c = Run(new[] { new E(100, 600, 50), new E(1, 30, 116), new E(200, 900, 51) }, out var ic);
            Check(c == 1, $"one icon (got {c})");
            Check(ic[0].Kind == DebuffKind.Imperil && ic[0].Stack == 1, "the Imperil");
            c = Run(new[] { new E(100, 600, 50), new E(200, 900, 51) }, out _);
            Check(c == 0, "buffs alone: no row");
        }

        private static void CooldownsAndZeroIdsDropped()
        {
            Case("cooldowns (0x8000+) and id 0 are dropped");
            int c = Run(new[] { new E(0x8001, 30, 0), new E(0, 30, 116), new E(2, 30, 110) }, out var ic);
            Check(c == 1 && ic[0].Kind == DebuffKind.VulnFire, $"only the fire vuln (got {c})");
        }

        private static void ExpiredAndEndlessDropped()
        {
            Case("expired entries and ones that never end (vitae, item spells) are dropped");
            int c = Run(new[] { new E(1, -1, 116), new E(2, 0, 110), new E(3, 1e13, 2), new E(5, 10, 42) }, out var ic);
            Check(c == 1 && ic[0].Kind == DebuffKind.MagicYield, $"only the live Magic Yield (got {c})");
            var ids = new uint[] { 1 };
            var exp = new[] { double.NaN };
            var cats = new uint[] { 116 };
            Check(SelfDebuffs.Select(ids, exp, cats, 1, ServerNow, Now, Freq, Lookup, new DebuffIcon[5]) == 0, "NaN expiry dropped");
        }

        private static void NoServerTimeOrNoSnapshot()
        {
            Case("no server time yet, or no snapshot (-1): no row");
            Check(Run(new[] { new E(1, 30, 116) }, out _, serverNow: 0) == 0, "server time 0");
            Check(Run(new[] { new E(1, 30, 116) }, out _, n: -1) == 0, "count -1 (CopyPlayerStatMods: nothing valid)");
            Check(Run(Array.Empty<E>(), out _) == 0, "empty snapshot");
        }

        private static void SameCategoryMerges()
        {
            Case("one per category: two Imperils in category 116 are one icon, lasting to the later end");
            int c = Run(new[] { new E(1, 20, 116), new E(10, 50, 116) }, out var ic);
            Check(c == 1, $"one icon (got {c})");
            Check(ic[0].Stack == 1, "a stack of one (one category)");
            Check(ic[0].Expiry == Now + 50 * Freq, $"ends with the later one (got {ic[0].Expiry - Now})");
            Check(ic[0].Duration == 75f, "that one's length");
            c = Run(new[] { new E(10, 50, 116), new E(1, 20, 116) }, out ic);
            Check(c == 1 && ic[0].Expiry == Now + 50 * Freq && ic[0].Duration == 75f, "same result in the other order");
        }

        private static void KindMergesCategories()
        {
            Case("one per kind: two attribute debuffs (categories 2 and 4) are one icon, stack 2, soonest end");
            int c = Run(new[] { new E(3, 80, 2), new E(4, 25, 4) }, out var ic);
            Check(c == 1 && ic[0].Kind == DebuffKind.Attributes, $"one Attributes icon (got {c})");
            Check(ic[0].Stack == 2, $"stack 2 (got {ic[0].Stack})");
            Check(ic[0].Expiry == Now + 25 * Freq && ic[0].Duration == 90f, "the soonest end and its length");
        }

        private static void KindOrder()
        {
            Case("icons in kind order (the monster row's), whatever the snapshot order");
            int c = Run(new[] { new E(6, 100, 20), new E(1, 30, 116), new E(9, 30, 106), new E(2, 30, 110), new E(5, 30, 42) }, out var ic);
            Check(c == 5, $"five icons (got {c})");
            var want = new[] { DebuffKind.VulnFire, DebuffKind.VulnCold, DebuffKind.Imperil, DebuffKind.MagicYield, DebuffKind.Skills };
            Check(ic.Select(i => i.Kind).SequenceEqual(want), "fire, cold, Imperil, Magic Yield, skills: " + string.Join(", ", ic.Select(i => i.Kind)));
        }

        private static void OverflowCount()
        {
            Case("more kinds than fit: the count includes the rest (+N), the first by kind are kept");
            var all = new[]
            {
                new E(8, 30, 78), new E(7, 10, 600), new E(6, 100, 20), new E(1, 30, 116),
                new E(9, 30, 106), new E(2, 30, 110), new E(5, 30, 42),
            };
            int c = Run(all, out var ic);
            Check(c == 7, $"count 7 (got {c})");
            var want = new[] { DebuffKind.VulnFire, DebuffKind.VulnCold, DebuffKind.Imperil, DebuffKind.MagicYield, DebuffKind.Movement };
            Check(ic.Select(i => i.Kind).SequenceEqual(want), "the five first kinds: " + string.Join(", ", ic.Select(i => i.Kind)));
        }

        private static void TicksAndDuration()
        {
            Case("server seconds left -> Stopwatch ticks from now; length from the table");
            Run(new[] { new E(5, 12.5, 42) }, out var ic);
            Check(ic[0].Expiry == Now + 12_500, $"12.5 s -> 12500 ticks (got {ic[0].Expiry - Now})");
            Check(ic[0].Duration == 30f, "Magic Yield's 30 s");
        }

        private static void CategoryZeroStandsAlone()
        {
            Case("no category recorded: each spell stands alone");
            int c = Run(new[] { new E(3, 40, 0), new E(4, 60, 0) }, out var ic);
            Check(c == 1 && ic[0].Stack == 2, $"two spells, one Attributes icon, stack 2 (got {c}, {ic[0].Stack})");
            c = Run(new[] { new E(3, 40, 0), new E(3, 60, 0) }, out ic);
            Check(c == 1 && ic[0].Stack == 1 && ic[0].Expiry == Now + 60 * Freq, "the same spell twice is one entry");
        }

        private static void ShortArraysAndHugeExpiry()
        {
            Case("a count past the arrays is clamped; a huge (but finite) end doesn't overflow");
            var ids = new uint[] { 1, 2 };
            var exp = new[] { ServerNow + 30, ServerNow + 9e11 };
            var cats = new uint[] { 116, 110 };
            var dst = new DebuffIcon[5];
            int c = SelfDebuffs.Select(ids, exp, cats, 999, ServerNow, Now, Freq, Lookup, dst);
            Check(c == 2, $"two icons (got {c})");
            Check(dst.Take(2).All(i => i.Expiry > Now), "both end after now");
            c = SelfDebuffs.Select(ids, exp, cats, 2, ServerNow, Now, Freq, Lookup, Array.Empty<DebuffIcon>());
            Check(c == 2, "an empty destination still counts");
        }

        // ── Your debuff icon size (2026-10-28) ──────────────────────────────

        private static void SelfIconSizeSetting()
        {
            Case("Your debuff icon size: 100 by default and from an older file, round-trips apart from your text size");
            S.ResetToDefaults();
            Check(S.SelfDebuffIconSize == 100, $"defaults to 100 (got {S.SelfDebuffIconSize})");
            Check(Written().Contains("selfDebuffIconSize=100"), "selfDebuffIconSize=100 written");
            S.SelfTextSize = 1.8f; S.SelfNumbers = true;
            string[] old = Written().Where(l => !l.StartsWith("selfDebuffIconSize=", StringComparison.Ordinal) &&
                                                !l.StartsWith("monsterDebuffIconSize=", StringComparison.Ordinal)).ToArray();
            S.SelfDebuffIconSize = 180;
            S.ResetToDefaults();
            S.ApplyLines(old);
            Check(S.SelfDebuffIconSize == 100, "100 after loading a 2026.10.28.1 file (no key)");
            Check(MathF.Abs(S.SelfTextSize - 1.8f) < 0.001f && S.SelfNumbers, "that file's text size still loads (and leaves the icons alone)");
            S.SelfDebuffIconSize = 140;
            string[] lines = Written();
            S.ResetToDefaults();
            S.ApplyLines(lines);
            Check(S.SelfDebuffIconSize == 140 && S.MonsterDebuffIconSize == 100, "140 comes back; the monster plates' stays 100");
            S.ApplyLines(new[] { "selfDebuffIconSize=1000" });
            Check(S.SelfDebuffIconSize == S.MaxDebuffIconSize, "clamped to the top");
            S.ApplyLines(new[] { "selfDebuffIconSize=1" });
            Check(S.SelfDebuffIconSize == S.MinDebuffIconSize, "clamped to the bottom");
            S.ResetToDefaults();
        }

        // Your plate's lengths as PlayerPlate.Measure computed them before the setting (2026.10.28.1).
        private readonly record struct OldPlate(float BarW, float BarH, float Gap, float Round, float BlockH, float NameH, float IconsH)
        {
            public float Height => NameH + BlockH + IconsH;
        }

        private static OldPlate Old(float k, bool numbers, float bw, float bh, float nameSize, bool icons)
        {
            float barW = MathF.Round((numbers ? 124f : 96f) * k * bw);
            float barH = MathF.Round((numbers ? 15f : 5f) * k * bh);
            float gap = MathF.Max(2f, MathF.Round(2f * k));
            float round = MathF.Max(1f, 1.5f * k);
            float blockH = 3 * barH + 2 * gap;
            float nameH = nameSize > 0f ? nameSize + 3f * k : 0f;
            float iconSize = MathF.Round(13f * k);
            float iconsH = icons ? MathF.Round(3f * k) + iconSize + 3f : 0f;
            return new OldPlate(barW, barH, gap, round, blockH, nameH, iconsH);
        }

        private static IEnumerable<(float K, bool Numbers, float Bw, float Bh, float Name, bool Icons)> Plates()
        {
            float[] ks = { 0.6f, 0.75f, 1f, 1.1f, 1.25f, 1.3f * 1.25f, 1.5f, 1.75f, 2f, 2f * 1.5f, 0.6f * 1.75f };
            float[] bws = { 0.5f, 0.85f, 1f, 1.37f, 2.5f };
            float[] bhs = { 0.5f, 1f, 1.6f, 3f };
            float[] names = { 0f, 6f, 11f, 13f, 22f };
            foreach (float k in ks)
                foreach (bool numbers in new[] { false, true })
                    foreach (float bw in bws)
                        foreach (float bh in bhs)
                            foreach (float name in names)
                                foreach (bool icons in new[] { false, true })
                                    yield return (k, numbers, bw, bh, name, icons);
        }

        private static void SelfPlateAt100IsTheOldPlate()
        {
            Case("100 %: your plate's bars, name room, debuff row, measured height and clamp are the old ones exactly");
            int bad = 0, n = 0;
            float[] xs = { -500f, 0f, 3f, 4f, 37.5f, 900f, 1800f, 1890f, 5000f };
            foreach (var c in Plates())
            {
                n++;
                SelfPlateMetrics m = SelfPlateMetrics.Measure(c.K, c.Numbers, c.Bw, c.Bh, c.Name, c.Icons, 100);
                OldPlate o = Old(c.K, c.Numbers, c.Bw, c.Bh, c.Name, c.Icons);
                bool ok = m.K == c.K && m.BarW == o.BarW && m.BarH == o.BarH && m.Gap == o.Gap && m.Round == o.Round &&
                          m.BlockH == o.BlockH && m.NameH == o.NameH && m.IconsH == o.IconsH && m.Height == o.Height &&
                          m.IconsOver == 0f && m.RowTop == o.BlockH + MathF.Round(3f * c.K);
                foreach (float x in xs)
                {
                    // The fixed plate's and Keep on screen's clamp before the setting.
                    float oldLeft = Math.Clamp(x, 4f, MathF.Max(4f, 1920f - o.BarW - 4f));
                    float oldTop = Math.Clamp(x, 4f + o.NameH, MathF.Max(4f + o.NameH, 1080f - o.BlockH - o.IconsH - 4f));
                    ok &= m.ClampLeft(x, 1920f) == oldLeft && m.ClampTop(x, 1080f) == oldTop;
                }
                // Even a row wider than narrow bars reaches past nothing at 100 % (as before).
                ok &= m.Row.Overhang(m.Row.Width(5, true, 14f), m.BarW) == 0f;
                if (!ok)
                {
                    bad++;
                    if (bad <= 5) Console.WriteLine($"    k={c.K:R} numbers={c.Numbers} bw={c.Bw} bh={c.Bh} name={c.Name} icons={c.Icons}: " +
                                                    $"height {m.Height} vs {o.Height}, icons {m.IconsH} vs {o.IconsH}");
                }
            }
            Check(bad == 0, $"{bad} of {n} plates differ from the old plate");
        }

        private static void MeasuredHeightIncludesTheScaledRow()
        {
            Case("the measured height is name + bars + the debuff row at its own size; no icon, no row");
            foreach (float k in new[] { 0.75f, 1f, 1.5f, 2f })
                foreach (bool numbers in new[] { false, true })
                {
                    float prev = 0f;
                    bool ok = true;
                    for (int pct = 50; pct <= 200; pct += 10)
                    {
                        SelfPlateMetrics m = SelfPlateMetrics.Measure(k, numbers, 1f, 1f, 11f, icons: true, pct);
                        SelfPlateMetrics none = SelfPlateMetrics.Measure(k, numbers, 1f, 1f, 11f, icons: false, pct);
                        var row = new DebuffRowMetrics(k, pct);
                        ok &= m.IconsH == row.Height && m.Row.Height == row.Height;
                        ok &= m.Height == m.NameH + m.BlockH + row.Height;
                        ok &= none.IconsH == 0f && none.Height == none.NameH + none.BlockH;
                        ok &= m.BlockH == none.BlockH && m.NameH == none.NameH && m.BarW == none.BarW;   // only the row changes
                        ok &= m.RowTop == m.BlockH + row.Above;
                        ok &= m.Height >= prev;
                        prev = m.Height;
                    }
                    Check(ok, $"k {k}, numbers {numbers}: 50-200 % - the row is in the height, grows with the setting, nothing else moves");
                }
            SelfPlateMetrics h100 = SelfPlateMetrics.Measure(1f, false, 1f, 1f, 0f, true, 100);
            SelfPlateMetrics h200 = SelfPlateMetrics.Measure(1f, false, 1f, 1f, 0f, true, 200);
            SelfPlateMetrics h50 = SelfPlateMetrics.Measure(1f, false, 1f, 1f, 0f, true, 50);
            Check(h100.BlockH == 19f && h100.IconsH == 19f && h100.Height == 38f, $"k 1, 100 %: bars 19 + row 19 (got {h100.BlockH} + {h100.IconsH})");
            Check(h200.IconsH == 38f && h200.Height == 57f, $"k 1, 200 %: the row doubles to 38 (got {h200.IconsH})");
            Check(h50.IconsH == 10f && h50.Height == 29f, $"k 1, 50 %: a 10 px row (got {h50.IconsH})");
            // Your text size never sizes the row: only the name's room follows it.
            SelfPlateMetrics bigText = SelfPlateMetrics.Measure(1f, false, 1f, 1f, 22f, true, 100);
            Check(bigText.IconsH == h100.IconsH && bigText.Row.Icon == h100.Row.Icon, "a bigger name (text size) leaves the row as it is");
        }

        private static void ClampKeepsTheRowOnScreen()
        {
            Case("fixed / keep on screen: the whole plate, an enlarged row included, stays inside the screen");
            const float w = 1280f, h = 720f;
            bool ok = true;
            foreach (int pct in new[] { 50, 75, 100, 125, 150, 200 })
                foreach (float k in new[] { 1f, 1.5f, 2f })
                    foreach (bool name in new[] { false, true })
                    {
                        SelfPlateMetrics m = SelfPlateMetrics.Measure(k, false, 0.5f, 1f, name ? 11f : 0f, true, pct);
                        // Five icons and "+N" under bars half the default width: wider than the bars once enlarged.
                        float rowW = m.Row.Width(5, true, 12f);
                        m.IconsOver = m.Row.Overhang(rowW, m.BarW);
                        if (pct > 100) ok &= m.IconsOver > 0f && m.IconsOver * 2f + m.BarW >= rowW;
                        else ok &= m.IconsOver == 0f;
                        foreach (float x in new[] { -9999f, 0f, 400f, 9999f })
                        {
                            float left = m.ClampLeft(x, w), top = m.ClampTop(x, h);
                            ok &= top - m.NameH >= 4f && top + m.BlockH + m.IconsH <= h - 4f;          // name above, row under
                            ok &= left - m.IconsOver >= 4f && left + m.BarW + m.IconsOver <= w - 4f;  // the row's ends
                        }
                    }
            Check(ok, "every size: the name, the bars and the debuff row (and its ends) inside a 1280x720 screen");
            SelfPlateMetrics big = SelfPlateMetrics.Measure(1f, false, 1f, 1f, 0f, true, 200);
            SelfPlateMetrics def = SelfPlateMetrics.Measure(1f, false, 1f, 1f, 0f, true, 100);
            Check(big.ClampTop(9999f, h) == h - 4f - big.BlockH - big.IconsH && big.ClampTop(9999f, h) < def.ClampTop(9999f, h),
                "at the bottom edge a bigger row lifts the bars by exactly its extra height");
        }

        // ── "Grey out what debuffs take" (VitalLoss) ───────────────────────

        private const uint AttrAdd = VitalLoss.ModAttribute | VitalLoss.ModAdditive;
        private const uint VitalAdd = VitalLoss.ModSecondAttribute | VitalLoss.ModAdditive;
        private const uint VitalMult = VitalLoss.ModSecondAttribute | VitalLoss.ModMultiplicative;

        /// <summary>The three vitals' totals for (spell id, type, key, value, category) entries.</summary>
        private static VitalMods[] Mods(params (uint Id, uint Type, uint Key, float Val, uint Cat)[] e)
        {
            var mods = new VitalMods[3];
            VitalLoss.Collect(e.Select(x => x.Id).ToArray(), e.Select(x => x.Type).ToArray(), e.Select(x => x.Key).ToArray(),
                e.Select(x => x.Val).ToArray(), e.Select(x => x.Cat).ToArray(), e.Length, mods);
            return mods;
        }

        private static void NoDebuffsNoLoss()
        {
            Case("no enchantments, or buffs only: the full maximum is the shown one");
            VitalMods[] none = Mods();
            Check(VitalLoss.FullMax(400, none[0]) == 400 && VitalLoss.FullMax(300, none[1]) == 300 && VitalLoss.FullMax(250, none[2]) == 250,
                "nothing on you: the shown maximums");
            VitalMods[] buffs = Mods((1, AttrAdd, VitalLoss.Endurance, 50f, 10), (2, AttrAdd, VitalLoss.Self, 40f, 11),
                (3, VitalMult, 1, 1.1f, 12), (4, VitalAdd, 3, 20f, 13));
            Check(!buffs[0].AnyDebuff && !buffs[1].AnyDebuff && !buffs[2].AnyDebuff, "buffs aren't debuffs");
            Check(VitalLoss.FullMax(480, buffs[0]) == 480 && VitalLoss.FullMax(370, buffs[1]) == 370 && VitalLoss.FullMax(290, buffs[2]) == 290,
                "buffed maximums stay as shown");
            Check(VitalLoss.FullMax(0, Mods((1, AttrAdd, VitalLoss.Endurance, -50f, 10))[0]) == 0, "a maximum of 0 (not read yet) stays 0");
        }

        private static void AttributeDebuffs()
        {
            Case("Endurance lowered: health gets back half, stamina all; Self lowered: mana");
            VitalMods[] end = Mods((7, AttrAdd, VitalLoss.Endurance, -50f, 20));
            Check(VitalLoss.FullMax(400, end[0]) == 425, "health 400 with Endurance -50 is 425 in full");
            Check(VitalLoss.FullMax(300, end[1]) == 350, "stamina 300 with Endurance -50 is 350 in full");
            Check(VitalLoss.FullMax(250, end[2]) == 250 && !end[2].AnyDebuff, "mana untouched by Endurance");
            VitalMods[] self = Mods((8, AttrAdd, VitalLoss.Self, -40f, 21));
            Check(VitalLoss.FullMax(250, self[2]) == 290 && !self[0].AnyDebuff && !self[1].AnyDebuff, "Self -40: mana 250 is 290, the others untouched");
            VitalMods[] str = Mods((9, AttrAdd, 1, -40f, 22), (10, AttrAdd, 3, -40f, 23));
            Check(!str[0].AnyDebuff && !str[1].AnyDebuff && !str[2].AnyDebuff, "Strength and Quickness debuffs take no vital");
        }

        private static void VitalDebuffs()
        {
            Case("a spell on the vital itself: a multiplier below 1 or an additive below 0");
            Check(VitalLoss.FullMax(320, Mods((11, VitalMult, 1, 0.8f, 30))[0]) == 400, "health x0.8: 320 is 400 in full");
            Check(VitalLoss.FullMax(270, Mods((12, VitalAdd, 3, -30f, 31))[1]) == 300, "stamina -30: 270 is 300 in full");
            Check(VitalLoss.FullMax(200, Mods((13, VitalMult, 5, 0.5f, 32))[2]) == 400, "mana x0.5: 200 is 400 in full");
            Check(VitalLoss.FullMax(320, Mods((14, VitalMult, 2, 0.8f, 33))[0]) == 400, "the current-health key counts for health too");
            Check(VitalLoss.FullMax(100, Mods((15, VitalMult, 1, 0.001f, 34))[0]) <= 1000, "an absurd multiplier is held to 10x");
        }

        private static void MixedBuffsAndDebuffs()
        {
            Case("buffs and debuffs together: the buffs stay, only the debuffs come off");
            // Attribute part 400 (Endurance -50 took 25 of it), x0.75 debuff, +20 buff: AC shows (375 x 0.75) + 20 = 301.
            VitalMods[] m = Mods((16, AttrAdd, VitalLoss.Endurance, -50f, 40), (17, VitalMult, 1, 0.75f, 41), (18, VitalAdd, 1, 20f, 42));
            Check(VitalLoss.FullMax(301, m[0]) == 420, $"health 301 is 420 in full (400 + 20), got {VitalLoss.FullMax(301, m[0])}");
            // x1.2 buff, Endurance -50: AC shows 375 x 1.2 = 450; in full 400 x 1.2 = 480.
            VitalMods[] b = Mods((19, AttrAdd, VitalLoss.Endurance, -50f, 40), (20, VitalMult, 1, 1.2f, 43));
            Check(VitalLoss.FullMax(450, b[0]) == 480, $"health 450 under a x1.2 buff is 480 in full, got {VitalLoss.FullMax(450, b[0])}");
            Check(VitalLoss.FullMax(450, b[0]) >= 450 && VitalLoss.FullMax(1, b[0]) >= 1, "never below the shown maximum");
        }

        private static void StrongestOfACategory()
        {
            Case("one spell category counts once (its strongest); category 0 entries each count");
            VitalMods[] same = Mods((21, AttrAdd, VitalLoss.Endurance, -30f, 50), (22, AttrAdd, VitalLoss.Endurance, -50f, 50));
            Check(VitalLoss.FullMax(300, same[1]) == 350, "Endurance -30 and -50 of one category: only -50");
            VitalMods[] tie = Mods((23, AttrAdd, VitalLoss.Endurance, -50f, 50), (24, AttrAdd, VitalLoss.Endurance, -50f, 50));
            Check(VitalLoss.FullMax(300, tie[1]) == 350, "two equal ones of a category count once");
            VitalMods[] two = Mods((25, AttrAdd, VitalLoss.Endurance, -30f, 50), (26, AttrAdd, VitalLoss.Endurance, -20f, 51));
            Check(VitalLoss.FullMax(300, two[1]) == 350, "two categories add up (-30 and -20)");
            VitalMods[] zero = Mods((27, AttrAdd, VitalLoss.Endurance, -30f, 0), (28, AttrAdd, VitalLoss.Endurance, -20f, 0));
            Check(VitalLoss.FullMax(300, zero[1]) == 350, "category 0 entries each count");
        }

        private static void SkippedEntries()
        {
            Case("vitae, cooldowns, zero ids, skills, NaN and short arrays are left out");
            VitalMods[] m = Mods((29, VitalMult | VitalLoss.ModVitae, 1, 0.9f, 60), (0x8001, AttrAdd, VitalLoss.Endurance, -50f, 61),
                (0, AttrAdd, VitalLoss.Endurance, -50f, 62), (30, 0x10 | VitalLoss.ModAdditive, 2, -50f, 63),
                (31, AttrAdd, VitalLoss.Endurance, float.NaN, 64), (32, VitalLoss.ModSecondAttribute, 1, -50f, 65));
            Check(!m[0].AnyDebuff && !m[1].AnyDebuff && !m[2].AnyDebuff, "none of them is a debuff here");
            var mods = new VitalMods[3];
            VitalLoss.Collect(new uint[] { 33 }, new uint[] { AttrAdd }, new uint[0], new float[] { -50f }, new uint[] { 70 }, 5, mods);
            Check(!mods[1].AnyDebuff, "a short array is cut to its length (nothing read past it)");
        }

        // ── harness ─────────────────────────────────────────────────────────

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
