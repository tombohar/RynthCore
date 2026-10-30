using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RynthCore.App;

/// <summary>Registry roots the Decal check reads. "32" = the 32-bit view (WOW6432Node on 64-bit Windows).</summary>
internal enum RegRoot { Machine32, Machine64, User32, User64 }

/// <summary>A read-only registry key (real one, or a fake in tools\DecalBridgeCheckTests).</summary>
internal interface IRegKey : IDisposable
{
    object? GetValue(string name);
    string[] GetSubKeyNames();
    string[] GetValueNames();
}

/// <summary>
/// Everything <see cref="DecalBridgeCheck"/> reads from the machine. The real one is
/// <see cref="RealDecalCheckHost"/>; the offline tests use a fake.
/// </summary>
internal interface IDecalCheckHost
{
    bool Is64BitOs { get; }
    /// <summary>The launcher (or injector) runs elevated. A client it starts is elevated too.</summary>
    bool LauncherElevated { get; }
    /// <summary>UAC registry virtualization is on for this process (as it is for a non-elevated AC).</summary>
    bool LauncherVirtualized { get; }
    /// <summary>The real registry: no UAC virtualization (HKLM is HKLM, the VirtualStore is its own key).</summary>
    IRegKey? OpenReal(RegRoot root, string subKey);
    /// <summary>
    /// HKLM (32-bit view) as THIS process sees it, virtualized when the process is. Only the
    /// bridge's own entry is asked for (the real host snapshots it before virtualization is
    /// switched off for the rest of the check).
    /// </summary>
    IRegKey? OpenOwnViewMachine32(string subKey);
    bool FileExists(string path);
    /// <summary>The file carries a Zone.Identifier stream ("downloaded from the internet").</summary>
    bool HasZoneIdentifier(string path);
    Version? AssemblyVersionOf(string path);
    Version? ReferenceVersionOf(string assemblyPath, string referenceName);
    /// <summary>requestedExecutionLevel from the exe's embedded manifest or its .manifest file, null if none.</summary>
    string? ExecutionLevelOf(string exePath);
    string? ReadTextFile(string path);
}

/// <summary>
/// "Check Decal bridge": can a Decal + RynthCore client started now load the RynthCore Decal
/// bridge? (docs/DECAL_BRIDGE_PLAN.md, "Where Decal reads its filters").
///
/// Decal lists network filters from HKLM\SOFTWARE\Decal\NetworkFilters (decalnet.dll), read
/// by the 32-bit client through the 32-bit view (WOW6432Node), and Decal.Adapter reads each
/// .NET filter's Path/Assembly/Object from SOFTWARE\Decal\NetworkFilters\{id}. Which key that
/// really is depends on the CLIENT's token:
///   - a non-elevated acclient.exe (no manifest, UAC on) is virtualized: when the per-user copy
///     HKCU\Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\Decal\NetworkFilters
///     exists, Decal reads ONLY that copy; otherwise the real HKLM key;
///   - an elevated client (the launcher runs as administrator, acclient.exe set to "Run as
///     administrator", UAC off), or one with a manifest, or with virtualization off by policy,
///     is not virtualized and reads the real HKLM key - a per-user registration is invisible.
/// The check predicts that, reads our entry from where Decal will read it, and where it can
/// (the launcher's token is virtualized exactly when the client's will be) reads it again
/// through the launcher's own view, which is the client's view.
/// </summary>
internal static class DecalBridgeCheck
{
    public enum Level { Ok, Info, Warn, Fail }

    public sealed record Line(Level Level, string Text);

    public sealed class Result
    {
        public List<Line> Lines { get; } = new();
        public bool DecalInstalled { get; set; }
        public string? DecalVersion { get; set; }
        public bool ClientVirtualized { get; set; }
        public string ClientReason { get; set; } = string.Empty;
        /// <summary>Where Decal reads its filter list on this machine, for people.</summary>
        public string FiltersReadFrom { get; set; } = string.Empty;
        public bool ReadsPerUserCopy { get; set; }
        /// <summary>Our entry is where Decal reads, enabled, with the DLL in place.</summary>
        public bool BridgeVisibleToDecal { get; set; }
        /// <summary>Not registered where Decal reads, and that place is HKLM: needs administrator rights once.</summary>
        public bool NeedsMachineWideRegistration { get; set; }
        public bool Blocking => Lines.Any(l => l.Level == Level.Fail);

