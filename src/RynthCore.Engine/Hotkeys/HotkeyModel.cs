// ============================================================================
//  RynthCore.Engine - Hotkeys/HotkeyModel.cs
//
//  User hotkeys (Settings > Hotkeys, /rc hotkey): a key with Ctrl/Alt/Shift runs
//  a built-in action (start/stop the macro, toggle Combat, open a panel...) or any
//  chat command ("/ra start", "/rn bc /ra stop", "/lua myscript go").
//
//  This file is the pure part, with no engine dependencies, so tools/HotkeyTests
//  compiles it as is: key names and combo parsing/formatting, the built-in action
//  list, matching a key press against the bindings (exact modifiers, key-down edge
//  only), the "is the player typing" rule, conflict warnings, the /rc hotkey
//  argument parser and the per-PC file (%LOCALAPPDATA%\RynthCore\hotkeys.txt).
//  The engine side (key messages, running actions, the Settings section) is
//  Hotkeys/HotkeyService.cs and ImGui/Panels/HotkeysSettingsUi.cs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RynthCore.Engine.Hotkeys;

[Flags]
internal enum HotkeyMods
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>A key (Win32 virtual-key code) and the modifiers held with it.</summary>
internal readonly record struct KeyCombo(int Vk, HotkeyMods Mods)
{
    public bool IsEmpty => Vk == 0;
    public override string ToString() => HotkeyKeys.Format(this);
}

/// <summary>Key names, and combo text ("Ctrl+Shift+F1") both ways.</summary>
internal static class HotkeyKeys
{
    public const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
        VK_MENU = 0x12, VK_PAUSE = 0x13, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22,
        VK_END = 0x23, VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_INSERT = 0x2D, VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_APPS = 0x5D,
        VK_NUMPAD0 = 0x60, VK_MULTIPLY = 0x6A, VK_ADD = 0x6B, VK_SUBTRACT = 0x6D, VK_DECIMAL = 0x6E,
        VK_DIVIDE = 0x6F, VK_F1 = 0x70, VK_F12 = 0x7B, VK_F24 = 0x87, VK_SCROLL = 0x91,
        VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5,
        VK_OEM_3 = 0xC0;

    private static readonly Dictionary<int, string> NameByVk = BuildNames();
    private static readonly Dictionary<string, int> VkByName = BuildLookup();

    private static Dictionary<int, string> BuildNames()
    {
        var n = new Dictionary<int, string>();
        for (int c = 'A'; c <= 'Z'; c++) n[c] = ((char)c).ToString();
        for (int c = '0'; c <= '9'; c++) n[c] = ((char)c).ToString();
        for (int i = 0; i < 24; i++) n[VK_F1 + i] = "F" + (i + 1);
        for (int i = 0; i < 10; i++) n[VK_NUMPAD0 + i] = "Num" + i;
        n[VK_MULTIPLY] = "Num*"; n[VK_ADD] = "Num+"; n[VK_SUBTRACT] = "Num-"; n[VK_DECIMAL] = "Num."; n[VK_DIVIDE] = "Num/";
        n[VK_BACK] = "Backspace"; n[VK_TAB] = "Tab"; n[VK_RETURN] = "Enter"; n[VK_PAUSE] = "Pause";
        n[VK_ESCAPE] = "Esc"; n[VK_SPACE] = "Space";
        n[VK_PRIOR] = "PageUp"; n[VK_NEXT] = "PageDown"; n[VK_END] = "End"; n[VK_HOME] = "Home";
        n[VK_LEFT] = "Left"; n[VK_UP] = "Up"; n[VK_RIGHT] = "Right"; n[VK_DOWN] = "Down";
        n[VK_INSERT] = "Insert"; n[VK_DELETE] = "Delete"; n[VK_SCROLL] = "ScrollLock";
        // US-layout punctuation (the OEM keys keep their codes on other layouts; the name is the US one).
        n[0xBA] = ";"; n[0xBB] = "="; n[0xBC] = ","; n[0xBD] = "-"; n[0xBE] = "."; n[0xBF] = "/";
        n[VK_OEM_3] = "`"; n[0xDB] = "["; n[0xDC] = "\\"; n[0xDD] = "]"; n[0xDE] = "'";
        return n;
    }

