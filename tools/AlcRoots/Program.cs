// AlcRoots: why didn't an engine generation unload?
//
// Attaches (read-only snapshot) to a CoreCLR-hosted acclient and prints
//   1. every managed thread with frames in collectible (engine) code, and
//   2. the shortest reference path from each kind of GC root to the first object that
//      belongs to a collectible context (an object of a collectible type, a
//      LoaderAllocator, or the Shim's EngineLoadContext).
// Anything on those lists keeps an unloaded generation alive.
//
//   AlcRoots.exe <pid> [maxPaths]
using Microsoft.Diagnostics.Runtime;

if (args.Length < 1 || !int.TryParse(args[0], out int pid))
{
    Console.WriteLine("usage: AlcRoots <pid> [maxPaths]");
    return 1;
}
int maxPaths = args.Length > 1 && int.TryParse(args[1], out int mp) ? mp : 25;
uint stackOfOsThread = args.Length > 2 && args[1] == "--stack" ? Convert.ToUInt32(args[2], 16) : 0;

using DataTarget dt = DataTarget.CreateSnapshotAndAttach(pid);
// A client that also runs Decal has the desktop CLR (clr.dll) loaded too: take CoreCLR.
foreach (ClrInfo c in dt.ClrVersions)
    Console.WriteLine($"runtime in process: {c.Flavor} {c.Version} ({c.ModuleInfo.FileName})");
ClrInfo info = dt.ClrVersions.Single(c => c.Flavor == ClrFlavor.Core);
string dac = Path.Combine(Path.GetDirectoryName(info.ModuleInfo.FileName)!, "mscordaccore.dll");
using ClrRuntime runtime = File.Exists(dac) ? info.CreateRuntime(dac, ignoreMismatch: true) : info.CreateRuntime();
ClrHeap heap = runtime.Heap;
Console.WriteLine($"CLR {info.Version} in pid {pid}; heap {heap.Segments.Sum(s => (long)s.Length) / 1048576} MB in {heap.Segments.Length} segments");

if (stackOfOsThread != 0 || (args.Length > 1 && args[1] == "--stack"))
{
    // AlcRoots <pid> --stack <osThreadIdHex>: managed stack of one thread.
    foreach (ClrThread th in runtime.Threads.Where(t => t.OSThreadId == stackOfOsThread))
        foreach (ClrStackFrame f in th.EnumerateStackTrace().Take(60))
            Console.WriteLine($"  {f.InstructionPointer:x8} {(f.Method != null ? f.Method.Signature : f.FrameName)}");
    return 0;
}

// --- collectible contexts still alive -------------------------------------------------
// The newest generation is the running one; every older context still alive is a leak
// (with only one context, it is the target: useful before a reload).
var allocators = heap.EnumerateObjects().Where(o => o.Type?.Name == "System.Reflection.LoaderAllocator").ToList();
var contexts = heap.EnumerateObjects().Where(o => o.Type?.Name == "RynthCore.Shim.EngineLoadContext")
    .Select(o => (Obj: o, Name: o.ReadStringField("_name") ?? "?"))
    .OrderBy(c => GenOf(c.Name))
    .ToList();
Console.WriteLine($"LoaderAllocators alive: {allocators.Count}; EngineLoadContexts alive: {contexts.Count}");
var targetContexts = new HashSet<ulong>(
    (contexts.Count > 1 ? contexts.Take(contexts.Count - 1) : contexts).Select(c => c.Obj.Address));
foreach (var (obj, name) in contexts)
    Console.WriteLine($"  context 0x{obj.Address:x8} \"{name}\"{(targetContexts.Contains(obj.Address) ? "   <- should be gone" : "   (running)")}");

static int GenOf(string name) =>
    int.TryParse(System.Text.RegularExpressions.Regex.Match(name, @"gen(\d+)").Groups[1].Value, out int g) ? g : 0;

bool InTarget(ClrType? t) => t != null && t.IsCollectible && targetContexts.Contains(t.AssemblyLoadContextAddress);

