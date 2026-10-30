// ============================================================================
//  RynthCore.Engine - Compatibility/RetailToolbarHooks.cs
//
//  "Hide retail toolbar": hides AC's toolbar panel (the combat-mode button, the
//  panel buttons, the selection's name and health, examine, the backpack, and
//  the two rows of shortcut slots under them; Tom 2026-10-22: bulky, and its
//  buttons are on keys anyway). Same shape as RetailVitalsHooks: capture the
//  UIElement, then EndSceneHook calls TickHide every frame, which calls
//  UIElement::SetVisible on it.
//
//  What the panel is (Ghidra symbols + disassembly of the 4,841,472-byte client):
//    gmToolbarUI (element class 0x10000007, 0x64C bytes) derives from
//    UIElement_Field like gmVitalsUI, so the UIElement is at offset 0 and the
//    NoticeHandler sits at +0x5F8 (its RecvNotice_* thunks: add ecx,-0x5F8).
//    What AC draws is gmFloatyToolbarUI (class 0x10000051), a movable window
//    that derives from gmToolbarUI and holds the gmToolbarUI with the buttons
//    and slots; hiding only that inner one leaves the panel on screen. So the
//    floaty window is what is hidden, with the base UIElement::SetVisible
//    called directly: the floaty's own override (0x004D0E00) also saves the
//    window's open state to the player's options, which must not change.
//
//  Capture, from two functions that run with the primary (UIElement) pointer:
//    gmToolbarUI::HandleSelectionChanged (0x004C0010, thiscall, no arguments):
//      every selection change (through RecvNotice_SelectionChanged's thunk).
//    gmToolbarUI::ListenToGlobalMessage (0x004BF170, thiscall, 2 arguments):
//      every key action broadcast to it (UIElementManager::BroadcastGlobalMessage
//      calls every registered listener, shown or not). This is where the
//      shortcut keys run (UseShortcut), so a hidden toolbar keeps them working.
//  And without waiting for either (a reload in the world, a bot that selects
//  nothing): TickHide looks for it in AC's UI tree by memory reads (SearchTree).
//  As for the vitals, an instance is only kept if its vtable's slot 0x18 is
//  UIElement::SetVisible (directly or through a one-jmp thunk).
//
//  Restore, freed instances, logoff and relog: exactly RetailVitalsHooks'.
//  Installed from PowerbarHooks.Initialize beside it. Both hook targets go
//  through HookResolver and are only hooked on a pattern match.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class RetailToolbarHooks
{
    // Fallback VAs (4,841,472-byte client). Pattern-scan is the source of truth.
    private const int SelectionChangedFallbackVa    = 0x004C0010;
    private const int ListenGlobalFallbackVa        = 0x004BF170;
    private const int UIElementSetVisibleFallbackVa = 0x00462390;

    // gmToolbarUI::HandleSelectionChanged(): 0x1C-byte frame, ebx = the selection global,
    // then the compare against its own last selection at +0x630. Unique in the text window
    // with the global's address wildcarded (checked with tools\pe_pattern.py).
    private static readonly byte?[] SelectionChangedPattern =
    [
        0x83, 0xEC, 0x1C,                     // sub esp, 0x1C
        0x53,                                 // push ebx
        0x8B, 0x1D, null, null, null, null,   // mov ebx, [selected id global]
        0x56,                                 // push esi
        0x8B, 0xF1,                           // mov esi, ecx
        0x39, 0x9E, 0x30, 0x06, 0x00, 0x00,   // cmp [esi+0x630], ebx
        0x89, 0x5C, 0x24, 0x0C,               // mov [esp+0Ch], ebx
        0x0F, 0x84                            // je rel32
    ];

    // gmToolbarUI::ListenToGlobalMessage(int message, int action): message 1 only, then
    // its quantity box's focus (+0x624) and the 0x27 action. Unique likewise; ret 8.
    private static readonly byte?[] ListenGlobalPattern =
    [
        0x83, 0x7C, 0x24, 0x04, 0x01,         // cmp dword [esp+4], 1
        0x56,                                 // push esi
        0x8B, 0xF1,                           // mov esi, ecx
        0x0F, 0x85, null, null, null, null,   // jne rel32
        0x8B, 0x8E, 0x24, 0x06, 0x00, 0x00,   // mov ecx, [esi+0x624]
        0xE8, null, null, null, null,         // call rel32 (UIElement::GetHasFocus)
        0x84, 0xC0,                           // test al, al
        0x74, null,                           // je rel8
        0x83, 0x7C, 0x24, 0x0C, 0x27          // cmp dword [esp+0Ch], 0x27
    ];

    // UIElement::SetVisible(bool): the pattern ChatHooks / RadarHooks / PowerbarHooks /
    // RetailVitalsHooks resolve; each consumer resolves it independently.
    private static readonly byte?[] UIElementSetVisiblePattern =
    [
        0x51, 0x53, 0x56, 0x57,
        0x8B, 0x3D, null, null, null, null,   // mov edi, ds:[imm32]
        0x8B, 0xF1,
        0xE8, null, null, null, null,         // call rel32
        0x8B, 0x9E, 0xA4, 0x00, 0x00, 0x00,
        0x88, 0x44, 0x24, 0x0F
    ];

    // UIRegion's own "visible" flag: bit 1 of the dword at +0xA4 (see RetailVitalsHooks).
    private const int RegionFlagsOffset = 0xA4;
    private const int RegionVisibleBit  = 0x2;
    private const int SetVisibleVtableSlot = 0x18;

    // AC builds the docked toolbar and its floaty twin; room for a stray third.
    private const int MaxInstances = 4;

    private struct Slot
    {
        public IntPtr Instance;
        public IntPtr Vtable;      // the instance's vtable when captured
        public bool HiddenByUs;    // TickHide turned it off; show it again when the option goes off
    }

    // Written by the detours and read by TickHide, all on AC's main thread only.
    private static readonly Slot[] Slots = new Slot[MaxInstances];

    // Set when a logoff is requested, cleared when the logout completes (as RetailVitalsHooks).
    private static volatile bool _logoffInProgress;

    private static IntPtr _uiElementSetVisibleAddress;
    private static IntPtr _originalSelectionChanged, _originalListenGlobal;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";
    private static int _captureLogs, _rejectLogs, _hideLogs;

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    // ── The setting ───────────────────────────────────────────────────────────

    private static readonly object SettingSync = new();
    private static volatile bool _hide;
    private static bool _settingLoaded;

    private static string SettingPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "retail_ui.txt");
    private const string HideToolbarKey = "HideRetailToolbar";

    /// <summary>
    /// The "Hide retail toolbar" option. Stored for the whole PC in
    /// %LOCALAPPDATA%\RynthCore\retail_ui.txt ("HideRetailToolbar=1"), beside "Hide retail
    /// vitals". Set from Settings > UI or /rc ui retailtoolbar. Any thread: it only flips a flag
    /// and writes the file; TickHide applies it on AC's main thread.
    /// </summary>
    public static bool HideRetailToolbar
    {
        get { EnsureSettingLoaded(); return _hide; }
        set
        {
            EnsureSettingLoaded();
            lock (SettingSync)
            {
                if (_hide == value) return;
                _hide = value;
                SaveSetting();
                if (!value) { _unhidePending = true; _unhideFallback = true; }
            }
            RynthLog.Compat($"RetailToolbarHooks: Hide retail toolbar -> {value} (installed={_hookInstalled}, captured={CountCaptured()}).");
        }
    }

    public static void EnsureSettingLoaded()
    {
        if (_settingLoaded) return;
        lock (SettingSync)
        {
            if (_settingLoaded) return;
            _settingLoaded = true;
            _hide = ReadSetting(out _settingWriteUtc);
            // Off at the start (a new generation, or another client switched it off): one toolbar shows,
            // whatever an earlier generation hid (it may not have noted it).
            if (!_hide) { _unhidePending = true; _unhideFallback = true; }
        }
    }

    private static bool ReadSetting(out DateTime writeUtc)
    {
        writeUtc = DateTime.MinValue;
        bool hide = false;
        try
        {
            string path = SettingPath;
            if (!File.Exists(path)) return false;
            writeUtc = File.GetLastWriteTimeUtc(path);
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0 || !line[..eq].Trim().Equals(HideToolbarKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(line.AsSpan(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    hide = v != 0;
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"RetailToolbarHooks: could not read {SettingPath}: {ex.GetType().Name}: {ex.Message}");
        }
        return hide;
    }

    // The file is the PC's: another client's change applies here too (checked every 2 s, main thread).
    private static DateTime _settingWriteUtc;
    private static long _nextSettingCheckMs;

    private static void FollowSettingFile()
    {
        long now = Environment.TickCount64;
        if (now < _nextSettingCheckMs) return;
        _nextSettingCheckMs = now + 2000;
        try
        {
            string path = SettingPath;
            DateTime w = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (w == _settingWriteUtc) return;
            bool hide = ReadSetting(out DateTime read);
            lock (SettingSync)
            {
                _settingWriteUtc = read;
                if (hide == _hide) return;
                _hide = hide;
                if (!hide) { _unhidePending = true; _unhideFallback = true; }
            }
            RynthLog.Compat($"RetailToolbarHooks: Hide retail toolbar -> {hide} (changed in another client).");
        }
        catch { }
    }

    // ── What this process hid, across engine generations ─────────────────────
    //
    // A hot reload starts with no memory of what the last generation hid, so the addresses of the
    // toolbar elements RynthCore hid are kept in the process's environment (it outlives every
    // engine generation, and no other process sees it). "Show the retail toolbar" shows those
    // again, whichever generation hid them.

    private const string HiddenEnv = "RYNTHCORE_RETAIL_TOOLBAR_HIDDEN";
    private static volatile bool _unhidePending, _unhideFallback;

    private static List<IntPtr> HiddenByUsBefore()
    {
        var list = new List<IntPtr>();
        string? v = Environment.GetEnvironmentVariable(HiddenEnv);
        if (string.IsNullOrEmpty(v)) return list;
        foreach (string part in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int a) && a != 0) list.Add(new IntPtr(a));
        return list;
    }

    private static void NoteHiddenByUs(IntPtr inst)
    {
        List<IntPtr> list = HiddenByUsBefore();
        if (list.Contains(inst)) return;
        list.Add(inst);
        Environment.SetEnvironmentVariable(HiddenEnv, string.Join(",", list.ConvertAll(p => p.ToInt32().ToString("X8", CultureInfo.InvariantCulture))));
    }

    private static bool IsToolbarElement(IntPtr inst)
    {
        if (!ClientObjectHooks.IsReadableSpan(inst, RegionFlagsOffset + 4)) return false;
        int t = TypeOf(inst);
        return t == FloatyToolbarType || (t == ToolbarType && IsSetVisibleSlot(Marshal.ReadIntPtr(inst)));
    }

    /// <summary>
    /// The setting went off: everything RynthCore hid (this generation or an earlier one) shows again.
    /// <paramref name="fallback"/> (it was just switched off): if that leaves no toolbar on screen (it was
    /// hidden before RynthCore kept track), the floaty toolbar window shows, else the docked one: one
    /// toolbar, never both twins. Main thread.
    /// </summary>
    private static void Unhide(IntPtr setVisible, bool fallback)
    {
        int shown = 0;
        var done = new HashSet<IntPtr>();
        void Show(IntPtr inst)
        {
            if (!done.Add(inst) || !IsToolbarElement(inst) || IsOwnVisible(inst)) return;
            try { ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)setVisible)(inst, 1); shown++; }
            catch { /* best-effort */ }
        }
        foreach (IntPtr inst in HiddenByUsBefore()) Show(inst);
        for (int i = 0; i < Slots.Length; i++)
        {
            if (Slots[i].HiddenByUs && Slots[i].Instance != IntPtr.Zero) Show(Slots[i].Instance);
            Slots[i].HiddenByUs = false;
        }
        Environment.SetEnvironmentVariable(HiddenEnv, null);

        bool anyVisible = false;
        IntPtr floaty = IntPtr.Zero, docked = IntPtr.Zero;
        for (int i = 0; i < Slots.Length; i++)
        {
            IntPtr inst = Slots[i].Instance;
            if (inst == IntPtr.Zero || !StillAlive(Slots[i])) continue;
            if (IsOwnVisible(inst)) anyVisible = true;
            int t = TypeOf(inst);
            if (t == FloatyToolbarType && floaty == IntPtr.Zero) floaty = inst;
            else if (t == ToolbarType && docked == IntPtr.Zero) docked = inst;
        }
        if (fallback && !anyVisible)
        {
            IntPtr pick = floaty != IntPtr.Zero ? floaty : docked;
            if (pick != IntPtr.Zero) Show(pick);
        }
        RynthLog.Compat($"RetailToolbarHooks: retail toolbar shown again ({shown} element(s); {CountCaptured()} held).");
    }

    // Caller holds SettingSync. Keeps every other key=value line in the file (HideRetailVitals ...).
    private static void SaveSetting()
    {
        try
        {
            string path = SettingPath;
            var lines = new List<string>();
            if (File.Exists(path))
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line[..eq].Trim().Equals(HideToolbarKey, StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.Trim().Length > 0) lines.Add(line);
                }
            }
            lines.Add($"{HideToolbarKey}={(_hide ? 1 : 0)}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"RetailToolbarHooks: could not save {SettingPath}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Install ───────────────────────────────────────────────────────────────

    public static void Initialize()
    {
        EnsureSettingLoaded();
        if (_hookInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        var setVisible = HookResolver.Resolve(textSection, "RetailToolbarHooks.UIElement::SetVisible",
            UIElementSetVisiblePattern, UIElementSetVisibleFallbackVa);
        if (!setVisible.Success)
        {
            _statusMessage = $"UIElement::SetVisible resolve failed ({setVisible.Detail}).";
            RynthLog.Compat($"RetailToolbarHooks: {_statusMessage} Not hooked.");
            return;
        }

        var selection = HookResolver.Resolve(textSection, "RetailToolbarHooks.gmToolbarUI::HandleSelectionChanged",
            SelectionChangedPattern, SelectionChangedFallbackVa);
        var listen = HookResolver.Resolve(textSection, "RetailToolbarHooks.gmToolbarUI::ListenToGlobalMessage",
            ListenGlobalPattern, ListenGlobalFallbackVa);

        // Only hook through a pattern match: a guessed address for a hook target is worse than no
        // "Hide retail toolbar" (as RetailVitalsHooks). Either capture alone is enough.
        bool selectionOk = selection.Success && selection.Source == HookResolver.ResolveSource.PatternScan;
        bool listenOk = listen.Success && listen.Source == HookResolver.ResolveSource.PatternScan;
        if (!selectionOk && !listenOk)
        {
            _statusMessage = $"no capture point by pattern (HandleSelectionChanged: {selection.Detail}; ListenToGlobalMessage: {listen.Detail}); not hooked.";
            RynthLog.Compat($"RetailToolbarHooks: {_statusMessage}");
            return;
        }

        _uiElementSetVisibleAddress = setVisible.Address;
        var hooked = new List<string>();
        if (selectionOk)
        {
            try
            {
                delegate* unmanaged[Thiscall]<IntPtr, void> detour = &SelectionChangedDetour;
                MinHook.Hook(selection.Address, (IntPtr)detour, out _originalSelectionChanged);
                hooked.Add($"HandleSelectionChanged @ 0x{selection.Address.ToInt32():X8}");
            }
            catch (Exception ex)
            {
                _originalSelectionChanged = IntPtr.Zero;
                RynthLog.Compat($"RetailToolbarHooks: HandleSelectionChanged hook threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (listenOk)
        {
            try
            {
                delegate* unmanaged[Thiscall]<IntPtr, int, int, void> detour = &ListenGlobalDetour;
                MinHook.Hook(listen.Address, (IntPtr)detour, out _originalListenGlobal);
                hooked.Add($"ListenToGlobalMessage @ 0x{listen.Address.ToInt32():X8}");
            }
            catch (Exception ex)
            {
                _originalListenGlobal = IntPtr.Zero;
                RynthLog.Compat($"RetailToolbarHooks: ListenToGlobalMessage hook threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        _hookInstalled = hooked.Count > 0;
        _statusMessage = _hookInstalled
            ? $"Hooked {string.Join(", ", hooked)}; SetVisible @ 0x{setVisible.Address.ToInt32():X8}."
            : "both hooks failed; not hooked.";
        RynthLog.Compat($"RetailToolbarHooks: {_statusMessage} Hide retail toolbar={_hide}.");
    }

    // ── Capture (AC's main thread; must never throw into AC) ─────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static void SelectionChangedDetour(IntPtr thisPtr)
    {
        try
        {
            RecursionGuard.Tick("RetailToolbarHooks.SelectionChanged");
            if (thisPtr != IntPtr.Zero && !_logoffInProgress && MainThreadGuard.IsOnMainThread())
                CaptureWithWrapper(thisPtr, "selection");
        }
        catch { }
        ((delegate* unmanaged[Thiscall]<IntPtr, void>)_originalSelectionChanged)(thisPtr);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static void ListenGlobalDetour(IntPtr thisPtr, int message, int action)
    {
        try
        {
            RecursionGuard.Tick("RetailToolbarHooks.ListenGlobal");
            if (thisPtr != IntPtr.Zero && !_logoffInProgress && MainThreadGuard.IsOnMainThread())
                CaptureWithWrapper(thisPtr, "key action");
        }
        catch { }
        ((delegate* unmanaged[Thiscall]<IntPtr, int, int, void>)_originalListenGlobal)(thisPtr, message, action);
    }

    private static void Capture(IntPtr inst, string via)
    {
        int free = -1;
        for (int i = 0; i < Slots.Length; i++)
        {
            if (Slots[i].Instance == inst) return;   // already have it
            if (free < 0 && Slots[i].Instance == IntPtr.Zero) free = i;
        }

        if (!ClientObjectHooks.IsReadableSpan(inst, RegionFlagsOffset + 4))
        {
            Reject(inst, "object not readable");
            return;
        }
        IntPtr vtable = Marshal.ReadIntPtr(inst);
        // The floaty toolbar overrides SetVisible (it saves the window's open state to the player's
        // options after the base call); TickHide calls the base UIElement::SetVisible directly, so
        // that one is accepted by its type instead and nothing is ever saved.
        if (!IsSetVisibleSlot(vtable) && TypeOf(inst) != FloatyToolbarType)
        {
            Reject(inst, $"vtable 0x{vtable.ToInt32():X8} slot 0x{SetVisibleVtableSlot:X} is not UIElement::SetVisible");
            return;
        }

        if (free < 0)
        {
            // Full: reuse a slot whose object is gone. With none, keep the ones we have.
            for (int i = 0; i < Slots.Length; i++)
            {
                if (!StillAlive(Slots[i])) { free = i; break; }
            }
            if (free < 0)
            {
                Reject(inst, "all slots hold live instances");
                return;
            }
        }

        Slots[free] = new Slot { Instance = inst, Vtable = vtable, HiddenByUs = false };
        if (++_captureLogs <= 8)
            RynthLog.Compat($"RetailToolbarHooks: captured toolbar element 0x{inst.ToInt32():X8} by {via} (vtable 0x{vtable.ToInt32():X8}, visible={IsOwnVisible(inst)}).");
    }

    // gmFloatyToolbarUI (element class 0x10000051, registered by gmFloatyToolbarUI::Register
    // 0x004D0450): the movable window AC actually draws, holding the gmToolbarUI (0x10000007)
    // with the buttons and slots. Hiding only the inner one leaves the panel on screen
    // (2026-10-22, the test client), so the wrapper is hidden too.
    private const int ToolbarType = 0x10000007, FloatyToolbarType = 0x10000051;
    private const int VtGetUIElementType = 0x98;

    /// <summary>
    /// The element's class id, read (not called) from its GetUIElementType, which in these classes
    /// is "mov eax, imm32; ret" (gmToolbarUI 0x004BE480, gmFloatyToolbarUI 0x004D01D0). 0 otherwise.
    /// </summary>
    internal static int TypeOf(IntPtr inst)
    {
        if (!ClientObjectHooks.IsReadablePointer(inst)) return 0;
        IntPtr vtable = Marshal.ReadIntPtr(inst);
        if (!ClientObjectHooks.IsReadableSpan(vtable, VtGetUIElementType + 4)) return 0;
        IntPtr fn = Marshal.ReadIntPtr(vtable, VtGetUIElementType);
        if (!ClientObjectHooks.IsReadableSpan(fn, 6)) return 0;
        if (Marshal.ReadByte(fn) != 0xB8 || Marshal.ReadByte(fn, 5) != 0xC3) return 0;
        return Marshal.ReadInt32(fn, 1);
    }

    /// <summary>
    /// The toolbar to hide: the floaty window around it when there is one (a few levels up at
    /// most), else the toolbar itself. Inside a floaty window the toolbar is never hidden (AC opens
    /// and closes the window, not its content); one found hidden there (an earlier build hid it) is
    /// shown again, so the window is whole whenever it shows.
    /// </summary>
    private static void CaptureWithWrapper(IntPtr inst, string via)
    {
        if (TypeOf(inst) == ToolbarType) NoteInner(inst);
        IntPtr p = inst;
        for (int d = 0; d < 4; d++)
        {
            if (!ClientObjectHooks.IsReadableSpan(p, ParentOffset + 4)) break;
            p = Marshal.ReadIntPtr(p, ParentOffset);
            if (p == IntPtr.Zero) break;
            if (TypeOf(p) != FloatyToolbarType) continue;
            Capture(p, via + " (its floaty window)");
            ShowInner(inst);
            return;
        }
        Capture(inst, via);
    }

    /// <summary>The toolbar inside a floaty window: visible again if its own flag is off. Main thread.</summary>
    private static void ShowInner(IntPtr inner)
    {
        IntPtr setVisible = _uiElementSetVisibleAddress;
        if (setVisible == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(inner, RegionFlagsOffset + 4) || IsOwnVisible(inner)) return;
        if (!IsSetVisibleSlot(Marshal.ReadIntPtr(inner))) return;
        try { ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)setVisible)(inner, 1); }
        catch { /* best-effort */ }
        RynthLog.Compat($"RetailToolbarHooks: showed the toolbar 0x{inner.ToInt32():X8} inside its floaty window again (visible={IsOwnVisible(inner)}).");
    }

    private static void Reject(IntPtr inst, string why)
    {
        if (++_rejectLogs <= 8)
            RynthLog.Compat($"RetailToolbarHooks: not capturing 0x{inst.ToInt32():X8}: {why}.");
    }

    /// <summary>
    /// The vtable's SetVisible slot must be UIElement::SetVisible itself, or a one-instruction
    /// "jmp rel32" thunk to it (incremental-link thunks, as the floaty classes have).
    /// </summary>
    private static bool IsSetVisibleSlot(IntPtr vtable)
    {
        IntPtr setVisible = _uiElementSetVisibleAddress;
        if (setVisible == IntPtr.Zero) return false;
        if (!ClientObjectHooks.IsReadableSpan(vtable, SetVisibleVtableSlot + 4)) return false;
        IntPtr fn = Marshal.ReadIntPtr(vtable, SetVisibleVtableSlot);
        if (fn == setVisible) return true;
        if (!ClientObjectHooks.IsReadableSpan(fn, 5)) return false;
        if (Marshal.ReadByte(fn) != 0xE9) return false;
        int rel = Marshal.ReadInt32(fn, 1);
        return unchecked(fn.ToInt32() + 5 + rel) == setVisible.ToInt32();
    }

    // ── Per-frame visibility (EndSceneHook, AC's main thread) ─────────────────

    public static void TickHide()
    {
        if (!_hookInstalled) return;
        if (!LoginLifecycleHooks.HasObservedLoginComplete) return;
        if (_logoffInProgress) return;
        if (!MainThreadGuard.IsOnMainThread()) return;
        IntPtr setVisible = _uiElementSetVisibleAddress;
        if (setVisible == IntPtr.Zero) return;

        FollowSettingFile();
        bool hide = _hide;
        if (!hide && _unhidePending)
        {
            _unhidePending = false;
            bool fallback = _unhideFallback;
            _unhideFallback = false;
            try
            {
                if (CountCaptured() == 0) SearchTree();
                Unhide(setVisible, fallback);
            }
            catch (Exception ex) { RynthLog.Compat($"RetailToolbarHooks: showing the toolbar again threw {ex.GetType().Name}: {ex.Message}"); }
            return;
        }
        if (hide)
        {
            long now = Environment.TickCount64;
            if (now >= _nextSearchMs)
            {
                _nextSearchMs = now + (CountCaptured() == 0 ? 2000 : 20000);
                try { SearchTree(); }
                catch (Exception ex) { if (++_rejectLogs <= 8) RynthLog.Compat($"RetailToolbarHooks: tree search threw {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        for (int i = 0; i < Slots.Length; i++)
        {
            IntPtr inst = Slots[i].Instance;
            if (inst == IntPtr.Zero) continue;
            if (!StillAlive(Slots[i]))
            {
                RynthLog.Compat($"RetailToolbarHooks: toolbar element 0x{inst.ToInt32():X8} is gone (freed); dropped until it is captured again.");
                Slots[i] = default;
                continue;
            }

            bool visible = IsOwnVisible(inst);
            if (hide)
            {
                // Only the one AC is showing; its hidden twin (docked vs floaty) stays AC's business.
                if (visible)
                {
                    try { ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)setVisible)(inst, 0); }
                    catch { /* best-effort */ }
                    if (!Slots[i].HiddenByUs && ++_hideLogs <= 8)
                        RynthLog.Compat($"RetailToolbarHooks: hid toolbar element 0x{inst.ToInt32():X8} (now visible={IsOwnVisible(inst)}).");
                    Slots[i].HiddenByUs = true;
                    NoteHiddenByUs(inst);
                }
            }
            else if (Slots[i].HiddenByUs)
            {
                if (!visible)
                {
                    try { ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)setVisible)(inst, 1); }
                    catch { /* best-effort */ }
                }
                Slots[i].HiddenByUs = false;
            }
        }
    }

    private static bool IsOwnVisible(IntPtr inst) =>
        (Marshal.ReadInt32(inst, RegionFlagsOffset) & RegionVisibleBit) != 0;

    // ── Finding the toolbar without waiting ───────────────────────────────────
    //
    // After a reload in the world nothing may select anything or press a key for a long time
    // (a bot fighting: combat is selection-free), so the detours alone could leave the panel up.
    // TickHide then looks for it in AC's UI tree, by memory reads only (no calls into AC): from
    // an element the engine already holds (the chat, the radar) up to the top, then through every
    // descendant for the toolbar's Use button (element 0x1000019D); the toolbar is the ancestor
    // that keeps that button at +0x60C (gmToolbarUI::PostInit stores it there). Both twins are
    // found that way. Layout (UIElement::GetParentconst, GetChildconst, GetRootElement):
    //   +0xAC parent; +0x120 child count, +0x124 the first child's list node + 8 (node +0x08 the
    //   next node + 8, node +0x10 the child); +0x2E4 the element id; +0x554 bit 21 a root.

    private const int ParentOffset = 0xAC, ChildCountOffset = 0x120, ChildHeadOffset = 0x124,
        ElementIdOffset = 0x2E4, RootFlagsOffset = 0x554, RootFlagBit = 1 << 21, UseButtonFieldOffset = 0x60C;
    private const int UseButtonId = 0x1000019D;
    private const int MaxVisited = 60_000, MaxDepth = 48;
    private static long _nextSearchMs;

    // The UI manager (UiElementHooks.Manager, `this` of its per-frame tooltip detour): +0x9C the
    // root; +0x14 an array of +0x1C elements that UIElementManager::GetElement (0x00459A00) scans
    // by id: the top-level windows, the floaty ones (each its own root) among them.
    private const int MgrRoot = 0x9C, MgrElements = 0x14, MgrElementCount = 0x1C, MaxRegistered = 200_000;
    private static int _searchLogs;

    private static void SearchTree()
    {
        // One page query per page for the whole search (AC isn't running: EndScene, main thread).
        using var probeScope = MemoryProbe.BeginWalk();
        int before = CountCaptured(), buttons = 0, registered = -1, visited = 0, nRoots = 0;

        // First the manager's element array: every Use button in it, whichever window holds it.
        IntPtr mgr = UiElementHooks.Manager;
        if (mgr != IntPtr.Zero && ClientObjectHooks.IsReadableSpan(mgr, MgrRoot + 4))
        {
            int count = Marshal.ReadInt32(mgr, MgrElementCount);
            IntPtr arr = Marshal.ReadIntPtr(mgr, MgrElements);
            if (count > 0 && count <= MaxRegistered && ClientObjectHooks.IsReadableSpan(arr, count * IntPtr.Size))
            {
                registered = count;
                for (int i = 0; i < count; i++)
                {
                    IntPtr e = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
                    if (e == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(e, ElementIdOffset + 4)) continue;
                    if (Marshal.ReadInt32(e, ElementIdOffset) != UseButtonId) continue;
                    buttons++;
                    CaptureOwnerOf(e);
                }
            }
        }

        // Then, if that found none, the UI tree from the roots the engine can reach.
        if (buttons == 0)
        {
            Span<IntPtr> roots = stackalloc IntPtr[8];
            void AddRoot(IntPtr e, Span<IntPtr> list, ref int n)
            {
                IntPtr top = TopOf(e);
                if (top == IntPtr.Zero || n >= list.Length) return;
                for (int i = 0; i < n; i++) if (list[i] == top) return;
                list[n++] = top;
            }
            if (mgr != IntPtr.Zero && ClientObjectHooks.IsReadableSpan(mgr, MgrRoot + 4))
                AddRoot(Marshal.ReadIntPtr(mgr, MgrRoot), roots, ref nRoots);
            AddRoot(ChatHooks.GmMainChatInstance, roots, ref nRoots);
            AddRoot(RadarHooks.GmRadarUIInstance, roots, ref nRoots);
            for (int i = 0; i < nRoots; i++) Walk(roots[i], 0, ref visited);
        }

        if (++_searchLogs <= 4 || CountCaptured() != before)
            RynthLog.Compat($"RetailToolbarHooks: search: manager {(mgr == IntPtr.Zero ? "not seen" : "seen")}, {registered} registered element(s), " +
                            $"{buttons} Use button(s); tree {nRoots} root(s), {visited} element(s); {CountCaptured()} toolbar element(s) held.");
    }

    /// <summary>The element's root (or its topmost readable ancestor); zero when it can't be read.</summary>
    private static IntPtr TopOf(IntPtr e)
    {
        for (int depth = 0; depth < MaxDepth && e != IntPtr.Zero; depth++)
        {
            if (!ClientObjectHooks.IsReadableSpan(e, RootFlagsOffset + 4)) return IntPtr.Zero;
            if ((Marshal.ReadInt32(e, RootFlagsOffset) & RootFlagBit) != 0) return e;
            IntPtr parent = Marshal.ReadIntPtr(e, ParentOffset);
            if (parent == IntPtr.Zero) return e;
            e = parent;
        }
        return IntPtr.Zero;
    }

    private static void Walk(IntPtr e, int depth, ref int visited)
    {
        if (depth > MaxDepth || ++visited > MaxVisited) return;
        if (!ClientObjectHooks.IsReadableSpan(e, ElementIdOffset + 4)) return;
        if (Marshal.ReadInt32(e, ElementIdOffset) == UseButtonId) CaptureOwnerOf(e);
        if (Marshal.ReadInt32(e, ChildCountOffset) == 0) return;
        IntPtr head = Marshal.ReadIntPtr(e, ChildHeadOffset);
        IntPtr node = head == IntPtr.Zero ? IntPtr.Zero : head - 8;
        for (int n = 0; node != IntPtr.Zero && n < MaxVisited; n++)
        {
            if (!ClientObjectHooks.IsReadableSpan(node, 0x14)) return;
            IntPtr child = Marshal.ReadIntPtr(node, 0x10);
            if (child != IntPtr.Zero) Walk(child, depth + 1, ref visited);
            if (visited > MaxVisited) return;
            IntPtr next = Marshal.ReadIntPtr(node, 8);
            node = next == IntPtr.Zero ? IntPtr.Zero : next - 8;
        }
    }

    /// <summary>The Use button's toolbar: the ancestor holding it at +0x60C (a few levels up at most).</summary>
    private static void CaptureOwnerOf(IntPtr button)
    {
        if (!ClientObjectHooks.IsReadableSpan(button, ParentOffset + 4)) return;
        IntPtr a = Marshal.ReadIntPtr(button, ParentOffset);
        for (int d = 0; d < 8 && a != IntPtr.Zero; d++)
        {
            if (!ClientObjectHooks.IsReadableSpan(a, UseButtonFieldOffset + 4)) return;
            if (Marshal.ReadIntPtr(a, UseButtonFieldOffset) == button)
            {
                NoteInner(a);
                CaptureWithWrapper(a, "the UI tree");   // held either way: hidden, or shown again (Unhide)
                return;
            }
            a = Marshal.ReadIntPtr(a, ParentOffset);
        }
    }

    // ── The toolbar with the shortcut slots (AcShortcuts) ─────────────────────
    //
    // The gmToolbarUI that owns the Use button also owns the 18 shortcut slots' item lists
    // (+0x634, count +0x63C): AcShortcuts calls its UseShortcut / AddShortcut /
    // RemoveShortcutInSlotNum, so a slot used or changed from RynthCore goes exactly the way
    // the retail toolbar's own do (the server, the player's shortcut list, the retail slots).
    // Found the same way (the element registry), whether the retail toolbar is hidden or not.

    private static IntPtr _inner, _innerVtable;
    private static long _nextInnerSearchMs;

    private static void NoteInner(IntPtr toolbar)
    {
        if (!ClientObjectHooks.IsReadablePointer(toolbar)) return;
        if (_inner != toolbar && ++_captureLogs <= 8)
            RynthLog.Compat($"RetailToolbarHooks: the shortcut toolbar is 0x{toolbar.ToInt32():X8}.");
        _inner = toolbar;
        _innerVtable = Marshal.ReadIntPtr(toolbar);
    }

    /// <summary>
    /// The gmToolbarUI holding the shortcut slots, or zero (not in the world, not found yet:
    /// it is looked for at most every 2 s). AC's main thread.
    /// </summary>
    public static IntPtr ShortcutToolbar()
    {
        if (!MainThreadGuard.IsOnMainThread() || !LoginLifecycleHooks.HasObservedLoginComplete || _logoffInProgress)
            return IntPtr.Zero;
        IntPtr t = _inner;
        if (t != IntPtr.Zero && ClientObjectHooks.IsReadableSpan(t, 0x640) && Marshal.ReadIntPtr(t) == _innerVtable)
            return t;
        _inner = IntPtr.Zero;
        long now = Environment.TickCount64;
        if (now < _nextInnerSearchMs) return IntPtr.Zero;
        _nextInnerSearchMs = now + 2000;
        try { SearchTree(); }
        catch { return IntPtr.Zero; }
        return _inner;
    }

    /// <summary>Still readable and still starting with the vtable it had when captured (see RetailVitalsHooks).</summary>
    private static bool StillAlive(in Slot s)
    {
        if (s.Instance == IntPtr.Zero) return false;
        if (!ClientObjectHooks.IsReadableSpan(s.Instance, RegionFlagsOffset + 4)) return false;
        return Marshal.ReadIntPtr(s.Instance) == s.Vtable;
    }

    private static int CountCaptured()
    {
        int n = 0;
        for (int i = 0; i < Slots.Length; i++)
            if (Slots[i].Instance != IntPtr.Zero) n++;
        return n;
    }

    /// <summary>
    /// /rc ui retailtoolbar status: into the log, each held toolbar's parents up to the root and its
    /// direct children (id, own visible flag, vtable, child count). AC's main thread.
    /// </summary>
    public static void LogStructure()
    {
        if (!MainThreadGuard.IsOnMainThread()) return;
        using var probeScope = MemoryProbe.BeginWalk();
        for (int s = 0; s < Slots.Length; s++)
        {
            IntPtr inst = Slots[s].Instance;
            if (inst == IntPtr.Zero || !StillAlive(Slots[s])) continue;
            var line = new System.Text.StringBuilder($"RetailToolbarHooks: toolbar 0x{inst.ToInt32():X8} parents:");
            IntPtr e = inst;
            for (int d = 0; d < 12 && e != IntPtr.Zero; d++)
            {
                if (!ClientObjectHooks.IsReadableSpan(e, RootFlagsOffset + 4)) break;
                line.Append($" [0x{e.ToInt32():X8} id=0x{Marshal.ReadInt32(e, ElementIdOffset):X8} vis={IsOwnVisible(e)} vt=0x{Marshal.ReadInt32(e):X8} kids={Marshal.ReadInt32(e, ChildCountOffset)}{((Marshal.ReadInt32(e, RootFlagsOffset) & RootFlagBit) != 0 ? " root" : "")}]");
                e = Marshal.ReadIntPtr(e, ParentOffset);
            }
            RynthLog.Compat(line.ToString());
            var kids = new System.Text.StringBuilder($"RetailToolbarHooks: toolbar 0x{inst.ToInt32():X8} children:");
            IntPtr head = Marshal.ReadIntPtr(inst, ChildHeadOffset);
            IntPtr node = head == IntPtr.Zero ? IntPtr.Zero : head - 8;
            for (int n = 0; node != IntPtr.Zero && n < 64; n++)
            {
                if (!ClientObjectHooks.IsReadableSpan(node, 0x14)) break;
                IntPtr child = Marshal.ReadIntPtr(node, 0x10);
                if (child != IntPtr.Zero && ClientObjectHooks.IsReadableSpan(child, RootFlagsOffset + 4))
                    kids.Append($" [id=0x{Marshal.ReadInt32(child, ElementIdOffset):X8} vis={IsOwnVisible(child)} kids={Marshal.ReadInt32(child, ChildCountOffset)}]");
                IntPtr next = Marshal.ReadIntPtr(node, 8);
                node = next == IntPtr.Zero ? IntPtr.Zero : next - 8;
            }
            RynthLog.Compat(kids.ToString());
        }
    }

    /// <summary>One line for /rc ui retailtoolbar.</summary>
    public static string Describe()
    {
        int hidden = 0;
        for (int i = 0; i < Slots.Length; i++)
            if (Slots[i].Instance != IntPtr.Zero && Slots[i].HiddenByUs) hidden++;
        string state = !_hookInstalled
            ? $"not available ({_statusMessage})"
            : CountCaptured() == 0
                ? "the toolbar isn't found yet (it is looked for every 2 s while this is on)"
                : $"{CountCaptured()} toolbar element(s) found, {hidden} hidden by RynthCore";
        return $"Hide retail toolbar is {(HideRetailToolbar ? "ON" : "OFF")} - {state}. Its shortcut keys keep working.";
    }

    // ── Logout / relog ────────────────────────────────────────────────────────

    /// <summary>A logoff was requested (LogoffOriginProbe): drop the elements now, before AC frees them.</summary>
    public static void OnLogoffRequested()
    {
        _logoffInProgress = true;
        for (int i = 0; i < Slots.Length; i++) Slots[i] = default;
        _inner = IntPtr.Zero;
    }

    /// <summary>The logout completed (PluginManager's logout dispatch): the next login builds new ones.</summary>
    public static void ResetCachedInstance()
    {
        for (int i = 0; i < Slots.Length; i++) Slots[i] = default;
        _logoffInProgress = false;
        _nextSearchMs = 0;
        _inner = IntPtr.Zero;
        _nextInnerSearchMs = 0;
    }
}