    private static Dictionary<string, int> BuildLookup()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in NameByVk) d[kv.Value] = kv.Key;
        void Alias(string name, int vk) => d[name] = vk;
        Alias("PgUp", VK_PRIOR); Alias("PgDn", VK_NEXT); Alias("PageDn", VK_NEXT);
        Alias("Ins", VK_INSERT); Alias("Del", VK_DELETE); Alias("Escape", VK_ESCAPE);
        Alias("Return", VK_RETURN); Alias("Spacebar", VK_SPACE); Alias("Back", VK_BACK);
        Alias("Plus", 0xBB); Alias("Equals", 0xBB); Alias("Minus", 0xBD); Alias("Comma", 0xBC);
        Alias("Period", 0xBE); Alias("Slash", 0xBF); Alias("Backslash", 0xDC); Alias("Semicolon", 0xBA);
        Alias("Quote", 0xDE); Alias("Apostrophe", 0xDE); Alias("Backtick", VK_OEM_3); Alias("Grave", VK_OEM_3);
        Alias("Tilde", VK_OEM_3); Alias("LBracket", 0xDB); Alias("RBracket", 0xDD);
        Alias("NumMultiply", VK_MULTIPLY); Alias("NumAdd", VK_ADD); Alias("NumPlus", VK_ADD);
        Alias("NumSubtract", VK_SUBTRACT); Alias("NumMinus", VK_SUBTRACT); Alias("NumDecimal", VK_DECIMAL);
        Alias("NumDivide", VK_DIVIDE); Alias("Scroll", VK_SCROLL);
        for (int i = 0; i < 10; i++) { Alias("Numpad" + i, VK_NUMPAD0 + i); Alias("Kp" + i, VK_NUMPAD0 + i); }
        return d;
    }

    /// <summary>The key's name ("F1", "Num5", "PageUp", ";"), or "Key 0x.." for one without a name.</summary>
    public static string KeyName(int vk) => NameByVk.TryGetValue(vk, out string? n) ? n : $"Key 0x{vk:X2}";

    /// <summary>Shift, Ctrl, Alt (either side) and the Windows keys: held with a key, never bound alone.</summary>
    public static bool IsModifierKey(int vk) =>
        vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL
            or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN or VK_APPS;

    /// <summary>A key a hotkey may use: any named key except Esc (it cancels and closes things).</summary>
    public static bool IsBindable(int vk) => vk != VK_ESCAPE && NameByVk.ContainsKey(vk);

    public static bool IsFunctionKey(int vk) => vk >= VK_F1 && vk <= VK_F24;

    public static string Format(KeyCombo c)
    {
        if (c.IsEmpty) return "(none)";
        var sb = new StringBuilder();
        if ((c.Mods & HotkeyMods.Ctrl) != 0) sb.Append("Ctrl+");
        if ((c.Mods & HotkeyMods.Alt) != 0) sb.Append("Alt+");
        if ((c.Mods & HotkeyMods.Shift) != 0) sb.Append("Shift+");
        sb.Append(KeyName(c.Vk));
        return sb.ToString();
    }

    /// <summary>
    /// "Ctrl+Shift+F1", "alt+num+", "Shift + PgDn" (spaces and case don't matter). Modifiers come
    /// first, each followed by '+'; what's left is the key's name.
    /// </summary>
    public static bool TryParse(string? text, out KeyCombo combo, out string error)
    {
        combo = default;
        error = string.Empty;
        string s = (text ?? string.Empty).Replace(" ", string.Empty).Replace("\t", string.Empty);
        if (s.Length == 0) { error = "No key given."; return false; }

        HotkeyMods mods = HotkeyMods.None;
        while (true)
        {
            int plus = s.IndexOf('+');
            if (plus <= 0 || plus == s.Length - 1) break;   // no modifier left ("+" alone or at the end is a key)
            HotkeyMods m = s[..plus].ToLowerInvariant() switch
            {
                "ctrl" or "control" or "ctl" => HotkeyMods.Ctrl,
                "alt" => HotkeyMods.Alt,
                "shift" => HotkeyMods.Shift,
                _ => HotkeyMods.None,
            };
            if (m == HotkeyMods.None) break;
            mods |= m;
            s = s[(plus + 1)..];
        }

        if (s.ToLowerInvariant() is "ctrl" or "control" or "alt" or "shift")
        {
            error = "A modifier alone can't be a hotkey; add a key (e.g. Ctrl+F1).";
            return false;
        }
        if (!VkByName.TryGetValue(s, out int vk))
        {
            error = $"Unknown key '{s}'. Keys: A-Z, 0-9, F1-F24, Num0-Num9, Num* Num+ Num- Num. Num/, " +
                    "Home End PageUp PageDown Insert Delete, Up Down Left Right, Space Tab Enter Backspace, ; = , - . / ` [ \\ ] '.";
            return false;
        }
        if (vk == VK_ESCAPE)
        {
            error = "Esc can't be a hotkey (it cancels and closes things).";
            return false;
        }
        combo = new KeyCombo(vk, mods);
        return true;
    }
}

/// <summary>
/// A built-in action. <see cref="Panel"/>: the panel title it opens/closes, for panel actions.
/// <see cref="Chat"/>: the chat command it runs ("/ra targetlock"); "{0}" is the binding's parameter
/// (<see cref="HotkeyBinding.Command"/>), for an action with a <see cref="Param"/> (e.g. the meta state).
/// <see cref="Group"/>: the dropdown section ("" = the RynthCore built-ins). <see cref="Tip"/>: a plain tooltip.
/// <see cref="Ilt"/>: an Infinite Leaftide panel (RynthILT's), offered only while <see cref="HotkeyActions.IltAvailable"/>.
/// </summary>
internal sealed record HotkeyAction(string Id, string Name, string? Panel = null, string? Chat = null,
    string Group = "", string Tip = "", string? Param = null, bool Ilt = false)
{
    public bool HasParam => Param != null;
}

