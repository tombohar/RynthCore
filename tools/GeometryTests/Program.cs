// ============================================================================
//  GeometryTests - RynthCore.Geometry against the real dats and captured ACE ground truth.
//  Each area has its own file (TerrainTests.cs, CellTests.cs, ...) with a Run() that uses T.
// ============================================================================

namespace GeometryTests;

internal static class Program
{
    private static int Main()
    {
        string? ac = Dats.Folder;
        if (ac == null)
        {
            Console.WriteLine("SKIP: no AC dats (client_portal.dat / client_cell_1.dat) found.");
            return 0;
        }
        Console.WriteLine($"dats: {ac}");

        foreach (var (name, run) in Suites.All)
        {
            T.Case(name);
            try { run(); }
            catch (Exception ex) { T.Check(false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        }

        Console.WriteLine();
        Console.WriteLine($"{T.Checks - T.Failed}/{T.Checks} checks passed.");
        Console.WriteLine(T.Failed == 0 ? "ALL GEOMETRY TESTS PASSED." : $"{T.Failed} FAILED.");
        return T.Failed == 0 ? 0 : 1;
    }
}

/// <summary>
/// The suites. Each area's file registers itself from a module initializer:
///   [System.Runtime.CompilerServices.ModuleInitializer]
///   internal static void Register() => Suites.Add("terrain", Run, order: 10);
/// </summary>
internal static class Suites
{
    private static readonly List<(int Order, string Name, Action Run)> _all = new();
    public static void Add(string name, Action run, int order) => _all.Add((order, name, run));
    public static IEnumerable<(string Name, Action Run)> All => _all.OrderBy(s => s.Order).Select(s => (s.Name, s.Run));
}

internal static class Dats
{
    public static string? Folder { get; } = Find();

    private static string? Find()
    {
        foreach (string dir in new[] { Environment.GetEnvironmentVariable("AC_DAT_DIR") ?? "", @"C:\Turbine\Asheron's Call" })
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "client_portal.dat")) && File.Exists(Path.Combine(dir, "client_cell_1.dat")))
                return dir;
        return null;
    }

    public static string Portal => Path.Combine(Folder!, "client_portal.dat");
    public static string Cell => Path.Combine(Folder!, "client_cell_1.dat");

    /// <summary>Ground-truth positions: "cell x y z" lines.</summary>
    public static List<(uint Cell, float X, float Y, float Z)> Vectors(string file)
    {
        var list = new List<(uint, float, float, float)>();
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Vectors", file)))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list.Add((Convert.ToUInt32(f[0], 16), float.Parse(f[1]), float.Parse(f[2]), float.Parse(f[3])));
        }
        return list;
    }
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

    public static void Info(string s) => Console.WriteLine($"   info: {s}");
}
