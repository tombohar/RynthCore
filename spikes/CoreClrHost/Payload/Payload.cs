using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Contracts;

namespace Payload;

// Mode bits (the Shim passes the host's mode through):
//   0x01  System.Text.Json reflection serialize/deserialize of payload types
//   0x02  start a Thread and join it in Shutdown
//   0x04  Task.Run work + a System.Threading.Timer, both finished/disposed in Shutdown
//   0x08  hold a 32 MB static array (the GC heap should give it back)
//   0x10  LEAK ON PURPOSE: subscribe to AppDomain.ProcessExit and never unsubscribe
//   0x20  JIT a lot of code (many generic instantiations over payload types)
//   0x40  allocate 8 MB with NativeMemory and free it in Shutdown
//   0x80  System.Text.Json reflection with a JsonSerializerOptions the payload owns
//   0x100 (Shim side) load a private copy of System.Text.Json into the collectible ALC
public sealed class Entry : IPayload
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int BinaryFn(int a, int b);

    private static byte[]? s_big;
    private static BinaryFn? s_mul;
    private Thread? _thread;
    private volatile bool _stop;
    private Timer? _timer;
    private Task? _task;
    private unsafe void* _native;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Add(int a, int b) => a + b;

    public unsafe nint GetUcoPointer(out string? error)
    {
        try
        {
            error = null;
            return (nint)(delegate* unmanaged[Stdcall]<int, int, int>)&Add;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return 0;
        }
    }

    public nint GetDelegatePointer()
    {
        s_mul = (a, b) => a * b;
        return Marshal.GetFunctionPointerForDelegate(s_mul);
    }

    public unsafe string Work(int mode)
    {
        var did = new List<string>();
        if ((mode & 0x01) != 0)
        {
            var rec = new Record { Name = "gen", Values = Enumerable.Range(0, 100).ToList(), Child = new Record { Name = "c" } };
            string json = JsonSerializer.Serialize(rec);
            var back = JsonSerializer.Deserialize<Record>(json)!;
            did.Add($"json {json.Length}b {back.Values.Count} stj alc {System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(JsonSerializer).Assembly)?.Name}");
        }
        if ((mode & 0x80) != 0)
        {
            var opts = new JsonSerializerOptions { WriteIndented = false };
            var rec = new Record { Name = "own", Values = Enumerable.Range(0, 10).ToList() };
            string json = JsonSerializer.Serialize(rec, opts);
            var back = JsonSerializer.Deserialize<Record>(json, opts)!;
            did.Add($"json(own opts) {json.Length}b {back.Values.Count} stj@{typeof(JsonSerializer).Assembly.GetName().Name}:{System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(JsonSerializer).Assembly)?.Name}");
        }
        if ((mode & 0x02) != 0)
        {
            _thread = new Thread(() => { while (!_stop) Thread.Sleep(5); }) { IsBackground = true, Name = "payload" };
            _thread.Start();
            did.Add("thread");
        }
        if ((mode & 0x04) != 0)
        {
            _task = Task.Run(() => Thread.Sleep(20));
            _timer = new Timer(_ => { }, null, 0, 10);
            did.Add("task+timer");
        }
        if ((mode & 0x08) != 0)
        {
            s_big = new byte[32 * 1024 * 1024];
            s_big[^1] = 1;
            did.Add("32MB static");
        }
        if ((mode & 0x10) != 0)
        {
            AppDomain.CurrentDomain.ProcessExit += OnExit;
            did.Add("LEAK ProcessExit");
        }
        if ((mode & 0x20) != 0)
        {
            int n = JitMany();
            did.Add($"jit {n}");
        }
        if ((mode & 0x40) != 0)
        {
            _native = NativeMemory.Alloc(8 * 1024 * 1024);
            did.Add("native 8MB");
        }
        return did.Count == 0 ? "basic" : string.Join(", ", did);
    }

    private void OnExit(object? s, EventArgs e) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int JitMany()
    {
        int sum = 0;
        sum += Gen<A1>.Run(); sum += Gen<A2>.Run(); sum += Gen<A3>.Run(); sum += Gen<A4>.Run();
        sum += Gen<A5>.Run(); sum += Gen<A6>.Run(); sum += Gen<A7>.Run(); sum += Gen<A8>.Run();
        var d = new Dictionary<A1, List<A2>>(); d[new A1()] = new List<A2> { new() };
        sum += d.Count + d.Values.SelectMany(x => x).Count();
        return sum;
    }

    public unsafe void Shutdown()
    {
        _stop = true;
        _thread?.Join();
        _timer?.Dispose();
        _task?.Wait();
        if (_native != null) { NativeMemory.Free(_native); _native = null; }
        s_big = null;
        // s_mul stays: a static in the collectible ALC, it dies with the ALC.
    }

    public sealed class Record
    {
        public string Name { get; set; } = "";
        public List<int> Values { get; set; } = new();
        public Record? Child { get; set; }
    }

    private sealed class A1 { } private sealed class A2 { } private sealed class A3 { } private sealed class A4 { }
    private sealed class A5 { } private sealed class A6 { } private sealed class A7 { } private sealed class A8 { }

    private static class Gen<T> where T : new()
    {
        public static int Run()
        {
            var list = new List<T>();
            for (int i = 0; i < 16; i++) list.Add(new T());
            var set = new HashSet<T>(list);
            var dict = list.Select((x, i) => (x, i)).ToDictionary(p => p.x, p => p.i);
            return list.Where(x => x != null).OrderBy(x => x!.GetHashCode()).Count() + set.Count + dict.Count;
        }
    }
}
