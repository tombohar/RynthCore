using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RynthCore.App;

/// <summary>A registry key copied into memory (also the tests' fake key).</summary>
internal sealed class RegSnapshot : IRegKey
{
    public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SubKeys { get; } = new();

    public object? GetValue(string name) => Values.TryGetValue(name, out object? v) ? v : null;
    public string[] GetSubKeyNames() => SubKeys.ToArray();
    public string[] GetValueNames() => new List<string>(Values.Keys).ToArray();
    public void Dispose() { }

    public static RegSnapshot? Of(RegistryKey? key)
    {
        if (key == null) return null;
        var s = new RegSnapshot();
        foreach (string n in key.GetValueNames())
        {
            object? v = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v != null) s.Values[n] = v;
        }
        s.SubKeys.AddRange(key.GetSubKeyNames());
        return s;
    }
}

/// <summary>The real machine for <see cref="DecalBridgeCheck"/>.</summary>
internal sealed class RealDecalCheckHost : IDecalCheckHost
{
    private const int TokenElevation = 20, TokenVirtualizationEnabled = 24;
    private static readonly string BridgeEntryMachinePath =
        DecalBridgeCheck.MachineDecalPath("NetworkFilters") + "\\" + DecalBridgeRegistration.FilterGuid;

    private readonly RegSnapshot? _ownViewBridgeEntry;
    private readonly Dictionary<string, string?> _levels = new(StringComparer.OrdinalIgnoreCase);

    public bool Is64BitOs => Environment.Is64BitOperatingSystem;
    public bool LauncherElevated { get; }
    public bool LauncherVirtualized { get; }

    /// <summary>Construct OUTSIDE a <see cref="RealRegistryView"/> scope: it reads the process's own view.</summary>
    private RealDecalCheckHost()
    {
        LauncherElevated = TokenFlag(TokenElevation);
        LauncherVirtualized = TokenFlag(TokenVirtualizationEnabled);
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Is64BitOs ? RegistryView.Registry32 : RegistryView.Default);
            using RegistryKey? k = hklm.OpenSubKey(BridgeEntryMachinePath);
            _ownViewBridgeEntry = RegSnapshot.Of(k);
        }
        catch { _ownViewBridgeEntry = null; }
    }

    /// <summary>Runs the check against this machine. Reads only.</summary>
    public static DecalBridgeCheck.Result Check(string bridgeDirectory, string? acClientPath, string? engineJsonPath)
    {
        var host = new RealDecalCheckHost();
        using (RealRegistryView.Enter())
            return DecalBridgeCheck.Run(host, bridgeDirectory, acClientPath, engineJsonPath);
    }

    /// <summary>Only the client-virtualization prediction (registration uses it to pick the key).</summary>
    public static bool PredictClientVirtualized(string? acClientPath, out string reason, out bool launcherElevated)
    {
        var host = new RealDecalCheckHost();
        launcherElevated = host.LauncherElevated;
        using (RealRegistryView.Enter())
            return DecalBridgeCheck.PredictClientVirtualized(host, acClientPath, out reason);
    }

    public static bool IsProcessElevated() => TokenFlag(TokenElevation);

    /// <summary><see cref="DecalBridgeCheck.PlanInstall"/> on this machine.</summary>
    public static (bool Machine, bool PerUser) PlanInstall()
    {
        var host = new RealDecalCheckHost();
        using (RealRegistryView.Enter())
            return DecalBridgeCheck.PlanInstall(host);
    }

    public IRegKey? OpenReal(RegRoot root, string subKey)
    {
        try
        {
            (RegistryHive hive, RegistryView view) = root switch
            {
                RegRoot.Machine32 => (RegistryHive.LocalMachine, Is64BitOs ? RegistryView.Registry32 : RegistryView.Default),
                RegRoot.Machine64 => (RegistryHive.LocalMachine, Is64BitOs ? RegistryView.Registry64 : RegistryView.Default),
                RegRoot.User32 => (RegistryHive.CurrentUser, Is64BitOs ? RegistryView.Registry32 : RegistryView.Default),
                _ => (RegistryHive.CurrentUser, Is64BitOs ? RegistryView.Registry64 : RegistryView.Default),
            };
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? k = baseKey.OpenSubKey(subKey);
            return RegSnapshot.Of(k);
        }
        catch
        {
            return null;
        }
    }

    public IRegKey? OpenOwnViewMachine32(string subKey) =>
        string.Equals(subKey, BridgeEntryMachinePath, StringComparison.OrdinalIgnoreCase) ? _ownViewBridgeEntry : null;

    public bool FileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    public bool HasZoneIdentifier(string path)
    {
        try { return File.Exists(path + ":Zone.Identifier"); } catch { return false; }
    }

    /// <summary>Removes the "downloaded from the internet" mark. True if none is left.</summary>
    public static bool RemoveZoneIdentifier(string path)
    {
        try
        {
            string ads = path + ":Zone.Identifier";
            if (File.Exists(ads)) File.Delete(ads);
            return !File.Exists(ads);
        }
        catch { return false; }
    }

    public Version? AssemblyVersionOf(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version; } catch { return null; }
    }

    public Version? ReferenceVersionOf(string assemblyPath, string referenceName)
    {
        try
        {
            using FileStream fs = File.OpenRead(assemblyPath);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return null;
            MetadataReader md = pe.GetMetadataReader();
            foreach (AssemblyReferenceHandle h in md.AssemblyReferences)
            {
                AssemblyReference r = md.GetAssemblyReference(h);
                if (string.Equals(md.GetString(r.Name), referenceName, StringComparison.OrdinalIgnoreCase))
                    return r.Version;
            }
        }
        catch { }
        return null;
    }

    public string? ExecutionLevelOf(string exePath)
    {
        if (_levels.TryGetValue(exePath, out string? cached)) return cached;
        string? level = null;
        try
        {
            if (File.Exists(exePath))
                level = LevelIn(Encoding.UTF8.GetString(File.ReadAllBytes(exePath)));
            // An external manifest counts only when the exe has none embedded.
            if (level == null && File.Exists(exePath + ".manifest"))
                level = LevelIn(File.ReadAllText(exePath + ".manifest"));
        }
        catch { }
        _levels[exePath] = level;
        return level;
    }

    /// <summary>The level="..." of a requestedExecutionLevel element, or null.</summary>
    internal static string? LevelIn(string text)
    {
        int i = text.IndexOf("requestedExecutionLevel", StringComparison.Ordinal);
        if (i < 0) return null;
        int end = text.IndexOf('>', i);
        string element = end > i ? text.Substring(i, end - i) : text.Substring(i, Math.Min(200, text.Length - i));
        int l = element.IndexOf("level=", StringComparison.Ordinal);
        if (l < 0) return null;
        int q = l + 6;
        if (q >= element.Length) return null;
        char quote = element[q];
        int close = element.IndexOf(quote, q + 1);
        return close > q ? element.Substring(q + 1, close - q - 1) : null;
    }

    public string? ReadTextFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
    }

    private static bool TokenFlag(int infoClass)
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return false;
            try
            {
                int v = 0;
                return GetTokenInformation(token, infoClass, ref v, sizeof(int), out _) && v != 0;
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    private const uint TOKEN_QUERY = 0x0008;
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, ref int info, int length, out int returned);
}

