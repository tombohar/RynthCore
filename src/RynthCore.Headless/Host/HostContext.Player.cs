// ============================================================================
//  RynthCore.Headless - Host/HostContext.Player.cs
//  The PLAYER area's state: the character sheet (Host/Player/CharacterSheet.cs) over
//  the world model, the server clock, and when each enchantment started in server time.
//
//  Enchantment times: ACE sends StartTime as seconds relative to "now" (0 at the cast,
//  negative = already running that long) and the retail client stores it as server time
//  (the received value plus its synchronized clock), so the engine's expiry is
//  start + duration in server seconds, compared with GetServerTime. We do the same: each
//  enchantment is stamped with the server time it arrived at (PlayerDescription,
//  MagicUpdateEnchantment / ...Multiple); one we never saw arrive (the context was made
//  after it came) is stamped the first time it is looked at.
// ============================================================================

using RynthCore.Headless.Dat;
using RynthCore.Headless.Host.Player;
using RynthCore.Headless.Messages;

namespace RynthCore.Headless.Host;

public sealed partial class HostContext
{
    private readonly object _enchantGate = new();
    // (spell, layer) -> (the wire StartTime, server time when it arrived)
    private readonly Dictionary<(ushort Spell, ushort Layer), (double Wire, double Stamp)> _enchantStamps = new();

    /// <summary>The skill / vital formula tables (null when the dats are missing: formulas degrade to init + ranks).</summary>
    internal SkillTable? SkillFormulas { get; private set; }

    partial void InitPlayer()
    {
        SkillFormulas = SkillTable.Shared;
        StampAllEnchantments(clear: false);
        World.PlayerDescribed += () => StampAllEnchantments(clear: true);
        World.EnchantmentAdded += StampEnchantment;
    }

    /// <summary>The server's clock now (seconds): last time sync + local time since. 0 before the first.</summary>
    public double ServerTimeNow => Session.ServerTimeNow;

    private void StampAllEnchantments(bool clear)
    {
        double now = ServerTimeNow;
        lock (_enchantGate)
        {
            if (clear) _enchantStamps.Clear();
            foreach (Enchantment e in World.Player.Enchantments)
                _enchantStamps[(e.SpellId, e.Layer)] = (e.StartTime, now);
        }
    }

    private void StampEnchantment(Enchantment e)
    {
        double now = ServerTimeNow;
        lock (_enchantGate) _enchantStamps[(e.SpellId, e.Layer)] = (e.StartTime, now);
    }

    /// <summary>When the enchantment started, in server-time seconds.</summary>
    internal double EnchantmentStart(Enchantment e)
    {
        lock (_enchantGate)
        {
            if (!_enchantStamps.TryGetValue((e.SpellId, e.Layer), out var s) || s.Wire != e.StartTime)
            {
                s = (e.StartTime, ServerTimeNow);
                _enchantStamps[(e.SpellId, e.Layer)] = s;
            }
            return s.Stamp + e.StartTime;
        }
    }

    /// <summary>When it runs out, in server-time seconds; double.MaxValue for no duration (item spells: -1).</summary>
    internal double EnchantmentExpiry(Enchantment e)
        => e.Duration > 0 ? EnchantmentStart(e) + e.Duration : double.MaxValue;

    /// <summary>The player's character sheet as of now (cheap: computed on demand).</summary>
    internal CharacterSheet Sheet() => new(World, SkillFormulas, EnchantmentStart);

    /// <summary>True when the world model has the player's own description (stats, spells, enchantments).</summary>
    internal bool PlayerKnown => World.Player.Id != 0 && World.Player.DescriptionReceived;

    /// <summary>The player's buffed maximum of a vital (1 health, 2 stamina, 3 mana), 0 if unknown.</summary>
    public uint PlayerMaxVital(uint vital) => PlayerKnown ? Sheet().VitalBuffedMax(vital) : 0;
}
