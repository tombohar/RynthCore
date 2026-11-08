// ============================================================================
//  RynthCore.Engine - Hotkeys/HotkeyService.cs
//
//  The engine side of the user hotkeys (model and rules: HotkeyModel.cs).
//
//  Keys: Win32Backend's WndProc subclass hands every key message here on AC's
//  main thread (after RynthChat's own input capture, before AC and ImGui see
//  it). A bound combo fires on its first key-down only (never on auto-repeat),
//  in the world, with the client in the foreground and nobody typing: not in a
//  RynthCore text box (RynthChat, an ImGui field, a pop-out's box) and not in
//  AC's own chat entry (AcKeyboardFocus: AC's UI focus element). The keystroke
//  that fires is eaten (down, repeats, its char and its up); unbound keys pass
//  through untouched.
//
//  Actions run the way the panels run them: RynthAi/RynthSort exports through
//  the UI hub's pump (RynthAiCommands, RynthSortCommands), panels through
//  PanelRouter.Toggle, chat commands through AcMainThreadQueue (the next
//  main-thread drain, outside this keystroke's WndProc frame, so AC's chat bar
//  is never driven re-entrantly: see Win32Backend's WM_RYNTHCORE_CHAT note).
//
//  Storage: %LOCALAPPDATA%\RynthCore\hotkeys.txt for every client on the PC
//  (HotkeyStore). Each client looks at the file's time at most every 2 s, so a
//  change made in one client reaches the others without a restart.
//
//  Settings capture ("press a key"): the Settings section asks for the next
//  combo (BeginCapture); the next non-modifier key-down is taken for it (Esc
//  cancels) and AC never sees it.
//
//  Hot reload: no event subscriptions, no threads; everything here is this
//  generation's statics. Shutdown cancels a capture.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.Hotkeys;

internal static class HotkeyService
{
    private const uint WM_KILLFOCUS = 0x0008, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102,
        WM_DEADCHAR = 0x0103, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_SYSCHAR = 0x0106, WM_SYSDEADCHAR = 0x0107;

    private const long FileCheckMs = 2000;
    private const long CaptureTimeoutMs = 15000;

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static readonly object Sync = new();
    private static HotkeyFile _file = new(true, new List<HotkeyBinding>());   // replaced, never edited in place
    private static bool _loaded;
    private static DateTime _seenWriteUtc = DateTime.MinValue;
    private static long _nextFileCheckMs;
    private static int _pendingSaves;

    private static readonly HotkeyKeyTracker Tracker = new();   // AC's main thread only

    // Capture for the Settings section (main thread: the WndProc and ImGui both run there).
    private static bool _capturing;
    private static long _captureStartedMs;
    private static KeyCombo? _captured;
    private static bool _captureCancelled;
    private static string _captureNote = string.Empty;

    private static HotkeyGate _lastGate = HotkeyGate.Fire;
    private const long HeldBackLogMs = 2000;
    private static long _lastHeldLogAt;
    private static readonly uint OurPid = (uint)Environment.ProcessId;

    // The ILT section's panels are offered only where the bar shows its ILT button.
    static HotkeyService() => HotkeyActions.IltAvailable = static () => IltInfo.IsIlt && IltInfo.PluginLoaded;

    // ── Bindings ───────────────────────────────────────────────────────────

    /// <summary>The bindings and master switch now (reloaded when another client changed the file). Any thread.</summary>
    public static HotkeyFile Current
    {
        get
        {
            EnsureFresh();
            return Volatile.Read(ref _file);
        }
    }

    private static void EnsureFresh()
    {
        long now = Environment.TickCount64;
        if (_loaded && now < Volatile.Read(ref _nextFileCheckMs)) return;
        lock (Sync)
        {
            if (_loaded && now < _nextFileCheckMs) return;
            _nextFileCheckMs = now + FileCheckMs;
            if (Volatile.Read(ref _pendingSaves) > 0) return;   // our own write is on its way; don't read a half-old file
            DateTime w = HotkeyStore.LastWriteUtc();
            if (_loaded && w == _seenWriteUtc) return;
            HotkeyFile read = HotkeyStore.Load();
            // A file that was never offered the default keys (HotkeyDefaults: the one-touch heal keys) gets them
            // once and is written back, so they show in Settings > Hotkeys and a removed one stays removed.
            HotkeyFile loaded = HotkeyDefaults.Apply(read);
            bool seeded = !ReferenceEquals(loaded, read);
            bool first = !_loaded;
            _loaded = true;
            _seenWriteUtc = w;
            Volatile.Write(ref _file, loaded);
            if (first || loaded.Bindings.Count > 0)
                RynthLog.UI($"Hotkeys: {(first ? "loaded" : "reloaded")} {loaded.Bindings.Count} binding(s) from {HotkeyStore.FilePath} (hotkeys {(loaded.Enabled ? "on" : "off")}).");
            if (seeded)
            {
                RynthLog.UI($"Hotkeys: added the default keys ({string.Join(", ", HotkeyDefaults.All.Select(d => d.Combo + " " + d.Action))}) where free.");
                Commit(loaded);
            }
        }
    }