internal static class HotkeyActions
{
    /// <summary>The action id of a binding that runs <see cref="HotkeyBinding.Command"/> as a chat command.</summary>
    public const string Command = "command";
    public const string PanelPrefix = "panel:";
    public const string VTankPrefix = "vt:";
    public const string VTankGroup = "VTank-style";
    /// <summary>The one-touch heal / stamina / mana actions (RynthAi's /ra heal, /ra stam, /ra mana).</summary>
    public const string HealId = "heal", StaminaId = "stamina", ManaId = "mana";
    /// <summary>The parameterized "Meta: set state" action (its binding's Command is the state name).</summary>
    public const string MetaStateId = VTankPrefix + "metastate";

    private static HotkeyAction V(string id, string name, string tip, string? chat = null, string? param = null) =>
        new(VTankPrefix + id, name, Chat: chat, Group: VTankGroup, Tip: tip, Param: param);

    private static HotkeyAction P(string title, string? name = null) =>
        new(PanelPrefix + title, "Open/close " + (name ?? title), title);

    // The ILT section's panels (the bar's ILT button): listed, resolved and run only on Infinite
    // Leaftide with RynthILT loaded, as the bar shows them (Compatibility/IltInfo.cs).
    private static HotkeyAction PIlt(string title, string tip) =>
        new(PanelPrefix + title, "Open/close " + title + " (Infinite Leaftide)", title, Tip: tip, Ilt: true);

    /// <summary>
    /// Whether the Infinite Leaftide panels are offered now: the engine sets it to "Infinite Leaftide
    /// with RynthILT loaded" (HotkeyService); never anywhere else (and never in the tests unless set).
    /// </summary>
    public static Func<bool> IltAvailable = static () => false;

    /// <summary>The action is listed and can be picked or typed now (ILT panels only on ILT).</summary>
    public static bool Offered(HotkeyAction a) => !a.Ilt || SafeIlt();

    private static bool SafeIlt()
    {
        try { return IltAvailable(); }
        catch { return false; }
    }

    /// <summary>Built-ins, in the order the Settings dropdown shows them.</summary>
    public static readonly HotkeyAction[] BuiltIns =
    {
        new("macro", "Start/stop the macro"),
        new("combat", "Toggle Combat"),
        new("buffing", "Toggle Buffing"),
        new("navigation", "Toggle Navigation"),
        new("looting", "Toggle Looting"),
        new("meta", "Toggle Meta"),
        new("forcerebuff", "Force rebuff"),
        // One-touch heal (RynthAi /ra heal | stam | mana): the best kit, spell or potion for that vital now.
        // HotkeyDefaults binds them once to F6 / F7 / F8.
        new(HealId, "Heal now (one touch)", Chat: "/ra heal", Tip: "Uses the best healing option you have right now: a healing kit, " +
            "Heal Self or a health potion, whatever your Heal At lines say, even with the macro stopped (/ra heal)."),
        new(StaminaId, "Restore stamina now (one touch)", Chat: "/ra stam", Tip: "A stamina kit, a stamina potion or Revitalize Self, " +
            "now, even with the macro stopped (/ra stam)."),
        new(ManaId, "Restore mana now (one touch)", Chat: "/ra mana", Tip: "Stamina to Mana Self or a mana potion, now, " +
            "even with the macro stopped (/ra mana)."),
        new("patrol", "Start dungeon patrol"),
        new("autosort", "Toggle auto-sort (RynthSort)"),
        new("reload", "Reload the engine"),
        P("RynthAi", "the RynthAi dashboard"),
        P("Radar"),
        P("Dungeon Map"),
        P("Inventory"),
        P("Items"),
        P("Settings"),
        P("Characters"),
        P("Chat"),
        P("Meta", "the Meta editor"),
        P("Nav", "the Nav editor"),
        P("Lua"),
        P("Damage"),
        P("Mini Remote"),
        P("Skills"),
        P("Vision"),
        P("Sense"),
        P("Item Count"),
        P("Loot Editor"),
        P("RynthNav"),
        PIlt("Bounties", "RynthILT's bounty tracker (/rc bounties)."),
        PIlt("Guardian", "RynthILT's Temple guardian tools (/rc guardian)."),

        // VTank's hotkey actions, by VTank's names. Those RynthCore already had run the same code
        // as the built-ins above; the rest run a RynthAi /ra command (also usable as a chat command).
        V("metastate", "Meta: set state...", "Switches the meta to the state you pick (/ra setmetastate <state>).",
            "/ra setmetastate {0}", param: "State"),
        V("startstop", "Start/Stop", "Starts or stops the macro."),
        V("targetlock", "Target lock", "Makes the macro attack the monster you have selected, ahead of everything else. " +
            "Press it again on the same monster to let go (/ra targetlock).", "/ra targetlock"),
        V("navigation", "Navigation", "Turns Navigation on or off."),
        V("looting", "Looting", "Turns Looting on or off."),
        V("combat", "Combat", "Turns Combat on or off."),
        V("buffing", "Buffing", "Turns Buffing on or off."),
        V("forcebuff", "Force buff", "Recasts every buff now, whatever time is left."),
        V("cancelforcebuff", "Cancel force buff", "Stops a force buff in progress."),
        V("meleelow", "MeleeLow", "Melee attacks go low (/ra meleelow).", "/ra meleelow"),
        V("meleemid", "MeleeMid", "Melee attacks go to the middle (/ra meleemid).", "/ra meleemid"),
        V("meleehigh", "MeleeHigh", "Melee attacks go high (/ra meleehigh).", "/ra meleehigh"),
        V("navaddpt", "NavAddPt", "Adds a waypoint where you stand to the loaded route (/ra navaddpt).", "/ra navaddpt"),
        V("testitem", "TestItem", "Checks the selected item against your loot profile and says what it would do (/ra testitem).", "/ra testitem"),
        V("propertydump", "PropertyDump", "Lists every property of the selected object in chat (/ra propertydump).", "/ra propertydump"),
        V("recharge", "Recharge", "One heal, stamina or mana top-off now, using your Top Off settings, " +
            "even with the macro stopped (/ra recharge).", "/ra recharge"),
        V("navprio", "NavPrio", "Turns Boost Nav Priority on or off (/ra navprio).", "/ra navprio"),
        V("lootprio", "LootPrio", "Turns Boost Loot Priority on or off (/ra lootprio).", "/ra lootprio"),
        V("navlootprio", "NavLootPrio", "Turns Boost Nav Priority and Boost Loot Priority on together, or both off " +
            "when both are on (/ra navlootprio).", "/ra navlootprio"),
        V("lootthiscont", "LootThisCont", "Loots the corpse or chest you have open now, even one the macro would skip. " +
            "Never house storage or your own packs (/ra lootthis).", "/ra lootthis"),
    };