/// <summary>
/// The launcher has no requestedExecutionLevel in its manifest, so Windows runs it with UAC
/// registry virtualization: its HKLM reads return the per-user VirtualStore copy where one
/// exists, and its HKLM writes land there silently (measured 2026-09-30). Registration and
/// the check have to see (and write) the real HKLM and the real per-user copy, so
/// virtualization is switched off for this process while they run and switched back on
/// after. A process without virtualization (the injector, an elevated launcher) is left alone.
/// </summary>
internal sealed class RealRegistryView : IDisposable
{
    private const int TokenVirtualizationEnabled = 24;
    private const uint TOKEN_QUERY = 0x0008, TOKEN_ADJUST_DEFAULT = 0x0080;
    private IntPtr _token;

    public static IDisposable Enter()
    {
        var scope = new RealRegistryView();
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_ADJUST_DEFAULT, out IntPtr token))
                return scope;
            int on = 0;
            if (GetTokenInformation(token, TokenVirtualizationEnabled, ref on, sizeof(int), out _) && on != 0)
            {
                int off = 0;
                if (SetTokenInformation(token, TokenVirtualizationEnabled, ref off, sizeof(int)))
                {
                    scope._token = token;   // restored in Dispose
                    return scope;
                }
            }
            CloseHandle(token);
        }
        catch { }
        return scope;
    }

    /// <summary>
    /// UAC registry virtualization is on for this process right now (outside an
    /// <see cref="Enter"/> scope, or when switching it off failed). An HKLM write would then
    /// land in the per-user VirtualStore copy, which registration must never create.
    /// Fails closed: true when the token can't be read.
    /// </summary>
    public static bool ProcessVirtualized()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return true;
            try
            {
                int on = 0;
                return !GetTokenInformation(token, TokenVirtualizationEnabled, ref on, sizeof(int), out _) || on != 0;
            }
            finally { CloseHandle(token); }
        }
        catch { return true; }
    }

    public void Dispose()
    {
        if (_token == IntPtr.Zero) return;
        int on = 1;
        SetTokenInformation(_token, TokenVirtualizationEnabled, ref on, sizeof(int));
        CloseHandle(_token);
        _token = IntPtr.Zero;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, ref int info, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref int info, int length);
}