    private static void Commit(HotkeyFile next)
    {
        lock (Sync)
        {
            _loaded = true;
            Volatile.Write(ref _file, next);
            Interlocked.Increment(ref _pendingSaves);
        }
        UI.UiBackgroundWriter.Enqueue("hotkeys", () =>
        {
            bool ok = HotkeyStore.Save(next);
            lock (Sync)
            {
                if (ok) _seenWriteUtc = HotkeyStore.LastWriteUtc();
                Interlocked.Decrement(ref _pendingSaves);
            }
            if (!ok) RynthLog.UI($"Hotkeys: could not write {HotkeyStore.FilePath}.");
        });
    }

    private static List<HotkeyBinding> Copy() => new(Current.Bindings);

    public static void SetMasterEnabled(bool on)
    {
        HotkeyFile f = Current;
        if (f.Enabled != on) Commit(f with { Enabled = on, Bindings = new List<HotkeyBinding>(f.Bindings) });
    }

    public static void Add(HotkeyBinding b)
    {
        List<HotkeyBinding> list = Copy();
        list.Add(b);
        Commit(Current with { Bindings = list });
    }

    /// <summary>Replaces binding <paramref name="index"/> (from <see cref="Current"/>); false if it's gone.</summary>
    public static bool Replace(int index, HotkeyBinding b)
    {
        List<HotkeyBinding> list = Copy();
        if ((uint)index >= (uint)list.Count) return false;
        list[index] = b;
        Commit(Current with { Bindings = list });
        return true;
    }

    /// <summary>All the bindings at once (AcKeymapLive: a key taken for a shortcut slot comes off the others).</summary>
    public static void SetAll(List<HotkeyBinding> bindings) => Commit(Current with { Bindings = new List<HotkeyBinding>(bindings) });

    public static bool RemoveAt(int index)
    {
        List<HotkeyBinding> list = Copy();
        if ((uint)index >= (uint)list.Count) return false;
        list.RemoveAt(index);
        Commit(Current with { Bindings = list });
        return true;
    }

    /// <summary>Moves binding <paramref name="index"/> up (-1) or down (+1): the higher of two with one key fires.</summary>
    public static bool Move(int index, int delta)
    {
        List<HotkeyBinding> list = Copy();
        int to = index + delta;
        if ((uint)index >= (uint)list.Count || (uint)to >= (uint)list.Count) return false;
        (list[index], list[to]) = (list[to], list[index]);
        Commit(Current with { Bindings = list });
        return true;
    }

    private static int RemoveCombo(KeyCombo combo)
    {
        List<HotkeyBinding> list = Copy();
        int n = list.RemoveAll(b => b.Combo == combo);
        if (n > 0) Commit(Current with { Bindings = list });
        return n;
    }