    /// <summary>The chat command a <see cref="HotkeyAction.Chat"/> action runs for this binding (its parameter filled in).</summary>
    public static string ChatFor(HotkeyAction a, HotkeyBinding b) =>
        a.Chat == null ? string.Empty : a.Chat.Replace("{0}", b.Command.Trim()).Trim();

    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c) && c != '-' && c != '_') sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>A built-in by id ("combat", "panel:Dungeon Map"), loosely ("panel dungeonmap", "dungeon map",
    /// "Toggle Combat"); null if none.</summary>
    public static HotkeyAction? Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        if (t.StartsWith("panel ", StringComparison.OrdinalIgnoreCase)) t = PanelPrefix + t[6..];
        string n = Norm(t);
        foreach (HotkeyAction a in BuiltIns)
            if (Offered(a) && (Norm(a.Id) == n || Norm(a.Name) == n)) return a;
        foreach (HotkeyAction a in BuiltIns)   // a panel's title alone
            if (Offered(a) && a.Panel != null && Norm(a.Panel) == n) return a;
        foreach (HotkeyAction a in BuiltIns)   // a VTank-style id without "vt:" ("startstop", "targetlock")
            if (a.Group == VTankGroup && Norm(a.Id) == VTankPrefix + n) return a;
        return null;
    }

    public static HotkeyAction? Find(string id)
    {
        foreach (HotkeyAction a in BuiltIns)
            if (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    /// <summary>What the binding does, for lists ("Toggle Combat", "Chat: /ra start", "Meta: Hunting").</summary>
    public static string Describe(HotkeyBinding b)
    {
        if (string.Equals(b.Action, Command, StringComparison.OrdinalIgnoreCase))
            return b.Command.Length > 0 ? "Chat: " + b.Command : "Chat: (no command)";
        HotkeyAction? a = Find(b.Action);
        if (a == null) return $"Unknown action '{b.Action}'";
        string param = b.Command.Trim();
        if (string.Equals(a.Id, MetaStateId, StringComparison.OrdinalIgnoreCase))
            return param.Length > 0 ? "Meta: " + param : "Meta: (no state)";
        if (a.HasParam) return param.Length > 0 ? $"{a.Name} {param}" : $"{a.Name} (no {a.Param!.ToLowerInvariant()})";
        return a.Group.Length > 0 ? $"{a.Name} ({a.Group})" : a.Name;
    }

    /// <summary>
    /// An action that takes a parameter, and the text after it: "metastate Hunting" or "vt:metastate
    /// Hunting" -> (Meta: set state, "Hunting"). Null if the text doesn't start with one.
    /// </summary>
    public static (HotkeyAction Action, string Param)? ResolveWithParam(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        for (int sp = t.IndexOf(' '); sp > 0; sp = t.IndexOf(' ', sp + 1))
        {
            string head = t[..sp];
            if (Resolve(head) is { HasParam: true } a) return (a, t[(sp + 1)..].Trim());
        }
        return null;
    }
}

/// <summary>One hotkey: the combo, a built-in action id or <see cref="HotkeyActions.Command"/>, and its chat command.</summary>
internal sealed record HotkeyBinding(KeyCombo Combo, string Action, string Command = "", bool Enabled = true)
{
    public bool IsCommand => string.Equals(Action, HotkeyActions.Command, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Turning a key message into a combo and finding its binding.</summary>
internal static class HotkeyMatch
{
    /// <summary>
    /// WM_KEYDOWN / WM_SYSKEYDOWN lParam bit 30 is the key's previous state: set on auto-repeat.
    /// A hotkey fires on the first key-down only, never on repeats.
    /// </summary>
    public static bool IsFreshPress(long lParam) => (lParam & 0x40000000) == 0;

    /// <summary>lParam bit 24: an extended key (numpad Enter, the arrow/navigation block).</summary>
    public static bool IsExtended(long lParam) => (lParam & 0x01000000) != 0;

    /// <summary>
    /// The virtual key a binding is matched on: left/right modifiers fold into Shift/Ctrl/Alt, and
    /// numpad Enter (extended Return) gives 0: AC keeps it for its own functions, as the chat code does.
    /// </summary>
    public static int NormalizeVk(int vk, bool extended)
    {
        if (vk == HotkeyKeys.VK_RETURN && extended) return 0;
        return vk switch
        {
            HotkeyKeys.VK_LSHIFT or HotkeyKeys.VK_RSHIFT => HotkeyKeys.VK_SHIFT,
            HotkeyKeys.VK_LCONTROL or HotkeyKeys.VK_RCONTROL => HotkeyKeys.VK_CONTROL,
            HotkeyKeys.VK_LMENU or HotkeyKeys.VK_RMENU => HotkeyKeys.VK_MENU,
            _ => vk,
        };
    }

    public static HotkeyMods Mods(bool ctrl, bool alt, bool shift) =>
        (ctrl ? HotkeyMods.Ctrl : 0) | (alt ? HotkeyMods.Alt : 0) | (shift ? HotkeyMods.Shift : 0);

    /// <summary>The first enabled binding for exactly this key and these modifiers (Ctrl+F1 doesn't fire on Ctrl+Shift+F1).</summary>
    public static HotkeyBinding? Find(IReadOnlyList<HotkeyBinding> bindings, KeyCombo pressed)
    {
        if (pressed.IsEmpty) return null;
        foreach (HotkeyBinding b in bindings)
            if (b.Enabled && b.Combo == pressed) return b;
        return null;
    }
}

/// <summary>What the engine knows when a key goes down.</summary>
/// <param name="InWorld">A character is in the world (not at character select).</param>
/// <param name="GameFocused">The foreground window is this client's (the game or one of our windows).</param>
/// <param name="RynthTextFocus">A RynthCore text box has the keyboard: RynthChat's input, an ImGui text
/// field (io.WantTextInput), a popped-out panel's text box, an Avalonia text box.</param>
/// <param name="AcTextFocus">AC's own UI has a keyboard focus element (its chat entry being typed in, a
/// quantity box...): true / false, or null when AC's UI manager isn't readable.</param>
internal readonly record struct TypingState(bool InWorld, bool GameFocused, bool RynthTextFocus, bool? AcTextFocus);

internal enum HotkeyGate
{
    Fire,
    NotInWorld,
    NotFocused,
    RynthTyping,
    AcTyping,
    /// <summary>AC's focus is unknown and the key could be typing (no Ctrl/Alt, not an F key).</summary>
    AcUnknownTextKey,
}

internal static class HotkeyRules
{
    /// <summary>
    /// Whether a bound key may fire. Never while the player is typing: in a RynthCore text box or in
    /// AC's own chat entry. When AC's focus can't be read, only keys that can't be typing fire.
    /// </summary>
    public static HotkeyGate Check(in TypingState s, KeyCombo combo)
    {
        if (!s.InWorld) return HotkeyGate.NotInWorld;
        if (!s.GameFocused) return HotkeyGate.NotFocused;
        if (s.RynthTextFocus) return HotkeyGate.RynthTyping;
        if (s.AcTextFocus == true) return HotkeyGate.AcTyping;
        if (s.AcTextFocus == null && !CantBeTyping(combo)) return HotkeyGate.AcUnknownTextKey;
        return HotkeyGate.Fire;
    }

    /// <summary>Ctrl or Alt held, or an F key: a combo no text box takes as typing.</summary>
    public static bool CantBeTyping(KeyCombo c) =>
        (c.Mods & (HotkeyMods.Ctrl | HotkeyMods.Alt)) != 0 || HotkeyKeys.IsFunctionKey(c.Vk);

    public static string Describe(HotkeyGate g) => g switch
    {
        HotkeyGate.Fire => "hotkeys fire",
        HotkeyGate.NotInWorld => "not in the world",
        HotkeyGate.NotFocused => "the game window isn't focused",
        HotkeyGate.RynthTyping => "typing in a RynthCore text box",
        HotkeyGate.AcTyping => "typing in AC's chat (or another AC text box)",
        HotkeyGate.AcUnknownTextKey => "AC's typing state is unknown; only Ctrl/Alt/F-key hotkeys fire",
        _ => g.ToString(),
    };
}

/// <summary>Warnings for a combo: bound twice, or one AC itself uses. Warnings only; the binding is allowed.</summary>
internal static class HotkeyConflicts
{
    /// <summary>Why AC may want this key itself (null if it's not a common AC key).</summary>
    public static string? CommonAcKey(KeyCombo c)
    {
        bool ctrlAlt = (c.Mods & (HotkeyMods.Ctrl | HotkeyMods.Alt)) != 0;
        if (ctrlAlt) return null;
        bool shift = (c.Mods & HotkeyMods.Shift) != 0;
        int vk = c.Vk;
        // Movement: with or without Shift (Shift switches walk/run while held).
        if (vk is 'W' or 'A' or 'S' or 'D' or 'Q' or 'E' or 'Z' or 'X' or 'C'
            or HotkeyKeys.VK_UP or HotkeyKeys.VK_DOWN or HotkeyKeys.VK_LEFT or HotkeyKeys.VK_RIGHT)
            return "AC moves your character with it";
        if (shift) return null;
        if (vk == HotkeyKeys.VK_SPACE) return "AC jumps with it";
        if (vk == HotkeyKeys.VK_RETURN) return "AC opens its chat with it";
        if (vk is >= '0' and <= '9') return "AC's shortcut bar uses the number keys";
        if (vk is HotkeyKeys.VK_TAB or HotkeyKeys.VK_BACK or HotkeyKeys.VK_OEM_3 or HotkeyKeys.VK_HOME
            or HotkeyKeys.VK_END or HotkeyKeys.VK_PRIOR or HotkeyKeys.VK_NEXT or HotkeyKeys.VK_INSERT or HotkeyKeys.VK_DELETE)
            return "AC's default keymap uses it";
        return null;
    }

    /// <summary>
    /// Every warning for giving <paramref name="combo"/> to a binding. <paramref name="ignoreIndex"/>: the
    /// binding being edited (it doesn't conflict with itself).
    /// </summary>
    public static List<string> Warnings(IReadOnlyList<HotkeyBinding> bindings, KeyCombo combo, int ignoreIndex = -1)
    {
        var list = new List<string>();
        if (combo.IsEmpty) return list;
        for (int i = 0; i < bindings.Count; i++)
        {
            if (i == ignoreIndex || bindings[i].Combo != combo) continue;
            bool firstWins = ignoreIndex < 0 ? true : i < ignoreIndex;
            list.Add($"{combo} is also bound to \"{HotkeyActions.Describe(bindings[i])}\"" +
                     (bindings[i].Enabled ? (firstWins ? " (that one fires, it's higher in the list)." : " (this one fires, it's higher in the list).")
                                           : " (switched off)."));
        }
        if (CommonAcKey(combo) is { } why)
            list.Add($"{combo}: {why}. While the hotkey is on, AC won't get that key.");
        return list;
    }

    /// <summary>Indexes of enabled bindings that share their combo with an earlier enabled one (they never fire).</summary>
    public static List<int> Shadowed(IReadOnlyList<HotkeyBinding> bindings)
    {
        var seen = new HashSet<KeyCombo>();
        var shadowed = new List<int>();
        for (int i = 0; i < bindings.Count; i++)
        {
            if (!bindings[i].Enabled) continue;
            if (!seen.Add(bindings[i].Combo)) shadowed.Add(i);
        }
        return shadowed;
    }
}

internal enum HotkeyVerb { Help, List, Actions, Status, Add, Remove, Enable, Disable, On, Off, Error }

/// <summary>A parsed "/rc hotkey ..." line.</summary>
internal sealed record HotkeyCommand(HotkeyVerb Verb, KeyCombo Combo = default, string Action = "", string ChatCommand = "",
    string Error = "");

internal static class HotkeyCommandParser
{
    public const string Usage =
        "Usage: /rc hotkey list | add <combo> <action or /command> | remove <combo> | enable|disable <combo> | " +
        "actions | status | on|off. Combo e.g. F5, Ctrl+1, Alt+Shift+Num3. /rc hotkeys opens Settings > Hotkeys.";

    /// <summary>Parses the arguments after "/rc hotkey".</summary>
    public static HotkeyCommand Parse(string? args)
    {
        string a = (args ?? string.Empty).Trim();
        if (a.Length == 0) return new HotkeyCommand(HotkeyVerb.Help);
        int sp = a.IndexOf(' ');
        string verb = (sp < 0 ? a : a[..sp]).ToLowerInvariant();
        string rest = sp < 0 ? string.Empty : a[(sp + 1)..].Trim();

        switch (verb)
        {
            case "help": case "?": return new HotkeyCommand(HotkeyVerb.Help);
            case "list": case "ls": return new HotkeyCommand(HotkeyVerb.List);
            case "actions": return new HotkeyCommand(HotkeyVerb.Actions);
            case "status": return new HotkeyCommand(HotkeyVerb.Status);
            case "on": return rest.Length == 0 ? new HotkeyCommand(HotkeyVerb.On) : WithCombo(HotkeyVerb.Enable, rest);
            case "off": return rest.Length == 0 ? new HotkeyCommand(HotkeyVerb.Off) : WithCombo(HotkeyVerb.Disable, rest);
            case "remove": case "rm": case "del": case "delete": return WithCombo(HotkeyVerb.Remove, rest);
            case "enable": return WithCombo(HotkeyVerb.Enable, rest);
            case "disable": return WithCombo(HotkeyVerb.Disable, rest);
            case "add": case "bind": case "set": return ParseAdd(rest);
            default: return Fail($"Unknown '/rc hotkey {verb}'. {Usage}");
        }
    }

    private static HotkeyCommand WithCombo(HotkeyVerb verb, string rest)
    {
        if (rest.Length == 0) return Fail($"Which key? e.g. /rc hotkey {verb.ToString().ToLowerInvariant()} Ctrl+F1");
        return HotkeyKeys.TryParse(rest, out KeyCombo combo, out string err) ? new HotkeyCommand(verb, combo) : Fail(err);
    }

    private static HotkeyCommand ParseAdd(string rest)
    {
        // The combo is one word (no spaces inside: "Ctrl+Shift+F1"); the rest is the action.
        int sp = rest.IndexOf(' ');
        if (rest.Length == 0 || sp < 0)
            return Fail("Usage: /rc hotkey add <combo> <action or /command>, e.g. /rc hotkey add F5 macro  or  /rc hotkey add Ctrl+F1 /ra start");
        string comboText = rest[..sp];
        string what = rest[(sp + 1)..].Trim();
        if (!HotkeyKeys.TryParse(comboText, out KeyCombo combo, out string err)) return Fail(err);
        if (what.StartsWith('/'))
            return new HotkeyCommand(HotkeyVerb.Add, combo, HotkeyActions.Command, what);
        HotkeyAction? action = HotkeyActions.Resolve(what);
        if (action == null && HotkeyActions.ResolveWithParam(what) is { } withParam)
        {
            if (withParam.Param.Length == 0)
                return Fail($"'{withParam.Action.Name}' needs a {withParam.Action.Param!.ToLowerInvariant()}.");
            return new HotkeyCommand(HotkeyVerb.Add, combo, withParam.Action.Id, withParam.Param);
        }
        if (action is { HasParam: true })
            return Fail($"'{action.Name}' needs a {action.Param!.ToLowerInvariant()}: /rc hotkey add {comboText} {action.Id} <{action.Param.ToLowerInvariant()}>");
        if (action == null)
            return Fail($"Unknown action '{what}'. Use a chat command starting with / or a built-in (/rc hotkey actions).");
        return new HotkeyCommand(HotkeyVerb.Add, combo, action.Id);
    }

    private static HotkeyCommand Fail(string error) => new(HotkeyVerb.Error, Error: error);
}

/// <summary>The bindings and the master switch, as saved. <paramref name="DefaultsApplied"/>: the
/// <see cref="HotkeyDefaults"/> version already offered to this file (0 = never).</summary>
internal sealed record HotkeyFile(bool Enabled, List<HotkeyBinding> Bindings, int DefaultsApplied = 0);

/// <summary>
/// Bindings a file gets once, when it has never been offered this version of them (a new PC, or an
/// engine that brought new default keys): the one-touch heal keys. Added only where they cannot take
/// anything from the player: an action already bound to any key is left alone, and so is a key already
/// bound to anything. The version is saved with the file, so a default the player removes or changes
/// stays gone. Raise <see cref="Version"/> with new entries; entries already offered are not offered again.
/// </summary>
internal static class HotkeyDefaults
{
    public const int Version = 1;

    private const int VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77;

    /// <summary>The default key for each action, in the order they are added. Tell the player where: Settings > Hotkeys.</summary>
    public static readonly (KeyCombo Combo, string Action)[] All =
    {
        (new KeyCombo(VK_F6, HotkeyMods.None), HotkeyActions.HealId),
        (new KeyCombo(VK_F7, HotkeyMods.None), HotkeyActions.StaminaId),
        (new KeyCombo(VK_F8, HotkeyMods.None), HotkeyActions.ManaId),
    };

    /// <summary>The file with the defaults it has not been offered yet (the same file when it has been).</summary>
    public static HotkeyFile Apply(HotkeyFile file)
    {
        if (file.DefaultsApplied >= Version) return file;
        var list = new List<HotkeyBinding>(file.Bindings);
        foreach (var (combo, action) in All)
        {
            bool actionBound = list.Exists(b => string.Equals(b.Action, action, StringComparison.OrdinalIgnoreCase));
            bool keyBound = list.Exists(b => b.Combo == combo);
            if (!actionBound && !keyBound) list.Add(new HotkeyBinding(combo, action));
        }
        return file with { Bindings = list, DefaultsApplied = Version };
    }
}

/// <summary>
/// %LOCALAPPDATA%\RynthCore\hotkeys.txt, for every client on this PC. Text, one binding a line:
/// "on|off &lt;TAB&gt; combo &lt;TAB&gt; action &lt;TAB&gt; chat command". RYNTHCORE_HOTKEYS_FILE points a test at its own file.
/// </summary>
internal static class HotkeyStore
{
    public const string EnvOverride = "RYNTHCORE_HOTKEYS_FILE";

    public static string FilePath
    {
        get
        {
            if (Environment.GetEnvironmentVariable(EnvOverride) is { Length: > 0 } p) return p;
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "RynthCore", "hotkeys.txt");
        }
    }

    public static string Serialize(HotkeyFile file)
    {
        var sb = new StringBuilder();
        sb.Append("# RynthCore hotkeys (Settings > Hotkeys, /rc hotkey). Shared by every client on this PC.\n");
        sb.Append("# One per line: on|off <TAB> combo <TAB> action <TAB> chat command (action \"command\").\n");
        sb.Append("enabled=").Append(file.Enabled ? '1' : '0').Append('\n');
        if (file.DefaultsApplied > 0) sb.Append("defaults=").Append(file.DefaultsApplied).Append('\n');
        foreach (HotkeyBinding b in file.Bindings)
        {
            if (b.Combo.IsEmpty) continue;
            bool keepsText = b.IsCommand || HotkeyActions.Find(b.Action)?.HasParam == true;   // a chat command, or a parameter
            string cmd = keepsText ? b.Command.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim() : string.Empty;
            sb.Append(b.Enabled ? "on" : "off").Append('\t').Append(HotkeyKeys.Format(b.Combo)).Append('\t')
              .Append(b.Action).Append('\t').Append(cmd).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Reads the file's text; bad lines are skipped (never throws).</summary>
    public static HotkeyFile Parse(string? text)
    {
        var file = new HotkeyFile(true, new List<HotkeyBinding>());
        bool enabled = true;
        int defaults = 0;
        if (string.IsNullOrEmpty(text)) return file;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (line.StartsWith("enabled=", StringComparison.OrdinalIgnoreCase))
            {
                enabled = line.Substring(8).Trim() != "0";
                continue;
            }
            if (line.StartsWith("defaults=", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line.Substring(9).Trim(), out defaults);
                continue;
            }
            string[] f = line.Split('\t', 4);
            if (f.Length < 3) continue;
            if (!HotkeyKeys.TryParse(f[1], out KeyCombo combo, out _)) continue;
            string action = f[2].Trim();
            if (action.Length == 0) continue;
            string cmd = f.Length > 3 ? f[3].Trim() : string.Empty;
            bool on = !f[0].Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
            file.Bindings.Add(new HotkeyBinding(combo, action, cmd, on));
        }
        return file with { Enabled = enabled, DefaultsApplied = Math.Max(0, defaults) };
    }

    public static HotkeyFile Load()
    {
        try
        {
            string path = FilePath;
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : new HotkeyFile(true, new List<HotkeyBinding>());
        }
        catch
        {
            return new HotkeyFile(true, new List<HotkeyBinding>());
        }
    }

    /// <summary>Writes the file (temp file, then replace). Returns false on an I/O error.</summary>
    public static bool Save(HotkeyFile file)
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(file));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The file's last write time (UTC), or MinValue if it doesn't exist.</summary>
    public static DateTime LastWriteUtc()
    {
        try
        {
            string path = FilePath;
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}

/// <summary>
/// Which key-ups and chars belong to a key a hotkey took. A hotkey's key-down is eaten, so are its
/// auto-repeats, the char Windows makes from it and its key-up: AC sees none of that keystroke.
/// Any other key passes untouched. Pure state, driven by HotkeyService on AC's thread.
/// </summary>
internal sealed class HotkeyKeyTracker
{
    private readonly bool[] _eaten = new bool[256];
    private bool _eatNextChar;

    /// <summary>A hotkey took this key-down: eat the rest of the keystroke.</summary>
    public void Took(int vk)
    {
        if ((uint)vk < 256) _eaten[vk] = true;
        _eatNextChar = true;
    }

    /// <summary>True if this key's keystroke is being eaten (a repeat of a key a hotkey took).</summary>
    public bool IsEaten(int vk) => (uint)vk < 256 && _eaten[vk];

    /// <summary>Key-down of another key: a char after it is that key's, not ours.</summary>
    public void OtherKeyDown() => _eatNextChar = false;

    /// <summary>A WM_CHAR / WM_SYSCHAR: eat it when it's the one our key-down made.</summary>
    public bool TakeChar()
    {
        bool eat = _eatNextChar;
        _eatNextChar = false;
        return eat;
    }

    /// <summary>A key-up: true (eat it) when its key-down was a hotkey's.</summary>
    public bool KeyUp(int vk)
    {
        _eatNextChar = false;
        if ((uint)vk >= 256 || !_eaten[vk]) return false;
        _eaten[vk] = false;
        return true;
    }

    /// <summary>Focus lost: Windows sends no key-ups for keys still down; forget them.</summary>
    public void Reset()
    {
        Array.Clear(_eaten);
        _eatNextChar = false;
    }
}
