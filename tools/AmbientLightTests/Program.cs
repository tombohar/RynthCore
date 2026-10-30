using RynthCore.Engine.D3D9;

namespace AmbientLightTests;

/// <summary>The ambient light floor's colour rule (AmbientLightPolicy, plugin API v91).</summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    // What AC sets in a dungeon: SetWorldAmbientLight(0.2, white) = 51 per channel, alpha 0xFF.
    private const uint Dungeon = 0xFF333333;

    private static int Main()
    {
        NoFloorPassesThrough();
        FloorLiftsADungeon();
        NeverDarker();
        PerChannel();
        AlphaBytes();
        FloorRgb();
        Exhaustive();

        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} assertions)" : $"FAIL ({_failed} of {_checks} assertions)");
        return _failed == 0 ? 0 : 1;
    }

    private static void Case(string name) { _case = name; Console.WriteLine("ok   " + name); }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"FAIL {_case}: {what}");
    }

    private static void Eq(uint got, uint want, string what) =>
        Check(got == want, $"{what}: got 0x{got:X8}, want 0x{want:X8}");

    private static void NoFloorPassesThrough()
    {
        Case("NoFloorPassesThrough");
        foreach (uint c in new uint[] { 0, Dungeon, 0xFFFFFFFF, 0x00123456, 0x80FEDCBA })
        {
            Eq(AmbientLightPolicy.Apply(c, 0), c, $"floor 0 keeps 0x{c:X8}");
            // A floor with only an alpha byte is no floor (D3D ignores the ambient's alpha).
            Eq(AmbientLightPolicy.Apply(c, 0xFF000000), c, $"alpha-only floor keeps 0x{c:X8}");
        }
    }

    private static void FloorLiftsADungeon()
    {
        Case("FloorLiftsADungeon");
        Eq(AmbientLightPolicy.Apply(Dungeon, 0x808080), 0xFF808080, "50 % grey over AC's 20 %");
        Eq(AmbientLightPolicy.Apply(Dungeon, 0xFFFFFF), 0xFFFFFFFF, "full white");
        // A floor at or below AC's own changes nothing.
        Eq(AmbientLightPolicy.Apply(Dungeon, 0x333333), Dungeon, "floor equal to AC's");
        Eq(AmbientLightPolicy.Apply(Dungeon, 0x202020), Dungeon, "floor below AC's");
    }

    private static void NeverDarker()
    {
        Case("NeverDarker");
        // Outdoors at noon AC's ambient is bright: a dungeon-sized floor leaves it alone.
        Eq(AmbientLightPolicy.Apply(0xFFC8C8B4, 0x808080), 0xFFC8C8B4, "noon stays noon");
        Eq(AmbientLightPolicy.Apply(0xFFFFFFFF, 0x010101), 0xFFFFFFFF, "white stays white");
    }

    private static void PerChannel()
    {
        Case("PerChannel");
        // A warm floor over grey: red and green lifted, blue AC's where AC's is brighter.
        Eq(AmbientLightPolicy.Apply(Dungeon, 0xA08C20), 0xFFA08C33, "warm torch tint");
        // A blue-ish AC colour (night sky) under a white floor: each channel the brighter.
        Eq(AmbientLightPolicy.Apply(0xFF102060, 0x404040), 0xFF404060, "night sky under grey");
    }

    private static void AlphaBytes()
    {
        Case("AlphaBytes");
        Eq(AmbientLightPolicy.Apply(0x00333333, 0xFF808080), 0x00808080, "AC's alpha 00 kept, floor's FF ignored");
        Eq(AmbientLightPolicy.Apply(0x7F333333, 0x00808080), 0x7F808080, "AC's alpha 7F kept, floor's 00 ignored");
    }

    private static void FloorRgb()
    {
        Case("FloorRgb");
        Eq(AmbientLightPolicy.FloorRgb(0xFF808080), 0x00808080, "alpha dropped");
        Eq(AmbientLightPolicy.FloorRgb(0xFF000000), 0, "alpha only = no floor");
        Eq(AmbientLightPolicy.FloorRgb(0x00010203), 0x00010203, "colour kept");
    }

    private static void Exhaustive()
    {
        Case("Exhaustive");
        // Every channel pair: the result is max(a, b), and the other channels are untouched.
        int bad = 0;
        for (uint a = 0; a < 256; a += 5)
            for (uint b = 0; b < 256; b += 3)
            {
                uint client = 0xFF000000 | (a << 16) | (7u << 8) | 9u;
                uint floor = (b << 16) | 1u;
                uint got = AmbientLightPolicy.Apply(client, floor);
                uint want = 0xFF000000 | (Math.Max(a, b) << 16) | (7u << 8) | 9u;
                if (got != want) bad++;
            }
        Check(bad == 0, $"{bad} red pairs wrong");
    }
}
