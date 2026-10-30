using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Contracts;

namespace Shim;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct HostApi
{
    public int Size;
    public delegate* unmanaged[Stdcall]<double*, double> FreeMb;
    public delegate* unmanaged[Stdcall]<byte*, void> Log;
    public delegate* unmanaged[Stdcall]<nint, int, int, int> CallBinary;
    public delegate* unmanaged[Stdcall]<uint> CommittedMb;
}

public static unsafe class Entry
{
    private static HostApi* s_api;

    private static void Log(string msg)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(msg + "\0");
        fixed (byte* p = bytes) s_api->Log(p);
    }

    private static (double free, double largest) Free()
    {
        double largest;
        double free = s_api->FreeMb(&largest);
        return (free, largest);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int Run(HostApi* api, int cycles, int mode)
    {
        s_api = api;
        try
        {
            string dir = Path.GetDirectoryName(typeof(Entry).Assembly.Location)!;
            string payloadPath = Path.Combine(dir, "payload", "Payload.dll");
            Log($"Shim up: CLR {Environment.Version}, {RuntimeInformation.ProcessArchitecture}, mode 0x{mode:x}, {cycles} cycles, payload {payloadPath}");

            var (free0, _) = Free();
            uint commit0 = s_api->CommittedMb();
            double firstGenFree = 0;
            int failures = 0;
            for (int i = 1; i <= cycles; i++)
            {
                var sw = Stopwatch.StartNew();
                WeakReference alcRef = RunGeneration(i, payloadPath, mode, out string report);
                long loadMs = sw.ElapsedMilliseconds;
                sw.Restart();
                int collections = 0;
                while (alcRef.IsAlive && sw.ElapsedMilliseconds < 5000)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    collections++;
                    if (alcRef.IsAlive) Thread.Sleep(5);
                }
                GC.Collect();
                bool freed = !alcRef.IsAlive;
                if (!freed) failures++;
                var (free, largest) = Free();
                if (i == 1) firstGenFree = free;
                Log($"gen {i,2}: {report} | load+run {loadMs} ms | alc {(freed ? "freed" : "NOT freed")} after {collections} GCs, {sw.ElapsedMilliseconds} ms | free {free:F0} MB (largest {largest:F0}), vs gen1 {free - firstGenFree:+0;-0} MB | commit {s_api->CommittedMb()} MB | gc heap {GC.GetTotalMemory(false) / 1048576.0:F1} MB");
            }
            var (freeEnd, _) = Free();
            Log($"done: {cycles} cycles, {failures} not freed; free {free0:F0} -> {freeEnd:F0} MB ({freeEnd - free0:+0;-0}), after gen1 {freeEnd - firstGenFree:+0;-0} MB; commit {commit0} -> {s_api->CommittedMb()} MB");
            return failures;
        }
        catch (Exception ex)
        {
            Log("Shim.Run failed: " + ex);
            return -100;
        }
    }

    // NoInlining so no local in Run keeps the ALC, the assembly or the payload alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunGeneration(int gen, string payloadPath, int mode, out string report)
    {
        var alc = new PayloadAlc($"gen{gen}", Path.GetDirectoryName(payloadPath)!, (mode & 0x100) != 0);
        Assembly asm;
        using (var fs = File.OpenRead(payloadPath)) asm = alc.LoadFromStream(fs);
        var payload = (IPayload)Activator.CreateInstance(asm.GetType("Payload.Entry", throwOnError: true)!)!;

        var parts = new List<string>();
        nint uco = payload.GetUcoPointer(out string? ucoError);
        parts.Add(uco != 0 ? $"uco 2+3={s_api->CallBinary(uco, 2, 3)}" : $"uco FAILED {ucoError}");
        nint del = payload.GetDelegatePointer();
        parts.Add($"delegate 6*7={s_api->CallBinary(del, 6, 7)}");
        parts.Add(payload.Work(mode));
        payload.Shutdown();

        report = string.Join("; ", parts);
        alc.Unload();
        return new WeakReference(alc, trackResurrection: true);
    }

    private sealed class PayloadAlc : AssemblyLoadContext
    {
        private static readonly string s_runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        private static readonly HashSet<string> s_private = new(StringComparer.OrdinalIgnoreCase)
        {
            "System.Text.Json", "System.Text.Encodings.Web", "System.IO.Pipelines",
        };
        private readonly string _dir;
        private readonly bool _privateFramework;

        public PayloadAlc(string name, string dir, bool privateFramework) : base(name, isCollectible: true)
        {
            _dir = dir;
            _privateFramework = privateFramework;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // Contracts and the framework come from the default ALC; anything else next to the payload loads here.
            if (name.Name == "Contracts") return null;
            if (_privateFramework && s_private.Contains(name.Name!))
                return LoadFromAssemblyPath(Path.Combine(s_runtimeDir, name.Name + ".dll"));
            string candidate = Path.Combine(_dir, name.Name + ".dll");
            if (!File.Exists(candidate)) return null;
            using var fs = File.OpenRead(candidate);
            return LoadFromStream(fs);
        }
    }
}
