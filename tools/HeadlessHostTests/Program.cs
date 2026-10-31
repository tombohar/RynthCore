// ============================================================================
//  HeadlessHostTests - the plugin host's API against a replayed ACE session.
//  Each area has its own file (ObjectsTests.cs, PlayerTests.cs, ActionsTests.cs) that
//  registers its suite from a module initializer:
//    [System.Runtime.CompilerServices.ModuleInitializer]
//    internal static void Register() => Suites.Add("objects", Run, order: 10);
//  and uses Fixture.Shared (a session that replayed the capture, its HostContext and the
//  API table) plus T.Check / T.Eq / T.Info.
// ============================================================================

using System.Buffers.Binary;
using RynthCore.Engine.Net;
using RynthCore.Headless.Dat;
using RynthCore.Headless.Host;
using RynthCore.Headless.Net;
using RynthCore.Headless.Session;
using RynthCore.PluginSdk;

namespace HeadlessHostTests;

internal static class Program
{
    private static int Main()
    {
        // Every suite runs against the captured session. The public source snapshots leave the
        // captures out (they hold a live login), so without one there is nothing to check.
        if (!File.Exists(Fixture.CapturePath))
        {
            Console.WriteLine($"SKIPPED: no captured session at {Fixture.CapturePath} (the public source leaves the captures out).");
            return 0;
        }
        Fixture f = Fixture.Shared;
        Console.WriteLine($"fixture: player 0x{f.Session.World.Player.Id:X8}, {f.Session.World.Objects.Count} objects, state {f.Session.State}, spells table {(f.Context.Spells != null ? "yes" : "no")}");
        foreach (var (name, run) in Suites.All)
        {
            T.Case(name);
            try { run(); }
            catch (Exception ex) { T.Check(false, $"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        }
        Console.WriteLine();
        Console.WriteLine($"{T.Checks - T.Failed}/{T.Checks} checks passed.");
        Console.WriteLine(T.Failed == 0 ? "ALL HEADLESS HOST TESTS PASSED." : $"{T.Failed} FAILED.");
        return T.Failed == 0 ? 0 : 1;
    }
}

internal static class Suites
{
    private static readonly List<(int Order, string Name, Action Run)> _all = new();
    public static void Add(string name, Action run, int order) => _all.Add((order, name, run));
    public static IEnumerable<(string Name, Action Run)> All => _all.OrderBy(s => s.Order).Select(s => (s.Name, s.Run));
}

/// <summary>
/// A ClientSession that has replayed the captured P1 session (the test character: login, teleport out,
/// identify, use, cast, pickup, give, drop, melee kill, teleport back), its HostContext and the API table.
/// Call API functions inside <c>using var _ = f.Context.Enter();</c>.
/// </summary>
internal sealed unsafe class Fixture
{
    public static Fixture Shared { get; } = new();

    /// <summary>The account name the replay logs in with (only the outbound LoginRequest, which the capture leaves out, uses it).</summary>
    public const string Account = "testaccount";

    /// <summary>The captured session (tools\HeadlessNetTests\Captures, copied beside the test).</summary>
    public static string CapturePath => Path.Combine(AppContext.BaseDirectory, "Captures", "ace-p1-session.txt");

    public readonly FakeLink Link = new();
    public readonly ClientSession Session;
    public readonly HostContext Context;
    public readonly RynthCoreApiNative* Api;
    public long Now = 1000;

    private Fixture()
    {
        Session = new ClientSession(Link, new SessionOptions { Account = Account, PickFirstCharacter = true }, () => Now);
        // The API suites look at objects from every part of the capture (Holtburg's shop and corpse after
        // the teleport back): keep them. TeleportForgets checks the real behaviour on its own replay.
        Session.World.ForgetOnTeleport = false;
        Session.Start("x");
        foreach (string line in File.ReadLines(CapturePath))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            string[] f = line.Split(' ');
            if (f[1] != "S") continue;
            Link.Inbox.Enqueue(Convert.FromHexString(f[2]));
            Now += 5;
            Session.Tick();
        }
        string? ac = DatFile.FindAcFolder();
        Context = new HostContext(Session, Account, geometry: null, spells: ac != null ? SpellTable.Load(ac, out _) : null);
        Api = HostApi.Build();
    }

    /// <summary>A mark for <see cref="ActionsSince"/>.</summary>
    public int Mark => Link.Sent.Count;

    /// <summary>The game actions (type, payload) sent since a mark.</summary>
    public List<(uint Type, byte[] Payload)> ActionsSince(int mark)
    {
        var list = new List<(uint, byte[])>();
        foreach (byte[] m in MessagesSince(mark))
            if (m.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(m) == 0xF7B1)
                list.Add((BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(8)), m.AsSpan(12).ToArray()));
        return list;
    }

    /// <summary>Every whole message (opcode first) sent since a mark (single-fragment messages).</summary>
    public List<byte[]> MessagesSince(int mark)
    {
        var list = new List<byte[]>();
        foreach (var (d, _) in Link.Sent.Skip(mark))
        {
            if (!InboundPacket.TryParse(d, out InboundPacket p) || !p.Has(PacketFlags.BlobFragments)) continue;
            int at = p.OptionalEnd;
            int size = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + 10));
            list.Add(d.AsSpan(at + 16, size - 16).ToArray());
        }
        return list;
    }

    /// <summary>Feeds a server message (opcode first) straight into the world model, as if it had arrived.</summary>
    public void Receive(byte[] message)
    {
        if (RynthCore.PluginSdk.Net.ServerMessage.TryFromWire(message, out var msg)) Session.World.Apply(msg);
    }
}

internal sealed class FakeLink : IDatagramLink
{
    public readonly List<(byte[] Data, bool ConnectPort)> Sent = new();
    public readonly Queue<byte[]> Inbox = new();
    public void Send(ReadOnlySpan<byte> d, bool connectPort) => Sent.Add((d.ToArray(), connectPort));
    public bool TryReceive(out byte[] d) => Inbox.TryDequeue(out d!);
}

internal static class T
{
    public static int Checks, Failed;
    private static string _case = "";

    public static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"-- {name}");
    }

    public static void Check(bool ok, string what)
    {
        Checks++;
        if (ok) return;
        Failed++;
        Console.WriteLine($"   FAIL [{_case}] {what}");
    }

    public static void Eq<TV>(TV actual, TV expected, string what)
    {
        Checks++;
        if (EqualityComparer<TV>.Default.Equals(actual, expected)) return;
        Failed++;
        Console.WriteLine($"   FAIL [{_case}] {what}: got {actual}, expected {expected}");
    }

    public static void Info(string s) => Console.WriteLine($"   info: {s}");
}
