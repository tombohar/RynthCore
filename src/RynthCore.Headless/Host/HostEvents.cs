// ============================================================================
//  RynthCore.Headless - Host/HostEvents.cs
//  The plugin callbacks waiting to be delivered, collected from the world model and
//  the API, drained once per host tick in the engine's order:
//    busy +/-, combat mode, view contents / stop viewing, vendor open/close,
//    inventory lists, creates, updates (one per object per drain), deletes, selection,
//    health, combat damage, kills, enchantments added/removed, chat lines.
//  Each queue is bounded; the oldest entry goes when one is full.
// ============================================================================

namespace RynthCore.Headless.Host;

public abstract record PluginEvent
{
    public sealed record BusyIncremented : PluginEvent;
    public sealed record BusyDecremented : PluginEvent;
    public sealed record CombatModeChange(int Current, int Previous) : PluginEvent;
    public sealed record ViewContents(uint Container) : PluginEvent;
    public sealed record StopViewingContents(uint Container) : PluginEvent;
    public sealed record VendorOpen(uint Vendor) : PluginEvent;
    public sealed record VendorClose(uint Vendor) : PluginEvent;
    public sealed record UpdateObjectInventory(uint Container) : PluginEvent;
    public sealed record CreateObject(uint Id) : PluginEvent;
    public sealed record UpdateObject(uint Id) : PluginEvent;
    public sealed record DeleteObject(uint Id) : PluginEvent;
    public sealed record SelectedTargetChange(uint Current, uint Previous) : PluginEvent;
    public sealed record UpdateHealth(uint Target, float Ratio, uint CurrentHealth, uint MaxHealth) : PluginEvent;
    public sealed record CombatDamage(uint Damage, uint DamageType, bool Critical, bool IsAttacker) : PluginEvent;
    public sealed record KillNotification(string Text) : PluginEvent;
    public sealed record EnchantmentAdded(uint SpellId, double Duration) : PluginEvent;
    public sealed record EnchantmentRemoved(uint SpellId) : PluginEvent;
    public sealed record ChatWindowText(string Text, int ChatType) : PluginEvent;
}

public sealed class HostEvents
{
    public const int Cap = 4096;

    private readonly HostContext _ctx;
    private readonly object _gate = new();
    private readonly List<PluginEvent> _pending = new();

    public HostEvents(HostContext ctx) => _ctx = ctx;

    public long Dropped { get; private set; }

    public void Enqueue(PluginEvent e)
    {
        lock (_gate)
        {
            if (_pending.Count >= Cap)
            {
                _pending.RemoveAt(0);
                Dropped++;
            }
            _pending.Add(e);
        }
    }

    private static int Rank(PluginEvent e) => e switch
    {
        PluginEvent.BusyIncremented => 0,
        PluginEvent.BusyDecremented => 1,
        PluginEvent.CombatModeChange => 2,
        PluginEvent.ViewContents => 3,
        PluginEvent.StopViewingContents => 4,
        PluginEvent.VendorOpen => 5,
        PluginEvent.VendorClose => 6,
        PluginEvent.UpdateObjectInventory => 7,
        PluginEvent.CreateObject => 8,
        PluginEvent.UpdateObject => 9,
        PluginEvent.DeleteObject => 10,
        PluginEvent.SelectedTargetChange => 11,
        PluginEvent.UpdateHealth => 12,
        PluginEvent.CombatDamage => 13,
        PluginEvent.KillNotification => 14,
        PluginEvent.EnchantmentAdded => 15,
        PluginEvent.EnchantmentRemoved => 16,
        PluginEvent.ChatWindowText => 17,
        _ => 18,
    };

    /// <summary>Everything pending, in delivery order (stable within a kind); updates collapse to one per object.</summary>
    public List<PluginEvent> Drain()
    {
        List<PluginEvent> batch;
        lock (_gate)
        {
            batch = new List<PluginEvent>(_pending);
            _pending.Clear();
        }
        var seenUpdates = new HashSet<uint>();
        var deleted = new HashSet<uint>(batch.OfType<PluginEvent.DeleteObject>().Select(d => d.Id));
        var result = new List<PluginEvent>(batch.Count);
        foreach (PluginEvent e in batch.Select((e, i) => (e, i)).OrderBy(x => Rank(x.e)).ThenBy(x => x.i).Select(x => x.e))
        {
            if (e is PluginEvent.UpdateObject u && (!seenUpdates.Add(u.Id) || deleted.Contains(u.Id))) continue;
            result.Add(e);
        }
        return result;
    }
}
