using RynthCore.Engine.Compatibility;

namespace PanelSwapPolicyTests;

/// <summary>
/// PanelSwapPolicy, and a model of AC's panel container (gmPanelUI) that shows why it is needed: closing a
/// panel opened over another one, from outside a UI message, re-enters AC's handler and leaves the earlier
/// panel shown in a hidden container; the next panel opened there shows on top of it (the pack's paper doll
/// over AC's Vitae panel).
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    // Panel notice ids (AC's): the pack, the Character window (claimed by a plugin on some servers), vitae.
    private const uint Pack = 7, Character = 11, Vitae = 15, Spellbook = 13;

    private static int Main()
    {
        PolicyChecks();
        ModelChecks();
        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    // ── The policy alone ─────────────────────────────────────────────────

    private static void PolicyChecks()
    {
        const nint vitae = 0x1000, pack = 0x2000, other = 0x3000;

        Check(PanelSwapPolicy.PreviousToWatch(false, vitae, vitae, pack) == pack,
            "closing the shown panel that has a previous one -> watch the previous one");
        Check(PanelSwapPolicy.PreviousToWatch(true, vitae, vitae, pack) == 0, "an open is never watched");
        Check(PanelSwapPolicy.PreviousToWatch(false, vitae, other, pack) == 0,
            "closing a panel the container doesn't show -> nothing (AC just hides it)");
        Check(PanelSwapPolicy.PreviousToWatch(false, vitae, vitae, 0) == 0, "no previous panel -> nothing");
        Check(PanelSwapPolicy.PreviousToWatch(false, vitae, vitae, vitae) == 0,
            "previous is the closing panel itself (the env container's keep) -> nothing");
        Check(PanelSwapPolicy.PreviousToWatch(false, 0, 0, pack) == 0, "unknown panel (0) -> nothing");

        Check(PanelSwapPolicy.ShouldReopen(pack, 0, watchedShows: true, claimed: false),
            "no current panel after, the previous one still shown -> open it again");
        Check(!PanelSwapPolicy.ShouldReopen(pack, pack, true, false),
            "AC made the previous panel current (its own swap worked) -> nothing");
        Check(!PanelSwapPolicy.ShouldReopen(pack, other, true, false), "another panel is current -> nothing");
        Check(!PanelSwapPolicy.ShouldReopen(pack, 0, watchedShows: false, claimed: false),
            "container empty and the previous panel hidden too (all closed) -> nothing");
        Check(!PanelSwapPolicy.ShouldReopen(pack, 0, true, claimed: true),
            "the previous panel is claimed by a plugin -> never pop AC's up");
        Check(!PanelSwapPolicy.ShouldReopen(0, 0, true, false), "nothing watched -> nothing");
    }

    // ── AC's container, modelled ─────────────────────────────────────────

    private static void ModelChecks()
    {
        // The screenshot: the pack open, AC pops the vitae panel over it (vitae has attribute 0x10000049,
        // the pack hasn't, so the pack becomes "previous"), the vitae panel is closed from the game tick
        // (RynthCore's bar or /rc panels), later AC pops it again.
        foreach (bool repair in new[] { false, true })
        {
            var ui = new AcUi(repair);
            ui.Notice(Pack, true);
            Check(ui.Shown() == "7", $"[{Tag(repair)}] the pack opens alone");
            ui.Notice(Vitae, true);
            Check(ui.Shown() == "15", $"[{Tag(repair)}] vitae opens over the pack, the pack hides");
            Check(ui.Previous == Pack, $"[{Tag(repair)}] the pack is the panel to come back to");

            ui.Notice(Vitae, false);   // game tick: no UI message is being handled
            if (!repair)
            {
                Check(ui.Shown() == "" && !ui.ContainerShows, "[AC alone] the close hides the whole window");
                Check(ui.Current == 0 && ui.PanelOwnVisible(Pack),
                    "[AC alone] ...with no current panel and the pack still shown inside it");
                ui.Notice(Vitae, true);   // AC: vitae drops again
                Check(ui.Shown() == "7,15", "[AC alone] the next vitae pop shows over the pack (the paper doll comes through)");
            }
            else
            {
                Check(ui.Shown() == "7" && ui.Current == Pack, "[repaired] the close brings the pack back, as AC meant");
                Check(ui.Repairs == 1, "[repaired] one repair");
                ui.Notice(Vitae, true);
                Check(ui.Shown() == "15", "[repaired] the next vitae pop shows vitae alone");
                ui.Notice(Spellbook, true);
                Check(ui.Shown() == "13", "[repaired] another panel opened over it shows alone");
            }
        }

        // The same close from the vitae panel's own button: inside a UI message, so AC's inner notice waits
        // and its swap is right; the repair never acts.
        {
            var ui = new AcUi(repair: true);
            ui.Notice(Pack, true);
            ui.Notice(Vitae, true);
            ui.InUiMessage(() => ui.Notice(Vitae, false));
            Check(ui.Shown() == "7" && ui.Current == Pack, "[button] AC brings the pack back by itself");
            Check(ui.Repairs == 0, "[button] no repair needed or made");
        }

        // Closing a panel with nothing to come back to: the window closes, nothing reopens.
        {
            var ui = new AcUi(repair: true);
            ui.Notice(Pack, true);
            ui.Notice(Pack, false);
            Check(ui.Shown() == "" && !ui.ContainerShows && ui.Current == 0, "[pack only] closing it closes the window");
            Check(ui.Repairs == 0, "[pack only] no repair");
        }

        // The previous panel is one a plugin claimed (the Character window): AC's own reopen of it is held
        // back, and the repair never opens AC's.
        {
            var ui = new AcUi(repair: true);
            ui.Claim(Character);
            ui.Send(Character, true);       // shown before the claim, or opened by RynthCore's own path
            ui.Notice(Vitae, true);
            Check(ui.Previous == Character, "[claimed] the Character window is the panel to come back to");
            ui.Notice(Vitae, false);
            Check(!ui.PanelOwnVisible(Character) && ui.Shown() == "", "[claimed] AC's Character window stays closed");
            Check(ui.Repairs == 0, "[claimed] no repair");
        }

        // Opening another panel over vitae (the swap path) is untouched.
        {
            var ui = new AcUi(repair: true);
            ui.Notice(Pack, true);
            ui.Notice(Vitae, true);
            ui.Notice(Spellbook, true);
            Check(ui.Shown() == "13" && ui.Repairs == 0, "[swap] spellbook over vitae shows alone, no repair");
        }
    }

    private static string Tag(bool repair) => repair ? "repaired" : "AC alone";

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"[FAIL] {what}");
    }

    /// <summary>
    /// One panel container, as AC runs it: gmPanelUI::RecvNotice_SetPanelVisibility (0x004BD380) behind the
    /// hooked notice (CM_UI::SendNotice_SetPanelVisibility 0x0047A4A0), UIElement::SetVisible telling the
    /// container at once (element message 0x18 -> gmPanelUI::ListenToElementMessage 0x004BD300 -> the notice
    /// again) unless a UI message is already being handled (UIElementManager queues it then). The hook does
    /// what AcPanels.NoticeDetour does: hold back a claimed open, watch a close, repair after it.
    /// </summary>
    private sealed class AcUi
    {
        private sealed class Panel(uint id, bool returnsToPrevious)
        {
            public uint Id { get; } = id;
            public bool ReturnsToPrevious { get; } = returnsToPrevious;   // attribute 0x10000049
            public bool Own;                                               // element +0xA4 bit 1
            public nint Handle => (nint)Id * 0x100;
        }

        private readonly Dictionary<uint, Panel> _panels = new()
        {
            [Pack] = new Panel(Pack, false),
            [Character] = new Panel(Character, false),
            [Spellbook] = new Panel(Spellbook, false),
            [Vitae] = new Panel(Vitae, true),
        };
        private readonly HashSet<uint> _claimed = new();
        private readonly Queue<Panel> _queued = new();
        private readonly bool _repair;
        private Panel? _current, _previous;
        private bool _dispatching, _bypass;

        public AcUi(bool repair) => _repair = repair;

        public bool ContainerShows { get; private set; }
        public int Repairs { get; private set; }
        public uint Current => _current?.Id ?? 0;
        public uint Previous => _previous?.Id ?? 0;
        public bool PanelOwnVisible(uint id) => _panels[id].Own;
        public void Claim(uint id) => _claimed.Add(id);

        /// <summary>The panels on screen: shown themselves, in a shown container.</summary>
        public string Shown() => ContainerShows
            ? string.Join(",", _panels.Values.Where(p => p.Own).Select(p => p.Id).OrderBy(i => i))
            : "";

        /// <summary>A UI message being handled (a button click) while <paramref name="body"/> runs.</summary>
        public void InUiMessage(Action body)
        {
            _dispatching = true;
            body();
            Drain();
        }

        /// <summary>RynthCore's own notice (AcPanels.Send): claims don't hold it back.</summary>
        public void Send(uint id, bool open)
        {
            bool was = _bypass;
            _bypass = true;
            try { Notice(id, open); }
            finally { _bypass = was; }
        }

        /// <summary>The hooked notice: AcPanels.NoticeDetour around AC's handler.</summary>
        public void Notice(uint id, bool open)
        {
            if (open && _claimed.Contains(id) && !_bypass) return;   // held back, the plugin's window runs
            if (open) { Handler(id, true); return; }

            Panel closing = _panels[id];
            nint watched = _repair
                ? PanelSwapPolicy.PreviousToWatch(false, closing.Handle, _current?.Handle ?? 0, _previous?.Handle ?? 0)
                : 0;
            uint watchedId = watched == 0 ? 0 : _panels.Values.First(p => p.Handle == watched).Id;
            Handler(id, false);
            if (watched == 0) return;
            Panel prev = _panels[watchedId];
            if (!PanelSwapPolicy.ShouldReopen(watched, _current?.Handle ?? 0, prev.Own, _claimed.Contains(watchedId))) return;
            Repairs++;
            Send(watchedId, true);
        }

        /// <summary>gmPanelUI::RecvNotice_SetPanelVisibility, step for step.</summary>
        private void Handler(uint id, bool open)
        {
            Panel p = _panels[id];
            if (open)
            {
                if (_current == p) { SetVisible(p, true); ContainerShows = true; return; }
                Panel? old = _current;
                _current = p;
                if (old != null && old.Own)
                {
                    _previous = p.ReturnsToPrevious && !old.ReturnsToPrevious ? old : null;
                    Notice(old.Id, false);
                }
                SetVisible(p, true);
                ContainerShows = true;
                return;
            }
            if (_current == p)
            {
                SetVisible(p, false);                 // 0x004BD4A3: may re-enter this handler
                if (_previous != null)                // 0x004BD4A6: re-read after it
                {
                    Notice(_previous.Id, true);
                    _previous = null;
                    return;
                }
                _current = null;                      // 0x004BD4DA
                ContainerShows = false;
                return;
            }
            SetVisible(p, false);
        }

        /// <summary>UIElement::SetVisible: the bit, then element message 0x18 to the container.</summary>
        private void SetVisible(Panel p, bool v)
        {
            if (p.Own == v) return;
            p.Own = v;
            _queued.Enqueue(p);
            if (_dispatching) return;
            _dispatching = true;
            Drain();
        }

        /// <summary>UIElementManager::BroadcastElementMessage's loop: each message, then the ones queued meanwhile.</summary>
        private void Drain()
        {
            while (_queued.Count > 0)
            {
                Panel p = _queued.Dequeue();
                Notice(p.Id, p.Own);   // gmPanelUI::ListenToElementMessage sends (id, the panel's bit now)
            }
            _dispatching = false;
        }
    }
}
