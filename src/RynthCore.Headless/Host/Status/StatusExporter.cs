// ============================================================================
//  RynthCore.Headless - Host/Status/StatusExporter.cs
//  What RynthRemote does for a retail client, for one headless character: the status
//  files the StatusAgent and DrakRemote read, and the command inbox they write.
//
//  Files, in the status folder, named by a per-character client id (one process hosts
//  several characters, so the process id can't be the key; ids are odd and above a billion,
//  so they never collide with a real process id):
//    RynthCore.<id>.status.json     the engine's "rynthcore.client-status/1" fields, plus
//                                   RynthRemote's bot / enchantments / opos, plus
//                                   headless: true, hostPid, hostProcess (so the agent can
//                                   check the headless process is alive), every 1 s
//    RynthCore.<id>.inventory.json  RynthAi's RynthPluginGetInventoryJson, when it changes
//    RynthCore.<id>.settings.json   RynthAi's RynthPluginGetSettingsJson, when it changes
//    RynthCore.<id>.character.json  "rynthcore.character/1" sheet, when it changes
//    commands\RynthCore.<id>.*.cmd.json   drained every 200 ms (oldest first, deleted
//                                   before they run): moveStart / moveStop / moveKeepAlive
//                                   (RemoteMove: forward = autorun, back / left / right = held
//                                   motions, the phone's dead-man), assess (identify); travel,
//                                   raise, click and hideUi are refused (no RynthNav travel or
//                                   raise senders here, nothing to click or hide); everything
//                                   else to RynthAi's RynthPluginApplyRemoteCommand.
//  Writes are tmp + replace, so readers never see a half file. Runs on the session thread.
// ============================================================================

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RynthCore.Headless.Messages;
using RynthCore.Headless.Session;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Host.Status;

public sealed unsafe class StatusExporter
{
    public const string HostProcess = "rynth-headless";

    private HostContext? _ctx;
    private readonly string _account;
    private readonly string _dir, _commands;
    private readonly long _startMs;
    private long _nextStatus, _nextExports, _nextCharacter, _nextCommands, _rateMark;
    private int _ticks, _rate;
    private string? _lastInventory, _lastSettings, _lastCharacter;
    private long _xpStart = -1, _lumStart = -1, _xpStartMs;
    private int _deathsStart = -1;

    // Manual movement from the phone: held directions and the dead-man deadline.
    private readonly RemoteMove _move = new();

    public int ClientId { get; }
    public string Account => _account;
    public string ServerName { get; set; } = "";

