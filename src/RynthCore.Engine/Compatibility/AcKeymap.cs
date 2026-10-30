// ============================================================================
//  RynthCore.Engine - Compatibility/AcKeymap.cs
//
//  The player's AC key bindings, read from the keymap file AC uses: Documents\
//  Asheron's Call\UserPreferences.ini's [Input] KeymapFile= (saving one from
//  Configure Keyboard makes it the default), else the client's own
//  "<exe>.keymap". Plugin API v90 GetActionKeys: an action's keys as the
//  player reads them ("1, Ctrl+1"), for RynthDefective's bar (each slot shows
//  the UseQuickSlot_N keys the retail toolbar answers to).
//
//  The file (AC:TD's own comments in it): a MetaKeys block numbers the
//  modifier keys (1 [ 0 DIK_LSHIFT ], 2 [ 0 DIK_LCONTROL ] ...); each binding
//  is  Action [ "" [ device KEY subcontrol? ] metakeys? activation? ]  where
//  device 0 is the keyboard and 1 the mouse, and metakeys is a bit mask, the
//  bit of metakey n being 1 << (n - 1) (0x3 = Ctrl+Shift).
//
//  Read again when the file (or UserPreferences.ini) changes, looked at no
//  more than every 2 s. Any thread; reads only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RynthCore.Engine.Compatibility;

internal static class AcKeymap
{
    /// <summary>One binding: device (0 keyboard, 1 mouse), the DirectInput name, the metakey mask.</summary>
    public readonly record struct Binding(int Device, string Key, uint Meta);

    private static readonly object Sync = new();
    private static Dictionary<string, List<Binding>> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private static string[] _metaNames = Array.Empty<string>();   // index n-1: metakey n's name
    private static string _file = string.Empty;
    private static DateTime _fileTime, _prefsTime;
    private static long _checkedMs = long.MinValue / 2;

    /// <summary>Documents\Asheron's Call, where AC keeps UserPreferences.ini and the keymaps.</summary>
    internal static string AcUserFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Asheron's Call");

    /// <summary>The keymap file AC uses ("" when there is none).</summary>
    public static string FilePath { get { Refresh(); lock (Sync) return _file; } }

    /// <summary>The action's bindings (none for an unknown action or no keymap).</summary>
    public static IReadOnlyList<Binding> BindingsFor(string action)
    {
        Refresh();
        lock (Sync)
            return _bindings.TryGetValue(action, out List<Binding>? list) ? list.ToArray() : Array.Empty<Binding>();
    }

