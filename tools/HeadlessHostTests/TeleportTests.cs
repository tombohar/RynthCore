// ============================================================================
//  TeleportTests - a teleport drops the surroundings, as the retail client does (the captured session
//  goes from the dungeon to Holtburg and back: Holtburg's shop and corpse must not follow the character home).
// ============================================================================

using RynthCore.Headless.Session;
using RynthCore.Headless.World;

namespace HeadlessHostTests;

internal static class TeleportTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("teleport", Run, order: 60);

    private static void Run()
    {
        long now = 1000;
        var link = new FakeLink();
        var session = new ClientSession(link, new SessionOptions { Account = Fixture.Account, PickFirstCharacter = true }, () => now);
        session.Start("x");
        int teleports = 0, deleted = 0;
        session.World.PlayerTeleported += () => teleports++;
        session.World.ObjectDeleted += _ => deleted++;
        foreach (string line in File.ReadLines(Fixture.CapturePath))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            string[] f = line.Split(' ');
            if (f[1] != "S") continue;
            link.Inbox.Enqueue(Convert.FromHexString(f[2]));
            now += 5;
            session.Tick();
        }
        WorldState w = session.World;
        bool Has(string name) => w.Objects.Values.Any(o => o.Name == name);
        T.Check(teleports >= 2, $"the capture teleports out and back ({teleports})");
        T.Check(deleted > 0, $"plugins hear the dropped objects as deletes ({deleted})");
        T.Check(!Has("Shopkeeper Renald the Elder") && !Has("Corpse of Drudge Skulker"), "Holtburg's shopkeeper and corpse are gone after the teleport home");
        T.Check(Has("Viamontian Laced Boots") && Has("Sack"), "what the character carries stays");
        T.Check(w.Objects.ContainsKey(w.Player.Id), "the player stays");
        T.Check(w.Objects.Count < Fixture.Shared.Session.World.Objects.Count,
            $"fewer objects than the replay that keeps them ({w.Objects.Count} < {Fixture.Shared.Session.World.Objects.Count})");
    }
}