    public StatusExporter(string statusDir, int clientId, string account)
    {
        _dir = statusDir;
        _commands = Path.Combine(statusDir, "commands");
        ClientId = clientId;
        _account = account;
        _startMs = Now;
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_commands);
    }

    /// <summary>The current session's context; null between sessions (the status then reads "loading").</summary>
    public HostContext? Context
    {
        get => _ctx;
        set
        {
            _ctx = value;
            _move.Clear();
            _lastInventory = _lastSettings = _lastCharacter = null;
            _nextStatus = 0;
        }
    }

    private HostContext C => _ctx!;
    private static long Now => Environment.TickCount64;

    /// <summary>
    /// A client id unique to this process and character slot (0..49). Odd, so never a real process id
    /// (Windows pids are multiples of 4), and above a billion, so it reads as synthetic at a glance.
    /// </summary>
    public static int MakeClientId(int slot) => 1_000_000_001 + Environment.ProcessId % 1_000_000 * 100 + slot % 50 * 2;

    public void Tick()
    {
        long now = Now;
        _ticks++;
        if (now - _rateMark >= 1000)
        {
            _rate = _ticks;
            _ticks = 0;
            _rateMark = now;
        }
        try
        {
            if (_ctx == null)
            {
                if (now >= _nextStatus) { _nextStatus = now + 1000; WriteAtomic($"RynthCore.{ClientId}.status.json", OfflineJson()); }
                return;
            }
            if (now >= _nextCommands) { _nextCommands = now + 200; DrainCommands(); }
            TickMove(now);
            if (now >= _nextStatus) { _nextStatus = now + 1000; WriteAtomic($"RynthCore.{ClientId}.status.json", StatusJson()); }
            if (now >= _nextExports)
            {
                _nextExports = now + 2000;
                WriteIfChanged("RynthPluginGetInventoryJson", $"RynthCore.{ClientId}.inventory.json", ref _lastInventory);
                WriteIfChanged("RynthPluginGetSettingsJson", $"RynthCore.{ClientId}.settings.json", ref _lastSettings);
            }
            if (now >= _nextCharacter)
            {
                _nextCharacter = now + 5000;
                string? sheet = CharacterJson();
                if (sheet != null && sheet != _lastCharacter)
                {
                    WriteAtomic($"RynthCore.{ClientId}.character.json", sheet);
                    _lastCharacter = sheet;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The character is finished for good: its files go (the agent stops listing it).</summary>
    public void Retire()
    {
        foreach (string kind in new[] { "status", "inventory", "settings", "character" })
        {
            try { File.Delete(Path.Combine(_dir, $"RynthCore.{ClientId}.{kind}.json")); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ── status ──────────────────────────────────────────────────────────────

    /// <summary>Between sessions (connecting, or waiting to reconnect): reads as "loading".</summary>
    private string OfflineJson()
    {
        var buf = new ArrayBufferWriter<byte>(512);
        using (var j = new Utf8JsonWriter(buf))
        {
            j.WriteStartObject();
            j.WriteString("schema", "rynthcore.client-status/1");
            j.WriteString("ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            j.WriteString("host", Environment.MachineName);
            j.WriteNumber("pid", ClientId);
            j.WriteString("account", _account);
            j.WriteString("character", LastCharacter);
            j.WriteString("server", ServerName);
            j.WriteNumber("uptimeSec", (Now - _startMs) / 1000);
            j.WriteNumber("fps", 0);
            j.WriteNumber("pluginTicksPerSec", 0);
            j.WriteNumber("workingSetMB", Environment.WorkingSet / (1024 * 1024));
            j.WriteBoolean("inWorld", false);
            j.WriteNull("bot");
            j.WriteBoolean("headless", true);
            j.WriteNumber("hostPid", Environment.ProcessId);
            j.WriteString("hostProcess", HostProcess);
            j.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>The character name last seen in the world, or the configured one.</summary>
    public string LastCharacter { get; set; } = "";

    private string StatusJson()
    {
        WorldState w = C.World;
        ClientSession s = C.Session;
        WorldObject? me = w.PlayerObject;
        bool inWorld = s.State == SessionState.InWorld && me != null;
        var buf = new ArrayBufferWriter<byte>(4096);
        using (var j = new Utf8JsonWriter(buf))
        {
            j.WriteStartObject();
            j.WriteString("schema", "rynthcore.client-status/1");
            j.WriteString("ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            j.WriteString("host", Environment.MachineName);
            j.WriteNumber("pid", ClientId);
            j.WriteString("engineVersion", "headless " + typeof(StatusExporter).Assembly.GetName().Version);
            j.WriteNumber("apiVersion", (int)HostApi.ApiVersion);
            j.WriteString("account", C.AccountName);
            if (me?.Name is { Length: > 0 } nm) LastCharacter = nm;
            j.WriteString("character", me?.Name ?? LastCharacter);
            j.WriteString("server", ServerName.Length > 0 ? ServerName : s.WorldName);
            j.WriteNumber("uptimeSec", (Now - _startMs) / 1000);
            // No frames are drawn: the host frame rate stands in for fps, so the agent doesn't read "hung".
            j.WriteNumber("fps", inWorld ? _rate : 0);
            j.WriteNumber("pluginTicksPerSec", inWorld ? _rate : 0);
            j.WriteNumber("workingSetMB", Environment.WorkingSet / (1024 * 1024));
            j.WriteBoolean("inWorld", inWorld);
            j.WriteNumber("queueDropped", C.Events.Dropped);
            j.WriteNumber("reconciles", 0);
            j.WriteNumber("forceClears", 0);
            j.WriteBoolean("mainThreadFrozen", false);
            j.WriteNumber("mainThreadStallMs", 0);
            j.WriteNumber("pluginTicksPausedMs", 0);

            int deaths = me != null && me.TryGetInt(43, out int d) ? d : 0;           // NumDeaths
            if (_deathsStart < 0 && me != null) _deathsStart = deaths;
            j.WriteNumber("deaths", deaths);
            j.WriteNumber("deathsSession", _deathsStart < 0 ? 0 : Math.Max(0, deaths - _deathsStart));
            float vitae = inWorld ? Sheet()?.Vitae ?? 1f : 1f;
            j.WriteNumber("vitaePct", Math.Round((1 - vitae) * 100, 1));
            WriteRates(j, me);
            j.WriteNumber("burdenPct", BurdenPct(me));

            if (me?.Position is { } p)
            {
                uint lb = p.Cell >> 16;
                bool indoor = (p.Cell & 0xFFFF) >= 0x100;
                j.WriteString("area", lb.ToString("X4"));
                j.WriteString("landblock", lb.ToString("X8"));
                j.WriteBoolean("indoor", indoor);
                if (indoor)
                {
                    j.WriteNumber("wx", Math.Round(p.X + (lb >> 8) * 192.0, 2));
                    j.WriteNumber("wy", Math.Round(p.Y + (lb & 0xFF) * 192.0, 2));
                    j.WriteNumber("pz", Math.Round(p.Z, 2));
                }
                j.WriteStartObject("opos");
                j.WriteString("cell", p.Cell.ToString("X8"));
                j.WriteBoolean("indoor", indoor);
                j.WriteNumber("wx", Math.Round(p.X + (lb >> 8) * 192.0, 2));
                j.WriteNumber("wy", Math.Round(p.Y + (lb & 0xFF) * 192.0, 2));
                j.WriteNumber("z", Math.Round(p.Z, 2));
                j.WriteNumber("heading", Math.Round(ClientSession.HeadingDegrees(p), 1));
                j.WriteEndObject();
            }
            else
            {
                j.WriteString("area", "");
                j.WriteString("landblock", "");
                j.WriteBoolean("indoor", false);
            }
            j.WriteNull("lastIssue");
            j.WriteNumber("lastIssueAgeSec", -1);

            // RynthRemote's additions.
            j.WritePropertyName("bot");
            string? bot = inWorld ? PluginJson("RynthPluginGetSnapshotJson") : null;
            if (bot != null && IsJson(bot)) j.WriteRawValue(bot); else j.WriteNullValue();
            j.WriteStartArray("enchantments");
            if (inWorld)
            {
                double serverNow = C.ServerTimeNow;
                foreach (Enchantment e in w.Player.ActiveEnchantments.Where(e => e.SpellId != 666)
                             .Select(e => (e, left: Remaining(e, serverNow))).OrderBy(x => x.left < 0 ? double.MaxValue : x.left).Select(x => x.e))
                {
                    j.WriteStartObject();
                    j.WriteNumber("spellId", e.SpellId);
                    j.WriteNumber("secondsRemaining", Math.Round(Remaining(e, serverNow), 1));
                    j.WriteEndObject();
                }
            }
            j.WriteEndArray();
            j.WriteBoolean("raiseXp", false);
            j.WriteNumber("moveProto", 2);

            j.WriteBoolean("headless", true);
            j.WriteNumber("hostPid", Environment.ProcessId);
            j.WriteString("hostProcess", HostProcess);
            j.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private double Remaining(Enchantment e, double serverNow)
    {
        double expiry = C.EnchantmentExpiry(e);
        return expiry == double.MaxValue ? -1 : Math.Max(0, expiry - serverNow);
    }

    private void WriteRates(Utf8JsonWriter j, WorldObject? me)
    {
        long xp = me != null && me.TryGetInt64(1, out long x) ? x : -1;          // TotalExperience
        long lum = me != null && me.TryGetInt64(6, out long l) ? l : -1;         // AvailableLuminance
        long now = Now;
        if (xp >= 0 && (_xpStart < 0 || xp < _xpStart)) { _xpStart = xp; _lumStart = lum; _xpStartMs = now; }
        double hours = Math.Max(1.0 / 60, (now - _xpStartMs) / 3_600_000.0);
        long xpSession = xp >= 0 && _xpStart >= 0 ? xp - _xpStart : 0;
        long lumSession = lum >= 0 && _lumStart >= 0 ? Math.Max(0, lum - _lumStart) : 0;
        j.WriteNumber("xpPerHour", Math.Round(xpSession / hours));
        j.WriteNumber("luminancePerHour", Math.Round(lumSession / hours));
        j.WriteNumber("xpSession", xpSession);
    }

    private double BurdenPct(WorldObject? me)
    {
        if (me == null || !me.TryGetInt(5, out int burden)) return 0;                // EncumbranceVal
        uint str = Sheet()?.AttributeBuffed(1) ?? 0;
        double capacity = 150.0 * str;
        return capacity <= 0 ? 0 : Math.Round(burden / capacity * 100, 1);
    }

    private Player.CharacterSheet? Sheet() => C.PlayerKnown ? C.Sheet() : null;

    // ── character sheet ─────────────────────────────────────────────────────

    private string? CharacterJson()
    {
        if (!C.PlayerKnown || C.World.PlayerObject is not { } me) return null;
        Player.CharacterSheet sheet = C.Sheet();
        var buf = new ArrayBufferWriter<byte>(8192);
        using (var j = new Utf8JsonWriter(buf))
        {
            j.WriteStartObject();
            j.WriteString("schema", "rynthcore.character/1");
            j.WriteNumber("pid", ClientId);
            j.WriteString("ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            j.WriteString("name", me.Name);
            j.WriteNumber("level", me.TryGetInt(25, out int level) ? level : 0);
            j.WriteNumber("skillCredits", me.TryGetInt(24, out int credits) ? credits : 0);
            j.WriteNumber("unassignedXp", me.TryGetInt64(2, out long avail) ? avail : 0);
            j.WriteNumber("totalXp", me.TryGetInt64(1, out long total) ? total : 0);
            string[] attrNames = { "", "Strength", "Endurance", "Quickness", "Coordination", "Focus", "Self" };
            j.WriteStartArray("attributes");
            for (uint a = 1; a <= 6; a++)
            {
                if (!sheet.HasAttribute(a)) continue;
                j.WriteStartObject();
                j.WriteNumber("id", a);
                j.WriteString("name", attrNames[a]);
                j.WriteNumber("base", sheet.AttributeBase(a));
                j.WriteNumber("buffed", sheet.AttributeBuffed(a));
                j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteStartArray("vitals");
            foreach (var (v, id, name) in new[] { (1u, "health", "Health"), (2u, "stamina", "Stamina"), (3u, "mana", "Mana") })
            {
                if (!sheet.HasVital(v)) continue;
                j.WriteStartObject();
                j.WriteString("id", id);
                j.WriteString("name", name);
                j.WriteNumber("current", sheet.VitalCurrent(v));
                j.WriteNumber("max", sheet.VitalBuffedMax(v));
                j.WriteNumber("base", sheet.VitalBaseMax(v));
                j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteStartArray("skills");
            foreach (SkillInfo s in C.World.Player.SkillTable.Values.OrderBy(s => s.Id))
            {
                if (s.Training < 1) continue;
                j.WriteStartObject();
                j.WriteNumber("id", s.Id);
                j.WriteString("name", C.SkillFormulas?.Get(s.Id)?.Name ?? $"Skill {s.Id}");
                j.WriteString("training", s.Training switch { 3 => "specialized", 2 => "trained", _ => "untrained" });
                j.WriteNumber("base", sheet.SkillBase(s));
                j.WriteNumber("buffed", sheet.SkillBuffed(s));
                j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    // ── plugin exports ──────────────────────────────────────────────────────

    private string? PluginJson(string export, string plugin = "RynthAi")
    {
        PluginHost? host = C.Plugins;
        if (host == null) return null;
        IntPtr fn = host.FindExport(plugin, export);
        if (fn == IntPtr.Zero) return null;
        using HostContext.Scope _ = C.Enter();
        try
        {
            IntPtr p = ((delegate* unmanaged[Cdecl]<IntPtr>)fn)();
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }
        catch { return null; }
    }

    private void WriteIfChanged(string export, string file, ref string? last)
    {
        if (C.Session.State != SessionState.InWorld) return;
        string? json = PluginJson(export);
        if (json == null || json == last || !IsJson(json)) return;
        WriteAtomic(file, json);
        last = json;
    }

    private static bool IsJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; }
        catch (JsonException) { return false; }
    }

    private void WriteAtomic(string file, string content)
    {
        string path = Path.Combine(_dir, file);
        string tmp = path + $".{ClientId}.tmp";
        File.WriteAllText(tmp, content);
        try { File.Move(tmp, path, overwrite: true); }
        catch (IOException) { try { File.Delete(tmp); } catch (IOException) { } }
    }

    // ── commands ────────────────────────────────────────────────────────────

    private void DrainCommands()
    {
        string[] files;
        try { files = Directory.GetFiles(_commands, $"RynthCore.{ClientId}.*.cmd.json"); }
        catch (IOException) { return; }
        Array.Sort(files, StringComparer.Ordinal);
        foreach (string f in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(f);
                File.Delete(f);
            }
            catch (IOException) { continue; }
            try
            {
                using var doc = JsonDocument.Parse(text);
                string action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                string value = doc.RootElement.TryGetProperty("value", out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
                Apply(action, value);
            }
            catch (JsonException) { C.Log($"[status] unreadable command {Path.GetFileName(f)}"); }
        }
    }

    private void Apply(string action, string value)
    {
        C.Log($"[status] command {action} {value}");
        ClientSession s = C.Session;
        switch (action.ToLowerInvariant())
        {
            case "movestart":
                if (RemoteMove.TryParseStart(value, out string dir, out long? deadman)) Motor(_move.Start(dir, deadman, Now));
                break;
            case "movekeepalive":
                if (RemoteMove.TryParseKeepAlive(value, out List<string> dirs)) Motor(_move.KeepAlive(dirs, Now));
                break;
            case "movestop":
                Motor(_move.Stop(RemoteMove.ParseStop(value)));
                break;
            case "assess":
                if (uint.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
                        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                        null, out uint id) && id != 0)
                    s.Identify(id);
                break;
            case "travel":
            case "travelstop":
            case "raise":
            case "click":
            case "hideui":
                _ctx?.Log($"[status] '{action}' is not available on a headless character");
                break;
            default:
                // RynthAi's remote-command export queues it for its next tick (as RynthRemote sends it).
                SendToPlugin("RynthAi", action, value);
                break;
        }
    }

    private void TickMove(long now)
    {
        var acts = _move.Tick(now, out bool fired);
        if (!fired) return;
        Motor(acts);
        C.Log(_move.KeepAliveMode
            ? "[status] manual-move dead-man: no keep-alive from the phone - stopped"
            : "[status] manual-move dead-man: no repeat from the phone for 2 s - stopped");
    }

    private void Motor(List<(string Dir, bool On)> acts)
    {
        foreach (var (dir, on) in acts) Motor(dir, on);
    }

    private void Motor(string dir, bool on)
    {
        ClientSession s = C.Session;
        switch (dir)
        {
            case "forward": s.SetAutoRun(on); break;
            case "back": s.SetDriveMotion(MotionCommand.WalkBackwards, on); break;
            case "left": s.SetDriveMotion(MotionCommand.TurnLeft, on); break;
            case "right": s.SetDriveMotion(MotionCommand.TurnRight, on); break;
        }
    }

    private void SendToPlugin(string plugin, string action, string value)
    {
        PluginHost? host = C.Plugins;
        IntPtr fn = host?.FindExport(plugin, "RynthPluginApplyRemoteCommand") ?? IntPtr.Zero;
        if (fn == IntPtr.Zero) { C.Log($"[status] no {plugin} to take '{action}'"); return; }
        IntPtr a = Marshal.StringToHGlobalAnsi(action), v = Marshal.StringToHGlobalAnsi(value);
        using HostContext.Scope _ = C.Enter();
        try { ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)fn)(a, v); }
        catch (Exception ex) { C.Log($"[status] {plugin} command failed: {ex.Message}"); }
        finally
        {
            Marshal.FreeHGlobal(a);
            Marshal.FreeHGlobal(v);
        }
    }
}