    private static int SetComboEnabled(KeyCombo combo, bool on)
    {
        List<HotkeyBinding> list = Copy();
        int n = 0;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Combo == combo && list[i].Enabled != on) { list[i] = list[i] with { Enabled = on }; n++; }
        if (n > 0) Commit(Current with { Bindings = list });
        return n;
    }

    // ── Capture (Settings: "press a key") ──────────────────────────────────

    public static bool Capturing => _capturing;

    public static void BeginCapture()
    {
        _capturing = true;
        _captured = null;
        _captureCancelled = false;
        _captureNote = string.Empty;
        _captureStartedMs = Environment.TickCount64;
    }

    public static void CancelCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        _captureCancelled = true;
    }

    /// <summary>A key that couldn't be taken during the capture ("Numpad Enter can't be a hotkey").</summary>
    public static string CaptureNote => _captureNote;

    /// <summary>
    /// The Settings section, each frame while it waits: true once the capture ended, with the combo, or
    /// null when it was cancelled (Esc, timeout).
    /// </summary>
    public static bool TryTakeCapture(out KeyCombo? combo)
    {
        combo = null;
        if (_capturing && Environment.TickCount64 - _captureStartedMs > CaptureTimeoutMs)
        {
            _capturing = false;
            _captureCancelled = true;
        }
        if (_capturing) return false;
        if (_captured is { } c) { _captured = null; combo = c; return true; }
        if (_captureCancelled) { _captureCancelled = false; return true; }
        return false;
    }

    // ── Keys (Win32Backend.WndProcHook, AC's main thread) ──────────────────

    /// <summary>
    /// One key message. True: it belongs to a hotkey (or to a Settings capture) and must not reach AC
    /// or ImGui. <paramref name="rynthTextFocus"/>: a RynthCore text box has the keyboard.
    /// </summary>
    public static bool HandleKeyMessage(uint msg, IntPtr wParam, IntPtr lParam, bool rynthTextFocus)
    {
        try
        {
            switch (msg)
            {
                case WM_KILLFOCUS:
                    Tracker.Reset();
                    return false;
                case WM_CHAR:
                case WM_SYSCHAR:
                case WM_DEADCHAR:
                case WM_SYSDEADCHAR:
                    return Tracker.TakeChar();
                case WM_KEYUP:
                case WM_SYSKEYUP:
                    return Tracker.KeyUp(HotkeyMatch.NormalizeVk((int)wParam, HotkeyMatch.IsExtended(lParam.ToInt64())));
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    return KeyDown((int)wParam, lParam.ToInt64(), rynthTextFocus);
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            RynthLog.UI($"Hotkeys: key message threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool Down(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    private static HotkeyMods HeldMods() =>
        HotkeyMatch.Mods(Down(HotkeyKeys.VK_CONTROL), Down(HotkeyKeys.VK_MENU), Down(HotkeyKeys.VK_SHIFT));

    private static bool KeyDown(int rawVk, long lParam, bool rynthTextFocus)
    {
        int vk = HotkeyMatch.NormalizeVk(rawVk, HotkeyMatch.IsExtended(lParam));

        if (_capturing)
        {
            if (HotkeyKeys.IsModifierKey(vk)) return false;   // held for the combo; let it through
            Tracker.Took(vk);
            if (vk == HotkeyKeys.VK_ESCAPE) { CancelCapture(); return true; }
            if (vk == 0 || !HotkeyKeys.IsBindable(vk))
            {
                _captureNote = vk == 0 ? "Numpad Enter can't be a hotkey (AC keeps it)." : $"{HotkeyKeys.KeyName(vk)} can't be a hotkey.";
                return true;   // keep waiting
            }
            _captured = new KeyCombo(vk, HeldMods());
            _capturing = false;
            return true;
        }

        if (Tracker.IsEaten(vk))
        {
            Tracker.Took(vk);   // auto-repeat of a key a hotkey took: eat it and its char too
            return true;
        }
        Tracker.OtherKeyDown();
        if (vk == 0 || HotkeyKeys.IsModifierKey(vk) || !HotkeyMatch.IsFreshPress(lParam)) return false;

        HotkeyFile file = Current;
        if (!file.Enabled || file.Bindings.Count == 0) return false;
        var combo = new KeyCombo(vk, HeldMods());
        HotkeyBinding? b = HotkeyMatch.Find(file.Bindings, combo);
        if (b == null) return false;

        HotkeyGate gate = HotkeyRules.Check(ReadTypingState(rynthTextFocus), combo);
        // Every held-back press is logged (at most one line per HeldBackLogMs), not only a change
        // of reason: a stamina kit on a hotkey "rarely goes off" and the log showed one held-back
        // line, so the presses that followed for the same reason left no trace (report
        // R-20261106-040904-bb4b, 2026-11-05). AC's focus element says which text box had the keys.
        long now = Environment.TickCount64;
        if (gate != HotkeyGate.Fire && (gate != _lastGate || now - _lastHeldLogAt >= HeldBackLogMs))
        {
            _lastHeldLogAt = now;
            RynthLog.UI($"Hotkeys: {combo} held back - {HotkeyRules.Describe(gate)}."
                + (gate == HotkeyGate.AcTyping ? " " + AcKeyboardFocus.Describe() : ""));
        }
        _lastGate = gate;
        if (gate != HotkeyGate.Fire) return false;

        Tracker.Took(vk);
        RynthLog.UI($"Hotkeys: {combo} -> {HotkeyActions.Describe(b)}.");
        Run(b);
        return true;
    }

    private static TypingState ReadTypingState(bool rynthTextFocus) => new(
        InWorld: LoginLifecycleHooks.HasObservedLoginComplete,
        GameFocused: ForegroundIsOurs(),
        RynthTextFocus: rynthTextFocus,
        AcTextFocus: AcKeyboardFocus.HasTextFocus());

    private static bool ForegroundIsOurs()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == OurPid;
    }

    // ── Actions ────────────────────────────────────────────────────────────

    /// <summary>Runs a binding's action. Main thread (from the key); plugin work is posted to the pump.</summary>
    public static void Run(HotkeyBinding b)
    {
        if (b.IsCommand)
        {
            if (b.Command.Trim().Length == 0) return;
            // The player pressed it: a /rc command answers in chat, as typed.
            if (!AcMainThreadQueue.EnqueueChatCommand(b.Command.Trim(), ChatCommandOrigin.Player))
                Reply("Hotkey: the chat command queue is full; try again.");
            return;
        }

        HotkeyAction? a = HotkeyActions.Find(b.Action);
        if (a == null)
        {
            Reply($"Hotkey {b.Combo}: unknown action '{b.Action}' (edit it in Settings > Hotkeys).");
            return;
        }
        if (a.Panel != null)
        {
            string title = UI.PanelRouter.Canonical(a.Panel);
            // An ILT panel bound on Infinite Leaftide does nothing elsewhere, as the bar's ILT
            // button isn't there (it can still close one left open).
            if (a.Ilt && !HotkeyActions.Offered(a) && !ImGuiBackend.ImGuiPanelHost.IsOpen(title))
                Reply($"Hotkey {b.Combo}: {a.Panel} is an Infinite Leaftide feature (RynthILT), and this isn't Infinite Leaftide (/rc ilt for why).");
            else if (!ImGuiBackend.ImGuiPanelHost.HasFace(title))
                Reply($"Hotkey {b.Combo}: the {a.Panel} panel isn't available (its plugin isn't loaded, or the ImGui layer is off).");
            else
                UI.PanelRouter.Toggle(title);
            return;
        }

        if (a.Chat != null)
        {
            // VTank-style actions that are RynthAi /ra commands (the same as typing them).
            if (a.HasParam && b.Command.Trim().Length == 0)
            {
                Reply($"Hotkey {b.Combo}: '{a.Name}' has no {a.Param!.ToLowerInvariant()} (edit it in Settings > Hotkeys).");
                return;
            }
            if (!AcMainThreadQueue.EnqueueChatCommand(HotkeyActions.ChatFor(a, b), ChatCommandOrigin.Player))
                Reply("Hotkey: the chat command queue is full; try again.");
            return;
        }

        switch (a.Id)
        {
            case "macro": case "vt:startstop": UI.Data.RynthAiCommands.ToggleMacro(); break;
            case "combat": case "vt:combat": UI.Data.RynthAiCommands.ToggleSubsystem(0); break;
            case "buffing": case "vt:buffing": UI.Data.RynthAiCommands.ToggleSubsystem(1); break;
            case "navigation": case "vt:navigation": UI.Data.RynthAiCommands.ToggleSubsystem(2); break;
            case "looting": case "vt:looting": UI.Data.RynthAiCommands.ToggleSubsystem(3); break;
            case "meta": UI.Data.RynthAiCommands.ToggleSubsystem(4); break;
            case "forcerebuff": case "vt:forcebuff": UI.Data.RynthAiCommands.ForceRebuff(); break;
            case "vt:cancelforcebuff": UI.Data.RynthAiCommands.CancelForceRebuff(); break;
            case "patrol": UI.Data.RynthAiCommands.SendNavCommand("{\"Cmd\":\"dunPatrol\"}", refreshPatrol: true); break;
            case "autosort": UI.Data.RynthSortCommands.ToggleAuto(); break;
            case "reload":
                // As /rc rl and the bar's RL: signalled from a pool thread (the reload shuts this engine down).
                RynthLog.Compat("Hotkeys: engine reload requested.");
                EntryPoint.NoteManualReload();
                ThreadPool.UnsafeQueueUserWorkItem(static _ => EngineLifecycle.SignalReload(), null);
                break;
            default:
                Reply($"Hotkey {b.Combo}: '{a.Name}' isn't wired up in this engine.");
                break;
        }
    }

    private static void Reply(string text) => AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] " + text, 1);

    // ── /rc hotkey ─────────────────────────────────────────────────────────

    /// <summary>"/rc hotkey ..." (args after "hotkey"): the reply lines.</summary>
    public static List<string> HandleCommand(string args)
    {
        var lines = new List<string>();
        HotkeyCommand c = HotkeyCommandParser.Parse(args);
        HotkeyFile f = Current;
        switch (c.Verb)
        {
            case HotkeyVerb.Help:
                lines.Add(HotkeyCommandParser.Usage);
                break;
            case HotkeyVerb.Error:
                lines.Add(c.Error);
                break;
            case HotkeyVerb.List:
                if (f.Bindings.Count == 0) { lines.Add("No hotkeys. Add one in Settings > Hotkeys (/rc hotkeys) or with /rc hotkey add <combo> <action or /command>."); break; }
                lines.Add($"Hotkeys ({(f.Enabled ? "on" : "OFF: /rc hotkey on")}):");
                List<int> shadowed = HotkeyConflicts.Shadowed(f.Bindings);
                for (int i = 0; i < f.Bindings.Count; i++)
                {
                    HotkeyBinding b = f.Bindings[i];
                    lines.Add($"  {b.Combo} - {HotkeyActions.Describe(b)}{(b.Enabled ? "" : " (off)")}{(shadowed.Contains(i) ? " (never fires: an earlier hotkey has this key)" : "")}");
                }
                break;
            case HotkeyVerb.Actions:
                lines.Add("Built-in actions (or any chat command starting with /):");
                foreach (string group in new[] { "", HotkeyActions.VTankGroup })
                {
                    var sb = new System.Text.StringBuilder(group.Length > 0 ? "  " + group + ": " : "  ");
                    foreach (HotkeyAction a in HotkeyActions.BuiltIns)
                    {
                        if (a.Group != group || !HotkeyActions.Offered(a)) continue;
                        if (sb.Length > 150) { lines.Add(sb.ToString().TrimEnd(',', ' ')); sb.Clear().Append("  "); }
                        sb.Append(a.Id);
                        if (a.HasParam) sb.Append(" <").Append(a.Param!.ToLowerInvariant()).Append('>');
                        sb.Append(", ");
                    }
                    lines.Add(sb.ToString().TrimEnd(',', ' '));
                }
                break;
            case HotkeyVerb.Status:
                lines.Add($"Hotkeys {(f.Enabled ? "on" : "off")}, {f.Bindings.Count} bound, file {HotkeyStore.FilePath}.");
                lines.Add("Last held back: " + (_lastGate == HotkeyGate.Fire ? "nothing" : HotkeyRules.Describe(_lastGate)) + ".");
                lines.Add(AcKeyboardFocus.Describe());
                break;
            case HotkeyVerb.On:
            case HotkeyVerb.Off:
                SetMasterEnabled(c.Verb == HotkeyVerb.On);
                lines.Add(c.Verb == HotkeyVerb.On ? "Hotkeys on." : "Hotkeys off (bindings kept; /rc hotkey on turns them back on).");
                break;
            case HotkeyVerb.Add:
            {
                var b = new HotkeyBinding(c.Combo, c.Action, c.ChatCommand);
                List<string> warn = HotkeyConflicts.Warnings(f.Bindings, c.Combo);
                Add(b);
                lines.Add($"Hotkey added: {c.Combo} - {HotkeyActions.Describe(b)}.");
                foreach (string w in warn) lines.Add("Note: " + w);
                if (!f.Enabled) lines.Add("Hotkeys are off; /rc hotkey on turns them on.");
                break;
            }
            case HotkeyVerb.Remove:
            {
                int n = RemoveCombo(c.Combo);
                lines.Add(n == 0 ? $"No hotkey on {c.Combo}." : $"Removed {n} hotkey(s) on {c.Combo}.");
                break;
            }
            case HotkeyVerb.Enable:
            case HotkeyVerb.Disable:
            {
                bool on = c.Verb == HotkeyVerb.Enable;
                int n = SetComboEnabled(c.Combo, on);
                lines.Add(n == 0 ? $"No hotkey on {c.Combo} to switch {(on ? "on" : "off")}." : $"{c.Combo}: {(on ? "on" : "off")}.");
                break;
            }
        }
        return lines;
    }

    /// <summary>Engine shutdown: a capture in progress ends (the next generation starts clean).</summary>
    public static void Shutdown()
    {
        _capturing = false;
        _captured = null;
        // Not Tracker.Reset(): the tracker is AC's main thread only, and shutdown runs on the loader's
        // reload thread while the window procedure can still be in it. The tracker belongs to this
        // engine generation and goes with it; the next one starts with its own. (A reload died here,
        // a write through a bad pointer on the ReloadWatcher thread, 2026-10-20.)
    }
}
