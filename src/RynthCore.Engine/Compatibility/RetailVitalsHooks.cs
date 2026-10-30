// ============================================================================
//  RynthCore.Engine - Compatibility/RetailVitalsHooks.cs
//
//  "Hide retail vitals": hides AC's own health/stamina/mana bars, now that the
//  RynthVision player plate and the RynthAi dashboard bars replace them. Same
//  shape as ChatHooks / RadarHooks: capture the UIElement, then EndSceneHook
//  calls TickHide every frame, which calls UIElement::SetVisible on it.
//
//  What the bars are (Ghidra symbols + disassembly of the 4,841,472-byte client):
//    gmVitalsUI (element class 0x10000009, 0x618 bytes) derives from
//    UIElement_Field, so the UIElement is at offset 0; NoticeHandler sits at
//    +0x5F8 and the QualityChangeHandler at +0x5FC. Two subclasses carry the
//    bars on screen: gmFloatyVitalsUI (0x1000004D, stacked) and
//    gmFloatySideVitalsUI (0x10000056, side by side). Both exist at once and AC
//    shows one and hides the other with UIElement::SetVisible (vtable slot 0x18),
//    per the "Side by side vitals" option (gmGamePlayUI::
//    RecvNotice_PlayerOptionChanged, 0x004EAA30).
//
//  Capture: gmVitalsUI::Update (0x004C0AB0), thiscall with no arguments. It is
//  only reached through two thunks, OnQualityChanged (add ecx,-0x5FC) and
//  RecvNotice_PlayerDescReceived (add ecx,-0x5F8), so its 'this' is always the
//  primary (UIElement) pointer - unlike gmPowerbarUI's RecvNotice_* hooks, whose
//  'this' was the NoticeHandler sub-object and crashed SetVisible
//  (PowerbarHooks.TickHide). It runs at login (player description) and on every
//  health/stamina/mana change, for every gmVitalsUI-family instance.
//  As a second check, an instance is only kept if its vtable's slot 0x18 is
//  UIElement::SetVisible (directly or through a one-jmp thunk): exactly the
//  function TickHide calls, and the one AC itself calls on these objects.
//
//  Restore: TickHide hides only instances whose own visible flag is set, and
//  remembers which ones it hid; with the option off it shows exactly those
//  again, once. The hidden sibling (stacked vs side by side) is never shown.
//
//  Freed instances: dropped when a logoff is requested (LogoffOriginProbe) and
//  when the logout completes (PluginManager), as the chat and radar are; and
//  every tick checks the object is still readable and still has the vtable it
//  had when captured (MSVC destructors reset the vtable to the base class's,
//  so a destroyed gmFloaty*VitalsUI fails the check).
//
//  Installed from PowerbarHooks.Initialize (the "powerbar hooks" init step)
//  because EntryPoint.cs is not edited for this. Both addresses go through
//  HookResolver: pattern first, fallback VA second.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class RetailVitalsHooks
{
    // Fallback VAs (4,841,472-byte client). Pattern-scan is the source of truth.
    private const int GmVitalsUIUpdateFallbackVa     = 0x004C0AB0;
    private const int UIElementSetVisibleFallbackVa  = 0x00462390;

    // gmVitalsUI::Update() - 0xA4-byte frame, then InterfaceSystem::GetInstance for
    // the PlayerDesc (push imm32 = its interface id). Unique in the text window
    // (checked with tools\pe_pattern.py; the first 11 bytes alone are unique).
    private static readonly byte?[] UpdatePattern =
    [
        0x81, 0xEC, 0xA4, 0x00, 0x00, 0x00,   // sub esp, 0xA4
        0x53, 0x55, 0x56, 0x57,               // push ebx/ebp/esi/edi
        0x8D, 0x44, 0x24, 0x10, 0x50,         // lea eax,[esp+10h]; push eax
        0x8B, 0xF1,                           // mov esi, ecx
        0x8D, 0x4C, 0x24, 0x18,               // lea ecx,[esp+18h]
        0x68, null, null, null, null,         // push imm32 (interface id)
        0x33, 0xFF, 0x51,                     // xor edi,edi; push ecx
        0x89, 0x7C, 0x24, 0x1C,               // mov [esp+1Ch], edi
        0xE8, null, null, null, null,         // call rel32 (InterfaceSystem::GetInstance)
        0x8B, 0xC8,                           // mov ecx, eax
        0xE8, null, null, null, null          // call rel32 (InterfaceSystem::GetClass)
    ];

    // UIElement::SetVisible(bool) - the same pattern ChatHooks / RadarHooks /
    // PowerbarHooks resolve; each consumer resolves it independently.
    private static readonly byte?[] UIElementSetVisiblePattern =
    [
        0x51, 0x53, 0x56, 0x57,
        0x8B, 0x3D, null, null, null, null,   // mov edi, ds:[imm32]
        0x8B, 0xF1,
        0xE8, null, null, null, null,         // call rel32
        0x8B, 0x9E, 0xA4, 0x00, 0x00, 0x00,
        0x88, 0x44, 0x24, 0x0F
    ];

    // UIRegion's own "visible" flag: bit 1 of the dword at +0xA4. UIRegion::SetVisible
    // writes it and UIElement::SetVisible reads it before and after; reading it here
    // has no side effects (UIElement::IsVisible walks the parents and calls virtuals).
    private const int RegionFlagsOffset = 0xA4;
    private const int RegionVisibleBit  = 0x2;
    private const int SetVisibleVtableSlot = 0x18;

    // Up to four gmVitalsUI-family objects at once (AC builds two: stacked + side by side).
    private const int MaxInstances = 4;

    private struct Slot
    {
        public IntPtr Instance;
        public IntPtr Vtable;      // the instance's vtable when captured
        public bool HiddenByUs;    // TickHide turned it off; show it again when the option goes off
    }

    // Written by UpdateDetour and read by TickHide, both on AC's main thread only.
    private static readonly Slot[] Slots = new Slot[MaxInstances];

    // Set when a logoff is requested, cleared when the logout completes: AC frees the
    // vitals UI during the logoff and Update can still fire on a dying one, so nothing
    // is captured in between (as ChatHooks).
    private static volatile bool _logoffInProgress;

    private static IntPtr _uiElementSetVisibleAddress;
    private static IntPtr _originalUpdate;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";
    private static int _updateFires;
    private static int _rejectLogs;

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    // ── The setting ───────────────────────────────────────────────────────────

    private static readonly object SettingSync = new();
    private static volatile bool _hide;
    private static bool _settingLoaded;

    private static string SettingPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "retail_ui.txt");
    private const string HideVitalsKey = "HideRetailVitals";

    /// <summary>
    /// The "Hide retail vitals" option. Stored for the whole PC in
    /// %LOCALAPPDATA%\RynthCore\retail_ui.txt ("HideRetailVitals=1"), beside the
    /// engine's other in-client UI settings (panel_text_size.txt,
    /// nameplate_settings.txt). Set from Settings > UI or /rc ui retailvitals.
    /// Any thread: it only flips a flag and writes the file; TickHide applies it
    /// on AC's main thread.
    /// </summary>
    public static bool HideRetailVitals
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
            }
            RynthLog.Compat($"RetailVitalsHooks: Hide retail vitals -> {value} (installed={_hookInstalled}, captured={CountCaptured()}).");
        }
    }

    public static void EnsureSettingLoaded()
    {
        if (_settingLoaded) return;
        lock (SettingSync)
        {
            if (_settingLoaded) return;
            _settingLoaded = true;
            try
            {
                string path = SettingPath;
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || !line[..eq].Trim().Equals(HideVitalsKey, StringComparison.OrdinalIgnoreCase)) continue;
                    if (int.TryParse(line.AsSpan(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                        _hide = v != 0;
                }
            }
            catch (Exception ex)
            {
                RynthLog.Compat($"RetailVitalsHooks: could not read {SettingPath}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Caller holds SettingSync. Keeps any other key=value lines in the file.
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
                    if (eq > 0 && line[..eq].Trim().Equals(HideVitalsKey, StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.Trim().Length > 0) lines.Add(line);
                }
            }
            lines.Add($"{HideVitalsKey}={(_hide ? 1 : 0)}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"RetailVitalsHooks: could not save {SettingPath}: {ex.GetType().Name}: {ex.Message}");
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

        var setVisible = HookResolver.Resolve(textSection, "RetailVitalsHooks.UIElement::SetVisible",
            UIElementSetVisiblePattern, UIElementSetVisibleFallbackVa);
        if (!setVisible.Success)
        {
            _statusMessage = $"UIElement::SetVisible resolve failed ({setVisible.Detail}).";
            RynthLog.Compat($"RetailVitalsHooks: {_statusMessage} Not hooked.");
            return;
        }

        var update = HookResolver.Resolve(textSection, "RetailVitalsHooks.gmVitalsUI::Update",
            UpdatePattern, GmVitalsUIUpdateFallbackVa);
        if (!update.Success)
        {
            _statusMessage = $"gmVitalsUI::Update resolve failed ({update.Detail}).";
            RynthLog.Compat($"RetailVitalsHooks: {_statusMessage} Not hooked.");
            return;
        }

        // Only hook through a pattern match: a guessed address for a hook target is
        // worse than no "Hide retail vitals" (the fallback VA is logged as RISKY and
        // refused here; the option then does nothing).
        if (update.Source != HookResolver.ResolveSource.PatternScan)
        {
            _statusMessage = $"gmVitalsUI::Update only resolved by fallback VA ({update.Detail}); not hooked.";
            RynthLog.Compat($"RetailVitalsHooks: {_statusMessage}");
            return;
        }

        try
        {
            _uiElementSetVisibleAddress = setVisible.Address;
            delegate* unmanaged[Thiscall]<IntPtr, void> detour = &UpdateDetour;
            MinHook.Hook(update.Address, (IntPtr)detour, out _originalUpdate);
            _hookInstalled = true;
            _statusMessage = $"Hooked gmVitalsUI::Update @ 0x{update.Address.ToInt32():X8} ({update.Detail}); SetVisible @ 0x{setVisible.Address.ToInt32():X8}.";
            RynthLog.Compat($"RetailVitalsHooks: {_statusMessage} Hide retail vitals={_hide}.");
        }
        catch (Exception ex)
        {
            _originalUpdate = IntPtr.Zero;
            _statusMessage = ex.Message;
            RynthLog.Compat($"RetailVitalsHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Capture (AC's main thread; must never throw into AC) ─────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static void UpdateDetour(IntPtr thisPtr)
    {
        try
        {
            RecursionGuard.Tick("RetailVitalsHooks.Update");
            if (thisPtr != IntPtr.Zero && !_logoffInProgress && MainThreadGuard.IsOnMainThread())
                Capture(thisPtr);
        }
        catch { }
        ((delegate* unmanaged[Thiscall]<IntPtr, void>)_originalUpdate)(thisPtr);
    }

    private static void Capture(IntPtr inst)
    {
        int free = -1;
        for (int i = 0; i < Slots.Length; i++)
        {
            if (Slots[i].Instance == inst) return;   // already have it
            if (free < 0 && Slots[i].Instance == IntPtr.Zero) free = i;
        }

        if (++_updateFires <= 6)
            RynthLog.Compat($"RetailVitalsHooks: Update fired on new instance 0x{inst.ToInt32():X8}.");

        if (!ClientObjectHooks.IsReadableSpan(inst, RegionFlagsOffset + 4))
        {
            Reject(inst, "object not readable");
            return;
        }
        IntPtr vtable = Marshal.ReadIntPtr(inst);
        if (!IsSetVisibleSlot(vtable))
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
        RynthLog.Compat($"RetailVitalsHooks: captured vitals element 0x{inst.ToInt32():X8} (vtable 0x{vtable.ToInt32():X8}, visible={IsOwnVisible(inst)}).");
    }

    private static void Reject(IntPtr inst, string why)
    {
        if (++_rejectLogs <= 8)
            RynthLog.Compat($"RetailVitalsHooks: not capturing 0x{inst.ToInt32():X8}: {why}.");
    }

    /// <summary>
    /// The vtable's SetVisible slot must be UIElement::SetVisible itself, or a
    /// one-instruction "jmp rel32" thunk to it (incremental-link thunks: the
    /// floaty classes' slot is 0x004CF950: jmp 0x00462390).
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

        bool hide = _hide;
        for (int i = 0; i < Slots.Length; i++)
        {
            IntPtr inst = Slots[i].Instance;
            if (inst == IntPtr.Zero) continue;
            if (!StillAlive(Slots[i]))
            {
                RynthLog.Compat($"RetailVitalsHooks: vitals element 0x{inst.ToInt32():X8} is gone (freed); dropped until it is captured again.");
                Slots[i] = default;
                continue;
            }

            bool visible = IsOwnVisible(inst);
            if (hide)
            {
                // Only the one AC is showing; its hidden sibling stays AC's business.
                if (visible)
                {
                    try { ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)setVisible)(inst, 0); }
                    catch { /* best-effort */ }
                    Slots[i].HiddenByUs = true;
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

    /// <summary>
    /// The object is still readable and still starts with the vtable it had when
    /// captured. A destroyed gmVitalsUI-family object has had its vtable reset to
    /// a base class's by the destructor chain (or its memory reused/decommitted).
    /// </summary>
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

    /// <summary>One line for /rc ui retailvitals.</summary>
    public static string Describe()
    {
        int hidden = 0;
        for (int i = 0; i < Slots.Length; i++)
            if (Slots[i].Instance != IntPtr.Zero && Slots[i].HiddenByUs) hidden++;
        string state = !_hookInstalled
            ? $"not available ({_statusMessage})"
            : $"{CountCaptured()} vitals element(s) captured, {hidden} hidden by RynthCore";
        return $"Hide retail vitals is {(HideRetailVitals ? "ON" : "OFF")} - {state}.";
    }

    // ── Logout / relog ────────────────────────────────────────────────────────

    /// <summary>
    /// A logoff was requested (LogoffOriginProbe): drop the vitals elements now,
    /// before AC frees them (see ChatHooks.OnLogoffRequested). AC's main thread.
    /// </summary>
    public static void OnLogoffRequested()
    {
        _logoffInProgress = true;
        for (int i = 0; i < Slots.Length; i++) Slots[i] = default;
    }

    /// <summary>The logout completed (PluginManager's logout dispatch): the next login builds new ones.</summary>
    public static void ResetCachedInstance()
    {
        for (int i = 0; i < Slots.Length; i++) Slots[i] = default;
        _logoffInProgress = false;
    }
}