    /// <summary>The action's keys as the player reads them, "1, Ctrl+1"; "" when unbound.</summary>
    public static string KeysText(string action)
    {
        IReadOnlyList<Binding> list = BindingsFor(action);
        if (list.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (Binding b in list)
        {
            string one = Describe(b);
            if (one.Length == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(one);
        }
        return sb.ToString();
    }

    /// <summary>One binding as text: its modifiers (by the file's MetaKeys) and the key.</summary>
    public static string Describe(Binding b)
    {
        var sb = new StringBuilder();
        string[] metas;
        lock (Sync) metas = _metaNames;
        for (int n = 1; n <= 32; n++)
        {
            if ((b.Meta & (1u << (n - 1))) == 0) continue;
            string name = n - 1 < metas.Length && metas[n - 1].Length > 0 ? metas[n - 1] : "Meta" + n;
            sb.Append(name).Append('+');
        }
        sb.Append(KeyName(b.Device, b.Key));
        return sb.ToString();
    }

    /// <summary>The file changed (AcKeymapLive wrote it): read it again at the next look.</summary>
    public static void Invalidate()
    {
        lock (Sync)
        {
            _checkedMs = long.MinValue / 2;
            _fileTime = DateTime.MinValue;
        }
    }

    private static void Refresh()
    {
        long now = Environment.TickCount64;
        lock (Sync)
        {
            if (now - _checkedMs < 2000) return;
            _checkedMs = now;
        }
        try
        {
            string folder = AcUserFolder;
            string prefs = Path.Combine(folder, "UserPreferences.ini");
            DateTime prefsTime = File.Exists(prefs) ? File.GetLastWriteTimeUtc(prefs) : DateTime.MinValue;
            string file = Path.Combine(folder, KeymapName(prefs));
            DateTime fileTime = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            lock (Sync)
                if (file == _file && fileTime == _fileTime && prefsTime == _prefsTime) return;
            Parse(file, out var bindings, out string[] metas);
            lock (Sync)
            {
                _file = File.Exists(file) ? file : string.Empty;
                _fileTime = fileTime;
                _prefsTime = prefsTime;
                _bindings = bindings;
                _metaNames = metas;
            }
            RynthLog.Compat($"AcKeymap: {(File.Exists(file) ? file : "no keymap file")} - {bindings.Count} action(s) bound.");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"AcKeymap: could not read the keymap: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>[Input] KeymapFile= in UserPreferences.ini, else "&lt;the client exe&gt;.keymap".</summary>
    private static string KeymapName(string prefs)
    {
        if (File.Exists(prefs))
        {
            bool input = false;
            foreach (string raw in File.ReadLines(prefs))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { input = line.Equals("[Input]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!input) continue;
                int eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().Equals("KeymapFile", StringComparison.OrdinalIgnoreCase))
                {
                    string name = line[(eq + 1)..].Trim();
                    if (name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) return name;
                }
            }
        }
        string exe = "acclient";
        try { exe = Process.GetCurrentProcess().ProcessName; } catch { }
        return exe + ".keymap";
    }

    // Action [ "" [ device KEY ...] mask? ...
    private static readonly Regex BindingLine = new(
        @"^\s*([A-Za-z0-9_]+)\s*\[\s*""[^""]*""\s*\[\s*(\d+)\s+([A-Za-z0-9_]+)[^\]]*\]\s*(0x[0-9A-Fa-f]+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // n [ device KEY ]
    private static readonly Regex MetaLine = new(@"^\s*(\d+)\s*\[\s*\d+\s+([A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void Parse(string file, out Dictionary<string, List<Binding>> bindings, out string[] metas)
    {
        bindings = new Dictionary<string, List<Binding>>(StringComparer.OrdinalIgnoreCase);
        var metaList = new List<string>();
        if (!File.Exists(file)) { metas = Array.Empty<string>(); return; }
        string section = string.Empty;
        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line is "Devices" or "MetaKeys" or "Bindings") { section = line; continue; }
            if (section == "MetaKeys")
            {
                Match m = MetaLine.Match(line);
                if (!m.Success) continue;
                int n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (n < 1 || n > 32) continue;
                while (metaList.Count < n) metaList.Add(string.Empty);
                if (metaList[n - 1].Length == 0) metaList[n - 1] = MetaName(m.Groups[2].Value);
                continue;
            }
            if (section != "Bindings") continue;
            Match b = BindingLine.Match(line);
            if (!b.Success) continue;
            uint mask = 0;
            if (b.Groups[4].Success)
                uint.TryParse(b.Groups[4].Value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out mask);
            var binding = new Binding(int.Parse(b.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), b.Groups[3].Value, mask);
            if (!bindings.TryGetValue(b.Groups[1].Value, out List<Binding>? list))
                bindings[b.Groups[1].Value] = list = new List<Binding>();
            if (!list.Contains(binding)) list.Add(binding);
        }
        metas = metaList.ToArray();
    }

    private static string MetaName(string key) => key.ToUpperInvariant() switch
    {
        "DIK_LSHIFT" or "DIK_RSHIFT" => "Shift",
        "DIK_LCONTROL" or "DIK_RCONTROL" => "Ctrl",
        "DIK_LMENU" or "DIK_RMENU" or "DIK_RALT" or "DIK_LALT" => "Alt",
        "DIK_LWIN" or "DIK_RWIN" => "Win",
        _ => KeyName(key.StartsWith("DIMOFS_", StringComparison.OrdinalIgnoreCase) ? 1 : 0, key),
    };

    /// <summary>A DirectInput key or mouse name as the player reads it: DIK_1 "1", DIK_NUMPAD5 "Num5", DIMOFS_BUTTON3 "Mouse4".</summary>
    public static string KeyName(int device, string key)
    {
        string k = key.ToUpperInvariant();
        if (k.StartsWith("DIMOFS_BUTTON", StringComparison.Ordinal) && int.TryParse(k.AsSpan(13), out int button))
            return "Mouse" + (button + 1);
        if (k == "DIMOFS_Z") return "Wheel";
        if (!k.StartsWith("DIK_", StringComparison.Ordinal)) return key;
        string n = k[4..];
        if (n.StartsWith("NUMPAD", StringComparison.Ordinal) && n.Length > 6)
        {
            string rest = n[6..];
            return rest switch
            {
                "ENTER" => "NumEnter", "COMMA" => "Num,", "EQUALS" => "Num=", "STAR" => "Num*", "SLASH" => "Num/",
                "PLUS" => "Num+", "MINUS" => "Num-", "PERIOD" => "Num.", _ => "Num" + rest,
            };
        }
        return n switch
        {
            "MINUS" => "-", "EQUALS" => "=", "LBRACKET" => "[", "RBRACKET" => "]", "SEMICOLON" => ";",
            "APOSTROPHE" => "'", "GRAVE" => "`", "BACKSLASH" => "\\", "COMMA" => ",", "PERIOD" => ".", "SLASH" => "/",
            "MULTIPLY" => "Num*", "SUBTRACT" => "Num-", "ADD" => "Num+", "DECIMAL" => "Num.", "DIVIDE" => "Num/",
            "SPACE" => "Space", "RETURN" => "Enter", "ESCAPE" => "Esc", "BACK" => "Backspace", "TAB" => "Tab",
            "CAPITAL" or "CAPSLOCK" => "CapsLock", "PRIOR" or "PGUP" => "PgUp", "NEXT" or "PGDN" => "PgDn",
            "INSERT" => "Ins", "DELETE" => "Del", "HOME" => "Home", "END" => "End", "BACKSPACE" => "Backspace",
            "UP" or "UPARROW" => "Up", "DOWN" or "DOWNARROW" => "Down", "LEFT" or "LEFTARROW" => "Left", "RIGHT" or "RIGHTARROW" => "Right",
            "SYSRQ" => "PrintScreen", "SCROLL" => "ScrollLock", "PAUSE" => "Pause",
            "LSHIFT" or "RSHIFT" => "Shift", "LCONTROL" or "RCONTROL" => "Ctrl", "LMENU" or "RMENU" => "Alt",
            _ => n.Length == 1 ? n : char.ToUpperInvariant(n[0]) + n[1..].ToLowerInvariant(),
        };
    }
}