        public void Add(Level level, string text) => Lines.Add(new Line(level, text));

        public string Summary => Blocking
            ? "Decal bridge check: FAILED - " + string.Join(" ", Lines.Where(l => l.Level == Level.Fail).Select(l => l.Text))
            : "Decal bridge check: OK - " + FiltersReadFrom + (DecalVersion != null ? $"; Decal {DecalVersion}" : "");

        public string ToReport()
        {
            var sb = new StringBuilder();
            foreach (Line l in Lines)
            {
                string tag = l.Level switch { Level.Ok => "OK  ", Level.Info => "    ", Level.Warn => "WARN", _ => "FAIL" };
                sb.Append(tag).Append("  ").AppendLine(l.Text);
            }
            sb.AppendLine();
            sb.AppendLine(Blocking
                ? "Result: a Decal + RynthCore client started now would NOT load the bridge (no RynthCore overlay, then a crash). Fix the FAIL lines first."
                : "Result: a Decal + RynthCore client started now will load the bridge.");
            return sb.ToString();
        }
    }

    /// <summary>Decal's .NET surrogate CLSID (Decal.Adapter.Surrogate).</summary>
    public const string SurrogateClsid = "{71A69713-6593-47EC-0002-0000000DECA1}";
    /// <summary>The bridge targets .NET Framework 4.8 (Release key 528040).</summary>
    private const int Net48Release = 528040;

    public static string VirtualStoreDecalPath(bool is64BitOs, string group) => is64BitOs
        ? @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\Decal\" + group
        : @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\Decal\" + group;

    public static string MachineDecalPath(string group) => @"SOFTWARE\Decal\" + group;

