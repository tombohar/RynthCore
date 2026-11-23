using System.Text.Json;
using RynthCore.Engine.Compatibility;
using static RynthCore.Engine.Compatibility.PaletteLeakSites;

namespace PaletteLeakFixTests;

/// <summary>
/// The palette leak fix's byte rules on fake bytes (no client): each site is patched only from the stock
/// inc, an already fixed (85 xx 24) or NOP'd (90 90 90) site counts as fixed and is left as it is, and any
/// other bytes leave the site alone. And the launcher setting: off unless appsettings.json says
/// "FixPaletteLeak": true.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        SiteTables();
        foreach (Site s in Sites) ByteChecks(s);
        SettingChecks();
        Check(Summary(2, 0, 0) == "Compat: palette leak fix on - 2 site(s) patched, 0 already fixed, 0 left alone (of 2).",
            "the one log line says it is on and how many sites were patched");
        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void SiteTables()
    {
        Check(Sites.Length == 2, "two sites");
        Check(Sites[0].Va == 0x0053EFFE && Sites[1].Va == 0x0053F19C, "at 0x53EFFE and 0x53F19C");
        Check(Stock(Sites[0]).AsSpan(Sites[0].Offset, 3).SequenceEqual(new byte[] { 0xFF, 0x40, 0x24 }), "site 1 is inc [eax+0x24] (FF 40 24)");
        Check(Stock(Sites[1]).AsSpan(Sites[1].Offset, 3).SequenceEqual(new byte[] { 0xFF, 0x46, 0x24 }), "site 2 is inc [esi+0x24] (FF 46 24)");
    }

    private static void ByteChecks(Site s)
    {
        string n = s.Name;
        int op = s.Offset;

        byte[] stock = Stock(s);
        Check(Classify(s, stock) == SiteState.Stock, $"{n}: the stock bytes -> stock (patched)");

        byte[] fixedB = Stock(s); fixedB[op] = FixedByte;
        Check(Classify(s, fixedB) == SiteState.AlreadyFixed, $"{n}: 85 xx 24 (this fix, or a patched client) -> already fixed");

        byte[] nop = Stock(s); nop[op] = nop[op + 1] = nop[op + 2] = Nop;
        Check(Classify(s, nop) == SiteState.AlreadyNopped, $"{n}: 90 90 90 (the original NOP patch) -> already fixed");

        byte[] other = Stock(s); other[op] = 0xCC;
        Check(Classify(s, other) == SiteState.Unknown, $"{n}: a breakpoint byte at the inc -> unknown, left alone");

        byte[] half = Stock(s); half[op] = Nop;
        Check(Classify(s, half) == SiteState.Unknown, $"{n}: one NOP and the rest of the inc -> unknown");

        byte[] reg = Stock(s); reg[op + 1] = 0x41;
        Check(Classify(s, reg) == SiteState.Unknown, $"{n}: another register in the inc -> unknown");

        byte[] around = Stock(s); around[op - 1] ^= 0x01;
        Check(Classify(s, around) == SiteState.Unknown, $"{n}: a byte before the inc differs -> unknown");

        byte[] after = Stock(s); after[^1] ^= 0x01;
        Check(Classify(s, after) == SiteState.Unknown, $"{n}: a byte after the inc differs -> unknown");

        byte[] rel = Stock(s); rel[1] = 0x12; rel[2] = 0x34; rel[3] = 0x56; rel[4] = 0x78;
        Check(Classify(s, rel) == SiteState.Stock, $"{n}: the call's target (rel32) may be anything");

        Check(Classify(s, stock.AsSpan(0, stock.Length - 1)) == SiteState.Unknown, $"{n}: too few bytes -> unknown");
        Check(Classify(s, new byte[stock.Length]) == SiteState.Unknown, $"{n}: zeros -> unknown");
    }

    private static void SettingChecks()
    {
        static bool Read(string json)
        {
            using JsonDocument d = JsonDocument.Parse(json);
            return LauncherSettings.ReadFixPaletteLeak(d.RootElement);
        }
        Check(!Read("{}"), "no key -> off");
        Check(!Read("""{ "AllowMultipleClients": true }"""), "other keys only -> off");
        Check(Read("""{ "FixPaletteLeak": true }"""), "\"FixPaletteLeak\": true -> on");
        Check(!Read("""{ "FixPaletteLeak": false }"""), "false -> off");
        Check(!Read("""{ "FixPaletteLeak": "true" }"""), "a string -> off");
        Check(!Read("[]"), "not an object -> off");
    }

    /// <summary>The site's context with any rel32 bytes filled in (the stock client's bytes, more or less).</summary>
    private static byte[] Stock(Site s) => s.Context.Select(b => b ?? (byte)0xAB).ToArray();

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) _failed++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    }
}