// --- threads running code of the leaked generation(s) -----------------------------------
Console.WriteLine();
Console.WriteLine("Threads with frames in the leaked generation(s):");
int engineThreads = 0;
foreach (ClrThread thread in runtime.Threads)
{
    if (!thread.IsAlive) continue;
    var frames = thread.EnumerateStackTrace().Take(200).ToList();
    if (!frames.Any(f => InTarget(f.Method?.Type))) continue;
    engineThreads++;
    Console.WriteLine($"  thread managed={thread.ManagedThreadId} os=0x{thread.OSThreadId:x}");
    foreach (ClrStackFrame f in frames.Where(f => f.Method != null).Take(12))
        Console.WriteLine($"      {(InTarget(f.Method!.Type) ? "*" : " ")} {f.Method.Signature ?? f.Method.Name}");
}
if (engineThreads == 0) Console.WriteLine("  (none)");

// --- reference paths from roots --------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Root paths into the leaked generation(s):");
var parent = new Dictionary<ulong, ulong>();
var rootOf = new Dictionary<ulong, string>();
var queue = new Queue<ClrObject>();
foreach (ClrRoot root in heap.EnumerateRoots())
{
    ClrObject o = root.Object;
    if (!o.IsValid || rootOf.ContainsKey(o.Address)) continue;
    string desc = root.RootKind.ToString();
    if (root is ClrStackRoot sr && sr.StackFrame != null)
        desc += $" thread os=0x{sr.StackFrame.Thread?.OSThreadId:x} in {sr.StackFrame.Method?.Signature ?? sr.StackFrame.FrameName}";
    rootOf[o.Address] = desc;
    queue.Enqueue(o);
}

var hits = new List<ulong>();
var visited = new HashSet<ulong>(rootOf.Keys);
while (queue.Count > 0)
{
    ClrObject o = queue.Dequeue();
    if (InTarget(o.Type)) { hits.Add(o.Address); continue; }   // stop at the boundary
    if (o.Type?.IsCollectible == true) continue;                // the running generation's own objects
    foreach (ClrObject r in o.EnumerateReferences(false, true))
    {
        if (!r.IsValid || !visited.Add(r.Address)) continue;
        parent[r.Address] = o.Address;
        queue.Enqueue(r);
    }
}

// Group by root description + path shape so one leak doesn't print a thousand times.
var seen = new HashSet<string>();
int printed = 0;
foreach (ulong hit in hits)
{
    var chain = new List<ulong> { hit };
    ulong cur = hit;
    while (parent.TryGetValue(cur, out ulong p)) { chain.Add(p); cur = p; }
    chain.Reverse();
    string rootDesc = rootOf.TryGetValue(chain[0], out string? d) ? d : "?";
    var types = chain.Select(a => heap.GetObject(a).Type?.Name ?? "?").ToList();
    string key = rootDesc.Split(" thread")[0] + "|" + string.Join(">", types);
    if (!seen.Add(key)) continue;
    Console.WriteLine($"  [{rootDesc}]");
    for (int i = 0; i < chain.Count; i++)
    {
        ClrObject o = heap.GetObject(chain[i]);
        string extra = o.Type?.IsString == true ? $" \"{Trim(o.AsString())}\"" : "";
        if (o.Type?.Name?.Contains("Delegate") == true || o.Type?.BaseType?.Name == "System.MulticastDelegate")
            extra = DescribeDelegate(runtime, o);
        Console.WriteLine($"      {(i == chain.Count - 1 ? "=>" : "->")} {o.Type?.Name}{extra} @0x{o.Address:x8}");
    }
    if (++printed >= maxPaths) { Console.WriteLine($"  ... ({hits.Count} hits, stopping at {maxPaths} distinct paths)"); break; }
}
if (printed == 0) Console.WriteLine("  (no strong path found: check the threads above and dependent handles)");
return 0;

static string Trim(string? s) => s == null ? "" : s.Length > 80 ? s[..80] + "..." : s;

static string DescribeDelegate(ClrRuntime runtime, ClrObject del)
{
    try
    {
        ulong methodPtr = del.ReadValueTypeField("_methodPtr").Address != 0 ? del.ReadField<ulong>("_methodPtr") : 0;
        ClrObject target = del.ReadObjectField("_target");
        ClrMethod? m = runtime.GetMethodByInstructionPointer(methodPtr);
        return $" (target {target.Type?.Name ?? "null"}, method {m?.Signature ?? "?"})";
    }
    catch { return ""; }
}