    /// <summary>
    /// Will a client started by this launcher get UAC registry virtualization? False when it
    /// runs elevated or Windows doesn't virtualize it (reason says which).
    /// </summary>
    public static bool PredictClientVirtualized(IDecalCheckHost host, string? acClientPath, out string reason)
    {
        int? lua = DwordOf(host, RegRoot.Machine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
        if (lua == 0)
        {
            reason = "UAC is turned off on this PC (EnableLUA=0), so Windows gives every program the real machine registry";
            return false;
        }
        int? virt = DwordOf(host, RegRoot.Machine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableVirtualization");
        if (virt == 0)
        {
            reason = "UAC registry virtualization is turned off by policy (EnableVirtualization=0)";
            return false;
        }
        if (host.LauncherElevated)
        {
            reason = "RynthCore runs as administrator, so the AC client it starts does too, and Windows gives it the real machine registry";
            return false;
        }
        if (!string.IsNullOrEmpty(acClientPath))
        {
            string? level = host.ExecutionLevelOf(acClientPath);
            if (level != null)
            {
                reason = $"{Path.GetFileName(acClientPath)} has a manifest (requestedExecutionLevel={level}), so Windows doesn't virtualize it";
                return false;
            }
            string? layer = RunAsAdminLayer(host, acClientPath);
            if (layer != null)
            {
                reason = $"{acClientPath} is set to \"Run this program as an administrator\" ({layer})";
                return false;
            }
        }
        reason = "AC runs without administrator rights, so Windows virtualizes its registry";
        return true;
    }

    /// <summary>
    /// Where a client (virtualized or not) reads SOFTWARE\Decal\&lt;group&gt;: the per-user
    /// VirtualStore copy when it is virtualized and the copy exists, else the real HKLM key.
    /// </summary>
    public static (RegRoot Root, string Path, bool PerUser) WhereDecalReads(IDecalCheckHost host, bool clientVirtualized, string group)
    {
        if (clientVirtualized)
        {
            string vs = VirtualStoreDecalPath(host.Is64BitOs, group);
            using IRegKey? k = host.OpenReal(RegRoot.User64, vs);
            if (k != null)
                return (RegRoot.User64, vs, true);
        }
        return (RegRoot.Machine32, MachineDecalPath(group), false);
    }

    /// <summary>
    /// Where a registration goes (the launcher's, and the installer's for a user with a
    /// Decal + RynthCore account: installer\RynthCore.iss, <see cref="DecalBridgeRegistration.RegisterMachineWide"/>
    /// elevated, <see cref="DecalBridgeRegistration.InstallPerUser"/> as the installing user):
    ///   - Machine: always (when Decal is installed). Elevated and unvirtualized clients read
    ///     only HKLM; a virtualized client without a per-user copy reads HKLM too.
    ///   - PerUser: only into a per-user copy that ALREADY exists. A virtualized client then
    ///     reads that copy and nothing else, so the HKLM entry is hidden from it. A copy is never
    ///     created: it would hide every HKLM filter added later (a Decal plugin installed for all
    ///     users) from this user's non-elevated clients. Repair Decal removes an existing one.
    /// </summary>
    public static (bool Machine, bool PerUser) PlanInstall(IDecalCheckHost host)
    {
        bool decal = StringOf(host, RegRoot.Machine32, MachineDecalPath("Agent"), "AgentPath") != null;
        if (!decal) return (false, false);
        using IRegKey? copy = host.OpenReal(RegRoot.User64, VirtualStoreDecalPath(host.Is64BitOs, "NetworkFilters"));
        return (true, copy != null);
    }

    public static string Describe(bool is64BitOs, RegRoot root, string path) => root switch
    {
        RegRoot.Machine32 => is64BitOs ? @"HKLM\SOFTWARE\WOW6432Node\" + path.Substring("SOFTWARE\\".Length) : @"HKLM\" + path,
        RegRoot.Machine64 => @"HKLM\" + path,
        _ => @"HKCU\" + path,
    };

    /// <summary>Runs every check. Reads only; changes nothing.</summary>
    public static Result Run(IDecalCheckHost host, string bridgeDirectory, string? acClientPath, string? engineJsonPath)
    {
        var r = new Result();
        string dir = bridgeDirectory.TrimEnd('\\');

        // 1. Decal and its version.
        string? agentPath = StringOf(host, RegRoot.Machine32, MachineDecalPath("Agent"), "AgentPath");
        r.DecalInstalled = agentPath != null && host.FileExists(Path.Combine(agentPath, "Inject.dll"));
        if (!r.DecalInstalled)
        {
            r.Add(Level.Fail, @"Decal is not installed (HKLM\SOFTWARE\Decal\Agent AgentPath\Inject.dll not found).");
            return r;
        }
        string adapter = Path.Combine(agentPath!, "Decal.Adapter.dll");
        Version? installed = host.FileExists(adapter) ? host.AssemblyVersionOf(adapter) : null;
        string bridgeDll = Path.Combine(dir, DecalBridgeRegistration.AssemblyFile);
        bool bridgePresent = host.FileExists(bridgeDll);
        Version? needed = bridgePresent ? host.ReferenceVersionOf(bridgeDll, "Decal.Adapter") : null;
        r.DecalVersion = installed?.ToString();
        if (installed == null)
            r.Add(Level.Fail, $"Decal.Adapter.dll is missing or unreadable in {agentPath} - reinstall Decal.");
        else if (needed != null && installed < needed)
            r.Add(Level.Fail, $"Decal {installed} is older than the {needed} the bridge is built for - update Decal (decaldev.com), then check again.");
        else
            r.Add(Level.Ok, $"Decal {installed} in {agentPath}" + (needed != null ? $" (bridge built for {needed})." : "."));

        // 2. The bridge DLL itself.
        if (!bridgePresent)
            r.Add(Level.Fail, $"{DecalBridgeRegistration.AssemblyFile} is missing from {dir} - reinstall RynthCore.");
        else if (host.HasZoneIdentifier(bridgeDll))
            r.Add(Level.Warn, $"{bridgeDll} is marked as downloaded from the internet; .NET may refuse to load it. Repair removes the mark (or: right-click > Properties > Unblock).");
        else
            r.Add(Level.Ok, $"Bridge DLL present: {bridgeDll}.");

        // 3. How the client will see the registry.
        r.ClientVirtualized = PredictClientVirtualized(host, acClientPath, out string why);
        r.ClientReason = why;
        r.Add(Level.Info, "AC client: " + why + ".");

        // 4. Where Decal reads its filters, and our entry there.
        var where = WhereDecalReads(host, r.ClientVirtualized, "NetworkFilters");
        r.ReadsPerUserCopy = where.PerUser;
        string whereText = Describe(host.Is64BitOs, where.Root, where.Path);
        r.FiltersReadFrom = "Decal reads its filters from " + whereText + (where.PerUser ? " (your Windows user's copy)" : "");
        r.Add(Level.Info, r.FiltersReadFrom + ".");

        string? entryProblem = EntryProblem(host, where.Root, where.Path + "\\" + DecalBridgeRegistration.FilterGuid, out string? registeredPath);
        if (entryProblem == null)
        {
            r.BridgeVisibleToDecal = true;
            r.Add(Level.Ok, $"RynthCore Decal Bridge is registered there and enabled ({registeredPath}).");
        }
        else
        {
            r.NeedsMachineWideRegistration = !where.PerUser;
            r.Add(Level.Fail, $"Decal will not load the bridge: {entryProblem} in {whereText}." +
                (where.PerUser
                    ? " Repair (or a Decal + RynthCore launch) adds it there and to the list for all users (Windows may ask for administrator permission once)."
                    : " That key is shared by all users: Repair adds it there once and Windows asks for administrator permission."));
        }

        // Other places, for people (a registration Decal won't read explains a "registered but nothing happens").
        if (where.PerUser)
        {
            using IRegKey? m = host.OpenReal(RegRoot.Machine32, MachineDecalPath("NetworkFilters") + "\\" + DecalBridgeRegistration.FilterGuid);
            if (m != null && IsOurs(m) && entryProblem != null)
                r.Add(Level.Info, "(A machine-wide entry exists, but your per-user copy hides it from AC.)");
        }
        else
        {
            using IRegKey? u = host.OpenReal(RegRoot.User64, VirtualStoreDecalPath(host.Is64BitOs, "NetworkFilters") + "\\" + DecalBridgeRegistration.FilterGuid);
            if (u != null && IsOurs(u) && entryProblem != null)
                r.Add(Level.Info, "(A per-user registration exists, but this AC client doesn't read it: " + why + ".)");
        }

        // 5. The same entry through this process's own view, when that view IS the client's.
        bool sameView = host.LauncherVirtualized == r.ClientVirtualized;
        if (sameView)
        {
            using IRegKey? live = host.OpenOwnViewMachine32(MachineDecalPath("NetworkFilters") + "\\" + DecalBridgeRegistration.FilterGuid);
            bool liveOk = live != null && IsOurs(live) && IsEnabled(live);
            if (liveOk == r.BridgeVisibleToDecal)
                r.Add(Level.Ok, $"Read back as AC will see it: {(liveOk ? "bridge listed and enabled" : "bridge not listed")}.");
            else
                r.Add(liveOk ? Level.Warn : Level.Fail,
                    $"Read back as AC will see it: {(liveOk ? "bridge listed" : "bridge NOT listed")}, which disagrees with {whereText}. Windows serves this PC's registry differently than expected.");
        }
        else
        {
            r.Add(Level.Info, "Not read back live: AC will run with other rights than this launcher.");
        }

        // 6. Hidden machine filters (a per-user list hides every HKLM entry it lacks).
        if (where.PerUser)
        {
            List<string> hidden = HiddenMachineFilters(host, where.Path);
            if (hidden.Count > 0)
                r.Add(Level.Warn, "Hidden from AC by your per-user filter list (in HKLM, not in the copy): " + string.Join(", ", hidden) +
                    ". If 'World Object Filter' is among them, VTank won't start. Recovery Actions > Repair Decal removes the per-user copy (after a backup).");
        }

        // 7. Decal's .NET surrogate: Decal must be able to create it, and must not have it disabled.
        var surWhere = WhereDecalReads(host, r.ClientVirtualized, "Surrogates");
        using (IRegKey? s = host.OpenReal(surWhere.Root, surWhere.Path + "\\" + SurrogateClsid))
        {
            if (s == null)
                r.Add(Level.Fail, $"Decal's .NET surrogate is not listed in {Describe(host.Is64BitOs, surWhere.Root, surWhere.Path)} - no .NET filter or plugin can load. Reinstall Decal.");
            else if (!IsEnabled(s))
                r.Add(Level.Fail, "Decal's .NET surrogate (Decal.Adapter Surrogate) is disabled in Decal - enable it in Decal's agent (or reinstall Decal).");
            else
                r.Add(Level.Ok, "Decal's .NET surrogate is enabled.");
        }
        string? comProblem = SurrogateComProblem(host, clientElevated: !r.ClientVirtualized && IsClientElevated(host, acClientPath), out string comWhere);
        if (comProblem != null)
            r.Add(Level.Fail, comProblem);
        else
            r.Add(Level.Ok, $"Decal.Adapter's COM class resolves ({comWhere}). The bridge itself needs no COM registration: Decal.Adapter loads it from Path\\Assembly.");

        // 8. .NET Framework 4.8.
        int? release = DwordOf(host, RegRoot.Machine32, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release");
        if (release == null)
            r.Add(Level.Fail, ".NET Framework 4 is not installed (Decal needs it).");
        else if (release < Net48Release)
            r.Add(Level.Warn, $".NET Framework older than 4.8 (Release {release}); the bridge targets 4.8 - install .NET Framework 4.8.");

        // 9. engine.json settings that turn bridge mode off.
        if (engineJsonPath != null)
        {
            (string? mode, bool? inGame) = ReadEngineSettings(host, engineJsonPath);
            if (mode != null && (mode.Equals("Off", StringComparison.OrdinalIgnoreCase) || mode.Equals("false", StringComparison.OrdinalIgnoreCase)))
                r.Add(Level.Fail, $"engine.json has \"DecalBridge\": {mode} - bridge mode is off for every client. Remove that line ({engineJsonPath}).");
            if (inGame == false)
                r.Add(Level.Warn, "engine.json has \"DecalInGameImGui\": false - no RynthCore overlay under Decal (by choice).");
        }
        return r;
    }

    // ── pieces (internal for the tests) ─────────────────────────────────────

    internal static string? EntryProblem(IDecalCheckHost host, RegRoot root, string entryPath, out string? registeredPath)
    {
        registeredPath = null;
        using IRegKey? k = host.OpenReal(root, entryPath);
        if (k == null) return "the RynthCore Decal Bridge is not registered";
        if (!IsOurs(k)) return "another filter uses the bridge's id";
        registeredPath = k.GetValue("Path") as string;
        if (!IsEnabled(k)) return "the RynthCore Decal Bridge is registered but disabled";
        if (!string.Equals(k.GetValue("Surrogate") as string, SurrogateClsid, StringComparison.OrdinalIgnoreCase))
            return "the bridge entry has the wrong Surrogate";
        string? file = k.GetValue("Assembly") as string;
        if (string.IsNullOrEmpty(registeredPath) || string.IsNullOrEmpty(file))
            return "the bridge entry has no Path/Assembly";
        if (!host.FileExists(Path.Combine(registeredPath, file)))
            return $"the registered bridge DLL is missing ({Path.Combine(registeredPath, file)})";
        return null;
    }

    internal static List<string> HiddenMachineFilters(IDecalCheckHost host, string perUserPath)
    {
        var hidden = new List<string>();
        using IRegKey? machine = host.OpenReal(RegRoot.Machine32, MachineDecalPath("NetworkFilters"));
        using IRegKey? user = host.OpenReal(RegRoot.User64, perUserPath);
        if (machine == null || user == null) return hidden;
        var seen = new HashSet<string>(user.GetSubKeyNames(), StringComparer.OrdinalIgnoreCase);
        foreach (string name in machine.GetSubKeyNames())
        {
            if (seen.Contains(name) || string.Equals(name, DecalBridgeRegistration.FilterGuid, StringComparison.OrdinalIgnoreCase))
                continue;
            using IRegKey? k = host.OpenReal(RegRoot.Machine32, MachineDecalPath("NetworkFilters") + "\\" + name);
            hidden.Add(k?.GetValue("") is string friendly && friendly.Length > 0 ? friendly : name);
        }
        return hidden;
    }

    /// <summary>
    /// Decal.Adapter's surrogate is an mscoree COM class. A non-elevated client resolves it
    /// per-user first (HKCU\Software\Classes), then machine-wide; an elevated one ignores
    /// per-user COM registrations.
    /// </summary>
    internal static string? SurrogateComProblem(IDecalCheckHost host, bool clientElevated, out string where)
    {
        where = string.Empty;
        string sub = @"\CLSID\" + SurrogateClsid + @"\InprocServer32";
        var candidates = new List<(RegRoot Root, string Path, string Label)>();
        if (!clientElevated) candidates.Add((RegRoot.User32, @"Software\Classes" + sub, "per-user"));
        candidates.Add((RegRoot.Machine32, @"SOFTWARE\Classes" + sub, "machine-wide"));
        foreach (var c in candidates)
        {
            using IRegKey? k = host.OpenReal(c.Root, c.Path);
            if (k == null) continue;
            where = c.Label;
            string server = k.GetValue("") as string ?? string.Empty;
            if (!server.EndsWith("mscoree.dll", StringComparison.OrdinalIgnoreCase))
                return $"Decal.Adapter's COM class ({c.Label}) points at '{server}', not mscoree.dll - reinstall Decal.";
            if (string.IsNullOrEmpty(k.GetValue("Assembly") as string))
                return $"Decal.Adapter's COM class ({c.Label}) names no assembly - reinstall Decal.";
            if (k.GetValue("CodeBase") is string cb && cb.Length > 0)
            {
                string file = cb.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? cb.Substring(5).TrimStart('/') : cb;
                if (!host.FileExists(file.Replace('/', '\\')))
                    return $"Decal.Adapter's COM class ({c.Label}) points at a missing file ({file}) - reinstall Decal.";
            }
            return null;
        }
        bool perUserOnly = clientElevated && OpenExists(host, RegRoot.User32, @"Software\Classes" + sub);
        return perUserOnly
            ? "Decal.Adapter's COM class is registered only for your Windows user, and an AC running as administrator ignores per-user COM classes - reinstall Decal for all users."
            : "Decal.Adapter's COM class (Decal.Adapter.Surrogate) is not registered - reinstall Decal.";
    }

    private static bool IsClientElevated(IDecalCheckHost host, string? acClientPath)
    {
        if (host.LauncherElevated) return true;
        if (string.IsNullOrEmpty(acClientPath)) return false;
        string? level = host.ExecutionLevelOf(acClientPath);
        if (level != null && (level.Equals("requireAdministrator", StringComparison.OrdinalIgnoreCase) || level.Equals("highestAvailable", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (RunAsAdminLayer(host, acClientPath) != null) return true;
        // UAC off: an administrator's programs run with the full token.
        return DwordOf(host, RegRoot.Machine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA") == 0;
    }

    private static string? RunAsAdminLayer(IDecalCheckHost host, string exePath)
    {
        foreach ((RegRoot root, string key) in new[]
        {
            (RegRoot.User64, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"),
            (RegRoot.Machine64, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"),
        })
        {
            using IRegKey? k = host.OpenReal(root, key);
            if (k == null) continue;
            foreach (string name in k.GetValueNames())
            {
                if (!string.Equals(name, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                string flags = k.GetValue(name) as string ?? string.Empty;
                string[] parts = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(p => p.Equals("RUNASADMIN", StringComparison.OrdinalIgnoreCase) || p.Equals("RUNASHIGHEST", StringComparison.OrdinalIgnoreCase)))
                    return (root == RegRoot.User64 ? "your compatibility settings" : "compatibility settings for all users") + ": " + flags.Trim();
            }
        }
        return null;
    }

    private static (string? Mode, bool? InGame) ReadEngineSettings(IDecalCheckHost host, string path)
    {
        try
        {
            string? text = host.ReadTextFile(path);
            if (text == null) return (null, null);
            using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            string? mode = null;
            bool? inGame = null;
            if (doc.RootElement.TryGetProperty("DecalBridge", out JsonElement m))
                mode = m.ValueKind switch { JsonValueKind.String => m.GetString(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null };
            if (doc.RootElement.TryGetProperty("DecalInGameImGui", out JsonElement g) && (g.ValueKind == JsonValueKind.True || g.ValueKind == JsonValueKind.False))
                inGame = g.GetBoolean();
            return (mode, inGame);
        }
        catch
        {
            return (null, null);
        }
    }

    internal static bool IsOurs(IRegKey k) =>
        string.Equals(k.GetValue("Object") as string, DecalBridgeRegistration.FilterObject, StringComparison.Ordinal);

    internal static bool IsEnabled(IRegKey k) => k.GetValue("Enabled") is int e && e != 0;

    private static bool OpenExists(IDecalCheckHost host, RegRoot root, string path)
    {
        using IRegKey? k = host.OpenReal(root, path);
        return k != null;
    }

    private static int? DwordOf(IDecalCheckHost host, RegRoot root, string path, string name)
    {
        using IRegKey? k = host.OpenReal(root, path);
        return k?.GetValue(name) is int v ? v : null;
    }

    private static string? StringOf(IDecalCheckHost host, RegRoot root, string path, string name)
    {
        using IRegKey? k = host.OpenReal(root, path);
        return k?.GetValue(name) is string s && s.Length > 0 ? s : null;
    }
}
