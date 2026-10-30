using RynthCore.Engine.ImGuiBackend.Panels;
using RynthCore.Engine.UI;

namespace BarDeclutterTests;

// Offline tests for the bar declutter work. See the csproj.
internal static class Program
{
    private static int _fails, _asserts;
    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }
    private static void Eq<T>(T actual, T expected, string msg) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{msg}: expected '{expected}', got '{actual}'");

    private static int Main()
    {
        ShownLatchBasics();
        EmbedShowRedrawsHide();
        EmbedTabSwitches();
        EmbedHideWithoutShow();
        EmbedDoubleClear();
        EmbedWindowClosedAndIdle();
        EmbedRaceAndShutdown();
        EmbedTwoThreads();
        FaceOnShownTwice();
        TabsAvailableRule();
        TitleLayoutGrid();
        TitleLayoutModes();
        TitleLayoutNarrow();
        TitleLayoutBadInput();
        ElideTitle();
        HeaderExtras();
        HeaderVault();
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? $"PASS ({_asserts} checks)" : $"FAIL ({_fails} of {_asserts} checks failed)");
        return _fails == 0 ? 0 : 1;
    }

    // ── The lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Stands in for SkillsFace: the same ShownLatch wiring (OnShown/OnHidden through a latch that runs a
    /// "core" once per edge), with the Progression subscribe / unsubscribe counted instead of UiSource.
    /// </summary>
    private sealed class FakeFace
    {
        public int Subscribes, Unsubscribes, Outstanding;
        private readonly ShownLatch _life;
        public FakeFace() => _life = new ShownLatch(() => { Subscribes++; Outstanding++; }, () => { Unsubscribes++; Outstanding--; });
        public void OnShown() => _life.Show();
        public void OnHidden() => _life.Hide();
    }

    private static FakeFace Fresh()
    {
        OracleSkillsEmbed.Shutdown();            // hides anything left by a previous test, forgets the announcement
        var face = new FakeFace();
        OracleSkillsEmbed.Bind(face.OnShown, face.OnHidden);
        return face;
    }

    /// <summary>One frame of ScriptWindowFace.Draw for the Oracle window: embed drawn only while a tab is active.</summary>
    private static void Frame(long nowMs)
    {
        OracleSkillsEmbed.Sweep(nowMs);                          // ImGuiPanelHost.DrawAll
        if (OracleSkillsEmbed.ActiveTab >= 0) OracleSkillsEmbed.NoteDrawn(nowMs);   // SkillsFace.DrawEmbedded
    }

    private static void ShownLatchBasics()
    {
        Console.WriteLine("ShownLatch");
        int on = 0, off = 0;
        var l = new ShownLatch(() => on++, () => off++);
        Eq(l.Hide(), false, "hide before any show does nothing");
        Eq(off, 0, "no hidden callback without a show");
        Eq(l.Show(), true, "first show runs");
        Eq(l.Show(), false, "second show is a no-op");
        Eq(l.IsShown, true, "shown");
        Eq(l.Hide(), true, "hide runs");
        Eq(l.Hide(), false, "second hide is a no-op");
        Eq(l.IsShown, false, "hidden");
        Eq((on, off), (1, 1), "one callback each");

        // A shown callback that throws still leaves the latch on, so the matching hide runs.
        int h = 0;
        var t = new ShownLatch(() => throw new InvalidOperationException("boom"), () => h++);
        try { t.Show(); } catch (InvalidOperationException) { }
        Eq(t.IsShown, true, "a throwing show leaves the latch on");
        t.Hide();
        Eq(h, 1, "so the hide still runs once");
    }

    private static void EmbedShowRedrawsHide()
    {
        Console.WriteLine("Embed: show, N redraws, hide");
        FakeFace f = Fresh();
        OracleSkillsEmbed.SetActiveTab(0);
        Eq(f.Subscribes, 0, "setting the tab alone shows nothing (the Oracle window may be closed)");
        long t = 1000;
        for (int i = 0; i < 600; i++, t += 16) Frame(t);      // ~10 s of 60 fps
        Eq(f.Subscribes, 1, "600 redraws: one subscribe");
        Eq(f.Unsubscribes, 0, "600 redraws: no unsubscribe yet");
        Eq(OracleSkillsEmbed.IsShown, true, "shown while drawing");
        // The old code re-ran OnShown every 2 s: 5 more "2 s gaps" must not add subscribes.
        for (int i = 0; i < 5; i++) { t += 900; Frame(t); Frame(t + 16); }
        Eq(f.Subscribes, 1, "redraws after long gaps (the old re-run trigger): still one subscribe");
        OracleSkillsEmbed.Clear();
        Eq(f.Unsubscribes, 1, "Clear: one unsubscribe");
        Eq(f.Outstanding, 0, "balanced");
        Eq(OracleSkillsEmbed.IsShown, false, "hidden");
        for (int i = 0; i < 50; i++, t += 16) Frame(t);       // ActiveTab is -1: nothing draws
        Eq((f.Subscribes, f.Unsubscribes), (1, 1), "frames after Clear change nothing");

        // Shown again later: a second, separate stretch.
        OracleSkillsEmbed.SetActiveTab(1);
        for (int i = 0; i < 10; i++, t += 16) Frame(t);
        OracleSkillsEmbed.Clear();
        Eq((f.Subscribes, f.Unsubscribes), (2, 2), "second stretch: two of each in all");
    }

    private static void EmbedTabSwitches()
    {
        Console.WriteLine("Embed: skills -> attrs -> progress -> off");
        FakeFace f = Fresh();
        long t = 5000;
        OracleSkillsEmbed.SetActiveTab(0);
        for (int i = 0; i < 20; i++, t += 16) Frame(t);
        OracleSkillsEmbed.SetActiveTab(1);
        for (int i = 0; i < 20; i++, t += 16) Frame(t);
        OracleSkillsEmbed.SetActiveTab(2);
        for (int i = 0; i < 20; i++, t += 16) Frame(t);
        Eq(OracleSkillsEmbed.ActiveTab, 2, "progress selected");
        Eq((f.Subscribes, f.Unsubscribes), (1, 0), "switching tabs does not resubscribe or release");
        OracleSkillsEmbed.SetActiveTab(0);
        Eq((f.Subscribes, f.Unsubscribes), (1, 0), "back to skills: still the same stretch");
        OracleSkillsEmbed.Clear();                              // /rc oracle-skills off
        Eq((f.Subscribes, f.Unsubscribes), (1, 1), "off: released once");
        Eq(OracleSkillsEmbed.ActiveTab, -1, "no tab");
        OracleSkillsEmbed.SetActiveTab(7);
        Eq(OracleSkillsEmbed.ActiveTab, -1, "an out-of-range tab is off");
        OracleSkillsEmbed.SetActiveTab(-3);
        Eq((f.Subscribes, f.Unsubscribes), (1, 1), "out-of-range tabs change nothing else");
    }

    private static void EmbedHideWithoutShow()
    {
        Console.WriteLine("Embed: hide without show");
        FakeFace f = Fresh();
        OracleSkillsEmbed.Clear();
        OracleSkillsEmbed.Sweep(1_000_000);
        f.OnHidden();
        Eq((f.Subscribes, f.Unsubscribes), (0, 0), "nothing was shown: nothing released (no negative refcount)");
        Eq(f.Outstanding, 0, "outstanding is zero");
    }

    private static void EmbedDoubleClear()
    {
        Console.WriteLine("Embed: double Clear");
        FakeFace f = Fresh();
        OracleSkillsEmbed.SetActiveTab(0);
        Frame(100);
        OracleSkillsEmbed.Clear();
        OracleSkillsEmbed.Clear();
        OracleSkillsEmbed.Clear();
        Eq((f.Subscribes, f.Unsubscribes), (1, 1), "three Clears: one release");
        f.OnHidden();
        Eq(f.Unsubscribes, 1, "even a stray OnHidden on the face is a no-op");
    }

    private static void EmbedWindowClosedAndIdle()
    {
        Console.WriteLine("Embed: Oracle window closed / idle sweep");
        FakeFace f = Fresh();
        OracleSkillsEmbed.SetActiveTab(2);
        Frame(0); Frame(16);
        // ScriptWindowFace.OnHidden / ScriptWindowRegistry.RemoveLocked for the Oracle window:
        Check(OracleSkillsEmbed.IsOraclePanel("script:RynthOracle/Oracle"), "the Oracle window's panel key matches");
        Check(!OracleSkillsEmbed.IsOraclePanel("script:RynthSort/Main"), "another script window's key does not");
        if (OracleSkillsEmbed.IsOraclePanel("script:RynthOracle/Oracle")) OracleSkillsEmbed.Clear();
        Eq((f.Subscribes, f.Unsubscribes), (1, 1), "window closed: released once");

        // Idle: the tab stays set but nothing draws (window collapsed / owner crashed without sending off).
        OracleSkillsEmbed.SetActiveTab(0);
        Frame(10_000); Frame(10_016);
        Eq(f.Subscribes, 2, "shown again");
        OracleSkillsEmbed.Sweep(10_016 + OracleSkillsEmbed.IdleMs);   // exactly at the limit: still shown
        Eq(f.Unsubscribes, 1, "at the idle limit: not yet released");
        OracleSkillsEmbed.Sweep(10_016 + OracleSkillsEmbed.IdleMs + 5000);
        Eq((f.Subscribes, f.Unsubscribes), (2, 2), "past the idle limit: released once");
        OracleSkillsEmbed.Sweep(10_016 + OracleSkillsEmbed.IdleMs + 9000);
        Eq(f.Unsubscribes, 2, "sweeping again releases nothing more");
        Frame(30_000);
        Eq((f.Subscribes, f.Unsubscribes), (3, 2), "the window drawing again shows it again");
        OracleSkillsEmbed.Shutdown();
        Eq((f.Subscribes, f.Unsubscribes), (3, 3), "engine shutdown releases it");
    }

    private static void EmbedRaceAndShutdown()
    {
        Console.WriteLine("Embed: draw after Clear, shutdown");
        FakeFace f = Fresh();
        OracleSkillsEmbed.SetActiveTab(0);
        Frame(1);
        OracleSkillsEmbed.Clear();
        OracleSkillsEmbed.NoteDrawn(2);                         // a draw that read the tab just before Clear
        Eq((f.Subscribes, f.Unsubscribes), (1, 1), "a draw after Clear does not re-show");
        OracleSkillsEmbed.Announce();
        Check(OracleSkillsEmbed.TabsAvailable(true), "announced and registered");
        OracleSkillsEmbed.Shutdown();
        Check(!OracleSkillsEmbed.TabsAvailable(true), "shutdown forgets the announcement");
    }

    private static void EmbedTwoThreads()
    {
        Console.WriteLine("Embed: Show and Clear from two threads");
        FakeFace f = Fresh();
        OracleSkillsEmbed.SetActiveTab(0);
        var drawer = Task.Run(() => { for (int i = 0; i < 20000; i++) OracleSkillsEmbed.NoteDrawn(i); });
        var clearer = Task.Run(() => { for (int i = 0; i < 20000; i++) OracleSkillsEmbed.Clear(); });
        Task.WaitAll(drawer, clearer);
        OracleSkillsEmbed.Clear();
        Eq(f.Subscribes, f.Unsubscribes, "every subscribe has its unsubscribe");
        Check(f.Outstanding == 0, "outstanding is zero");
    }

    private static void FaceOnShownTwice()
    {
        Console.WriteLine("Face: host and embed both asking");
        // The panel host calls OnShown once per open; the embed's latch may also call it. A face whose OnShown is
        // idempotent subscribes once however it is asked.
        var f = new FakeFace();
        f.OnShown(); f.OnShown(); f.OnShown();
        Eq(f.Subscribes, 1, "OnShown x3: one subscribe");
        f.OnHidden(); f.OnHidden();
        Eq((f.Subscribes, f.Unsubscribes), (1, 1), "OnHidden x2: one unsubscribe");
    }

    private static void TabsAvailableRule()
    {
        Console.WriteLine("TabsAvailable (Skills bar icon / routing)");
        Fresh();
        Eq(OracleSkillsEmbed.TabsAvailable(false), false, "no Oracle window, never announced: Skills icon stays");
        Eq(OracleSkillsEmbed.TabsAvailable(true), false, "an Oracle window that never announced (older Oracle): Skills icon stays");
        OracleSkillsEmbed.Announce();
        Eq(OracleSkillsEmbed.TabsAvailable(false), false, "announced but its window is gone: Skills icon returns");
        Eq(OracleSkillsEmbed.TabsAvailable(true), true, "window registered and announced: the tabs are there, icon hidden");
        OracleSkillsEmbed.Shutdown();
    }

    // ── The title bar ───────────────────────────────────────────────────

    private static IEnumerable<(string Name, TitleSlot Slot)> Slots(InventoryTitleLayout l)
    {
        yield return ("Smaller", l.Smaller);
        yield return ("Bigger", l.Bigger);
        yield return ("Tools", l.Tools);
        yield return ("Modern", l.Modern);
        yield return ("Vault", l.Vault);
        yield return ("Remote", l.Remote);
        yield return ("Sort", l.Sort);
        yield return ("Count", l.Count);
        yield return ("GInv", l.GInv);
        yield return ("More", l.More);
    }

    private static void CheckLayout(InventoryTitleLayout l, float barW, float s, bool vault, float titleWant, string tag)
    {
        var present = Slots(l).Where(x => x.Slot.Present).OrderBy(x => x.Slot.X).ToList();
        foreach (var (name, slot) in present)
        {
            Check(slot.X >= -0.001f, $"{tag}: {name} starts on the bar (x={slot.X})");
            Check(slot.Right <= barW + 0.001f, $"{tag}: {name} ends on the bar (right={slot.Right}, bar={barW})");
        }
        for (int i = 1; i < present.Count; i++)
            Check(present[i - 1].Slot.Right <= present[i].Slot.X + 0.001f,
                $"{tag}: {present[i - 1].Name} does not overlap {present[i].Name} ({present[i - 1].Slot.Right} vs {present[i].Slot.X})");
        Eq(l.Vault.Present, vault, $"{tag}: Vault present iff the server has it");
        bool extrasShown = l.Mode is TitleExtrasMode.Text or TitleExtrasMode.Icons;
        Eq(l.Remote.Present && l.Sort.Present && l.Count.Present && l.GInv.Present, extrasShown, $"{tag}: all four related buttons or none");
        Eq(l.More.Present, l.Mode == TitleExtrasMode.Menu, $"{tag}: the more button iff Menu mode");
        Check(l.TitleLeft >= 0f && l.TitleRight >= l.TitleLeft, $"{tag}: title span is ordered ({l.TitleLeft}..{l.TitleRight})");
        Check(l.TitleRight <= l.LeftmostX + 0.001f, $"{tag}: title ends before the first button ({l.TitleRight} vs {l.LeftmostX})");
        Check(l.TitleWidth > 0f, $"{tag}: title width > 0 ({l.TitleWidth})");
        Check(l.Mode != TitleExtrasMode.None, $"{tag}: the related buttons are reachable from the bar (mode {l.Mode})");
        // The title keeps its minimum (or its whole text when shorter) whenever all four buttons are shown.
        float owed = Math.Min(titleWant, InventoryTitleLayout.TitleMinUnits * s);
        if (extrasShown)
            Check(l.TitleWidth >= owed - 0.001f - InventoryTitleLayout.PadUnits * s, $"{tag}: title keeps its room ({l.TitleWidth} >= {owed})");
    }

    private static void TitleLayoutGrid()
    {
        Console.WriteLine("Title bar: widths 300/360/480 x scales 1/1.5/2 x Vault on/off");
        foreach (float units in new[] { 300f, 360f, 480f })
            foreach (float s in new[] { 1f, 1.5f, 2f })
                foreach (bool vault in new[] { true, false })
                    foreach (float want in new[] { 40f, 135f, 190f })
                    {
                        float barW = (units - 24f) * s;          // the panel less the close button
                        var l = InventoryTitleLayout.Compute(barW, s, vault, want * s);
                        CheckLayout(l, barW, s, vault, want * s, $"{units}u s{s} vault={vault} title={want}");
                    }
        // The bar widths a 1.0-2.0 scale gives at the fixed retail panel (300 units x scale), at fractional scales too.
        for (float s0 = 1f; s0 <= 2.0001f; s0 += 1f / 32f)
            foreach (bool vault in new[] { true, false })
            {
                float sc = MathF.Round(32f * s0) / 32f;
                float barW = 276f * sc;
                var l = InventoryTitleLayout.Compute(barW, sc, vault, 130f * sc);
                CheckLayout(l, barW, sc, vault, 130f * sc, $"retail s={sc} vault={vault}");
            }
    }

    private static void TitleLayoutModes()
    {
        Console.WriteLine("Title bar: which style at which width");
        Eq(InventoryTitleLayout.Compute(276f, 1f, true, 130f).Mode, TitleExtrasMode.Menu, "retail width + Vault: one more menu");
        Eq(InventoryTitleLayout.Compute(276f, 1f, false, 130f).Mode, TitleExtrasMode.Icons, "retail width, no Vault: four icons");
        Eq(InventoryTitleLayout.Compute(336f, 1f, true, 130f).Mode, TitleExtrasMode.Icons, "360 wide + Vault: icons");
        Eq(InventoryTitleLayout.Compute(456f, 1f, true, 130f).Mode, TitleExtrasMode.Text, "480 wide + Vault: text buttons");
        Eq(InventoryTitleLayout.Compute(456f * 1.5f, 1.5f, true, 130f * 1.5f).Mode, TitleExtrasMode.Text, "480 wide at 1.5x: text buttons");
        Eq(InventoryTitleLayout.Compute(276f * 1.5f, 1.5f, true, 130f * 1.5f).Mode, TitleExtrasMode.Menu, "retail at 1.5x + Vault: more menu");

        // The case from the review: 190 px of text buttons used to push Vault to x=-44 at 300 / scale 1.
        var r = InventoryTitleLayout.Compute(276f, 1f, true, 130f);
        Check(r.Vault.X > 100f && r.Vault.Right <= r.Smaller.X, $"Vault sits just left of the toggles at retail width (x={r.Vault.X})");
        Check(r.TitleWidth >= 100f, $"and the title keeps {r.TitleWidth} px at retail width");

        // Everything is relative to the bar: scaling the bar scales the positions (whole-pixel rounding aside).
        var a = InventoryTitleLayout.Compute(276f, 1f, true, 130f);
        var b = InventoryTitleLayout.Compute(552f, 2f, true, 260f);
        Check(MathF.Abs(a.Vault.X * 2f - b.Vault.X) < 0.01f, "Vault at 2x is twice the 1x position");
        Check(MathF.Abs(a.More.X * 2f - b.More.X) < 0.01f, "More at 2x is twice the 1x position");
    }

    private static void TitleLayoutNarrow()
    {
        Console.WriteLine("Title bar: narrower than any real panel");
        for (float barW = 150f; barW <= 700f; barW += 5f)
        {
            var l = InventoryTitleLayout.Compute(barW, 1f, true, 130f);
            var present = Slots(l).Where(x => x.Slot.Present).OrderBy(x => x.Slot.X).ToList();
            Check(l.Vault.Present && l.Vault.X >= 0f && l.Vault.Right <= barW, $"w={barW}: Vault present and on the bar");
            Check(present.All(x => x.Slot.X >= 0f && x.Slot.Right <= barW + 0.001f), $"w={barW}: every button on the bar");
            for (int i = 1; i < present.Count; i++)
                Check(present[i - 1].Slot.Right <= present[i].Slot.X + 0.001f, $"w={barW}: {present[i - 1].Name} / {present[i].Name} do not overlap");
            Check(l.TitleWidth >= 0f, $"w={barW}: title width never negative");
        }
        // Far too narrow for the toggles: Vault is clamped at the left edge, never negative; no related buttons.
        var tiny = InventoryTitleLayout.Compute(60f, 1f, true, 130f);
        Check(tiny.Vault.Present && tiny.Vault.X == 0f, "a 60 px bar: Vault pinned at x=0, not negative");
        Eq(tiny.Mode, TitleExtrasMode.None, "a 60 px bar: no related buttons");
        Check(tiny.TitleWidth >= 0f, "a 60 px bar: title width not negative");
        var noVault = InventoryTitleLayout.Compute(60f, 1f, false, 130f);
        Check(!noVault.Vault.Present, "no Vault server: no Vault slot");
    }

    private static void TitleLayoutBadInput()
    {
        Console.WriteLine("Title bar: bad input");
        var l = InventoryTitleLayout.Compute(float.NaN, 0f, true, float.PositiveInfinity);
        Check(float.IsFinite(l.Vault.X) && float.IsFinite(l.TitleRight), "NaN bar width, zero scale: finite positions");
        var m = InventoryTitleLayout.Compute(276f, float.NaN, false, -5f);
        Check(float.IsFinite(m.Modern.X) && m.Modern.Right <= 276f, "NaN scale reads as 1");
    }

    private static void ElideTitle()
    {
        Console.WriteLine("Title: elide");
        Func<string, float> m = t => t.Length * 7f;
        string full = "Inventory of Drakkon1";          // 21 chars = 147 px
        Eq(InventoryTitleLayout.Elide(full, 200f, m), full, "fits: unchanged");
        Eq(InventoryTitleLayout.Elide(full, 147f, m), full, "fits exactly: unchanged");
        string cut = InventoryTitleLayout.Elide(full, 100f, m);
        Check(cut.EndsWith("...") && m(cut) <= 100f && cut.Length > 3, $"cut to fit with an ellipsis: '{cut}'");
        Eq(InventoryTitleLayout.Elide(full, 100f, m), cut, "same input, same output");
        Eq(InventoryTitleLayout.Elide(full, 21f, m), "...", "room for only the ellipsis");
        Eq(InventoryTitleLayout.Elide(full, 20f, m), "", "not even the ellipsis fits: nothing");
        Eq(InventoryTitleLayout.Elide(full, 0f, m), "", "no room: nothing");
        Eq(InventoryTitleLayout.Elide("", 100f, m), "", "empty stays empty");
        for (float w = 21f; w < 147f; w += 7f)
            Check(m(InventoryTitleLayout.Elide(full, w, m)) <= w, $"never wider than the room ({w})");
    }

    private static void HeaderExtras()
    {
        Console.WriteLine("Modern header: icons / menu / none");
        Eq(InventoryTitleLayout.ChooseHeaderExtras(300f, 50f, out bool c1), TitleExtrasMode.Icons, "lots of room: four icons");
        Eq(c1, true, "and the count text");
        Eq(InventoryTitleLayout.ChooseHeaderExtras(100f, 50f, out bool c2), TitleExtrasMode.Menu, "room for the menu button and the count");
        Eq(c2, true, "count kept");
        Eq(InventoryTitleLayout.ChooseHeaderExtras(40f, 50f, out bool c3), TitleExtrasMode.Menu, "room for the menu button only");
        Eq(c3, false, "count text dropped before the menu button");
        Eq(InventoryTitleLayout.ChooseHeaderExtras(10f, 50f, out bool c4), TitleExtrasMode.None, "no room: none");
        Eq(c4, false, "nothing fits");
        Eq(InventoryTitleLayout.ChooseHeaderExtras(-20f, 50f, out bool c5), TitleExtrasMode.None, "negative room: none");
        Eq(c5, false, "no count either");
        Eq(InventoryTitleLayout.ChooseHeaderExtras(float.NaN, 50f, out _), TitleExtrasMode.None, "NaN room: none");
    }

    // ── The Modern header with the Aelrynth Vault button (2026-10-19; idea from Silentkelpie, rynth PR 6) ──

    private static IEnumerable<(string Name, TitleSlot Slot)> HeaderSlots(InventoryHeaderLayout h)
    {
        yield return ("Close", h.Close);
        yield return ("Classic", h.Classic);
        yield return ("Vault", h.Vault);
        yield return ("Remote", h.Remote);
        yield return ("Sort", h.Sort);
        yield return ("Count", h.Count);
        yield return ("GInv", h.GInv);
        yield return ("More", h.More);
    }

    private static void HeaderVault()
    {
        Console.WriteLine("Modern header: the Vault button");

        // Without a vault the plan is exactly the old one, so the header is unchanged off Aelrynth.
        foreach (float free in new[] { -30f, 0f, 10f, 24f, 40f, 90f, 100f, 130f, 300f })
            foreach (float cw in new[] { 0f, 30f, 50f, 80f })
            {
                var plan = InventoryTitleLayout.ChooseHeader(free, cw, vault: false);
                TitleExtrasMode old = InventoryTitleLayout.ChooseHeaderExtras(free, cw, out bool oldCount);
                Check(!plan.Vault && plan.Extras == old && plan.ShowCount == oldCount, $"no vault: same as ChooseHeaderExtras (free {free}, count {cw})");
            }

        // With a vault the Vault takes its 24 px first; the rest shares what is left.
        Eq(InventoryTitleLayout.ChooseHeader(300f, 50f, true).Extras, TitleExtrasMode.Icons, "lots of room + vault: four icons");
        Eq(InventoryTitleLayout.ChooseHeader(96f + 60f + 24f - 1f, 50f, true).Extras, TitleExtrasMode.Menu, "one px short of icons + vault: the menu");
        Eq(InventoryTitleLayout.ChooseHeader(96f + 60f + 24f, 50f, true).Extras, TitleExtrasMode.Icons, "icons + count + vault just fit");
        Eq(InventoryTitleLayout.ChooseHeader(24f + 24f + 60f, 50f, true).Extras, TitleExtrasMode.Menu, "vault + menu + count");
        var tight = InventoryTitleLayout.ChooseHeader(24f + 24f, 50f, true);
        Check(tight.Vault && tight.Extras == TitleExtrasMode.Menu && !tight.ShowCount, "vault + menu, the count text dropped");
        var tiny = InventoryTitleLayout.ChooseHeader(24f, 50f, true);
        Check(tiny.Vault && tiny.Extras == TitleExtrasMode.None && !tiny.ShowCount, "room for the vault only: no menu, no count");
        foreach (float free in new[] { 20f, 0f, -40f, float.NaN, float.NegativeInfinity })
            Check(InventoryTitleLayout.ChooseHeader(free, 50f, true).Vault, $"the Vault is kept whatever the room ({free})");

        // Every supported width (the panel's minimum is 140 px) and a range of left-hand text widths:
        // the Vault is present, inside the header, clear of the other buttons, and nothing drawn right of the
        // pyreals overlaps anything else. Without a vault the geometry is the one the header always had.
        int checkedLayouts = 0;
        for (float w = 140f; w <= 800f; w += 1f)
            foreach (float leftEnd in new[] { 80f, 120f, 150f, 200f })
                foreach (float cw in new[] { 0f, 40f, 70f })
                    foreach (bool vault in new[] { true, false })
                    {
                        var h = InventoryHeaderLayout.Compute(w, leftEnd, cw, vault);
                        string tag = $"w {w} leftEnd {leftEnd} count {cw} vault {vault}";
                        checkedLayouts++;
                        var slots = HeaderSlots(h).Where(x => x.Slot.Present).OrderBy(x => x.Slot.X).ToList();
                        bool ok = true;
                        foreach (var (name, slot) in slots)
                        {
                            if (slot.X < 0f || slot.Right > w + 0.001f) { ok = false; Console.WriteLine($"  [FAIL] {tag}: {name} outside the header ({slot.X}..{slot.Right})"); }
                        }
                        for (int i = 1; i < slots.Count; i++)
                            if (slots[i].Slot.X < slots[i - 1].Slot.Right - 0.001f)
                            {
                                ok = false;
                                Console.WriteLine($"  [FAIL] {tag}: {slots[i - 1].Name} and {slots[i].Name} overlap");
                            }
                        Check(ok, $"{tag}: buttons inside the header and not overlapping");

                        Check(h.Plan.Vault == vault && h.Vault.Present == vault, $"{tag}: the Vault button exists exactly when asked for");
                        if (vault)
                        {
                            Check(h.Vault.Right <= h.Classic.X + 0.001f && h.Vault.X >= 0f, $"{tag}: Vault sits left of the Classic button, inside the header");
                            Check(h.Vault.W >= 22f, $"{tag}: Vault keeps its full 22 px size");
                            foreach (var (name, slot) in slots)
                                if (name != "Vault" && name != "Close" && name != "Classic")
                                    Check(slot.Right <= h.Vault.X + 0.001f, $"{tag}: {name} stays left of the Vault");
                        }
                        else
                        {
                            Check(h.Classic.X == w - 46 && h.Close.X == w - 22, $"{tag}: Close / Classic where they always were");
                            if (h.Plan.Extras == TitleExtrasMode.Icons) Check(h.GInv.X == w - 46 - 24 && h.Remote.X == w - 46 - 96, $"{tag}: the four icons where they always were");
                            if (h.Plan.Extras == TitleExtrasMode.Menu) Check(h.More.X == w - 46 - 24, $"{tag}: the menu button where it always was");
                        }
                        // Related buttons / count never reach back over the pyreals when there was room for them.
                        TitleSlot? leftmost = h.Plan.Extras == TitleExtrasMode.Icons ? h.Remote : h.Plan.Extras == TitleExtrasMode.Menu ? h.More : null;
                        if (leftmost is { } lm)
                            Check(lm.X >= leftEnd + 4f - 0.001f, $"{tag}: related buttons start clear of the pyreals text");
                        if (h.Plan.ShowCount)
                            Check(h.CountX + cw <= (leftmost?.X ?? (vault ? h.Vault.X : h.Classic.X)) + 0.001f, $"{tag}: the count text sits left of the buttons");
                    }
        Check(checkedLayouts > 10000, $"swept the layouts ({checkedLayouts})");

        // At the retail width (302 px) and the usual left-hand text the Vault is a button, and so are the
        // rest that fit: a worked example.
        var r = InventoryHeaderLayout.Compute(302f, 150f, 50f, vault: true);
        Check(r.Vault.Present && r.Vault.X == 302f - 46 - 24, "302 px: Vault right beside the Classic button");
        Eq(r.Plan.Extras, TitleExtrasMode.Menu, "302 px with a vault: related panels fold into the menu");
        Check(r.More.Present && r.More.Right <= r.Vault.X, "...left of the Vault");
        var wide = InventoryHeaderLayout.Compute(600f, 150f, 50f, vault: true);
        Eq(wide.Plan.Extras, TitleExtrasMode.Icons, "600 px with a vault: four icons");
        Check(wide.Vault.X == 600f - 46 - 24 && wide.GInv.X == wide.Vault.X - 24, "...the icons start just left of the Vault");

        // Garbage in, no crash and the Vault still present.
        var bad = InventoryHeaderLayout.Compute(float.NaN, float.NaN, float.NaN, vault: true);
        Check(bad.Vault.Present, "NaN sizes: the Vault slot exists (the caller never draws a header this small)");
    }
}
