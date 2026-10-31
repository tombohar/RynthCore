// ============================================================================
//  RynthCore.Headless - Host/HostBridge.cs
//  Turns one session's world model into plugin events, and drives the plugin host:
//    - creates / updates (UpdateObject, state, motion, vectors, positions) / deletes;
//    - containers: ViewContents of something not ours = OnViewObjectContents (and the
//      ground container), every ViewContents = OnUpdateObjectInventory, a close =
//      OnStopViewingObjectContents;
//    - UpdateHealth, combat notices (attacker/defender damage, kills), enchantments;
//    - chat as retail's chat window shows it ("X says, \"...\"", "X tells you, ...");
//    - combat mode changes; teleports (IsPortaling for a moment);
//    - login (once the player, its description and LoginComplete are there) and logout.
//  Call Tick() from the session's loop after session.Tick().
// ============================================================================

using RynthCore.Headless.Messages;
using RynthCore.Headless.Session;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Host;

public sealed class HostBridge
{
    private readonly HostContext _ctx;
    private readonly PluginHost _host;
    private int _lastCombatMode = 1;
    private long _portalUntil;
    private bool _loggedIn;

    public HostBridge(HostContext ctx, PluginHost host)
    {
        _ctx = ctx;
        _host = host;
        WorldState w = ctx.World;
        HostEvents ev = ctx.Events;

        w.ObjectCreated += o => ev.Enqueue(new PluginEvent.CreateObject(o.Id));
        w.ObjectUpdated += o => ev.Enqueue(new PluginEvent.UpdateObject(o.Id));
        w.ObjectStateChanged += o => ev.Enqueue(new PluginEvent.UpdateObject(o.Id));
        w.ObjectMoved += id => ev.Enqueue(new PluginEvent.UpdateObject(id));
        w.ObjectDeleted += id => ev.Enqueue(new PluginEvent.DeleteObject(id));

        w.ContainerOpened += id =>
        {
            if (!IsOurs(id))
            {
                ctx.GroundContainer = id;
                ev.Enqueue(new PluginEvent.ViewContents(id));
            }
            ev.Enqueue(new PluginEvent.UpdateObjectInventory(id));
        };
        w.ContainerClosed += id =>
        {
            if (ctx.GroundContainer == id) ctx.GroundContainer = 0;
            ev.Enqueue(new PluginEvent.StopViewingContents(id));
        };

        w.HealthUpdated += (id, ratio) =>
        {
            (uint cur, uint max) = ctx.HealthOf(id, ratio);
            ev.Enqueue(new PluginEvent.UpdateHealth(id, ratio, cur, max));
        };
        w.Combat += c =>
        {
            switch (c.EventType)
            {
                case WorldEvent.AttackerNotification when c.Damage > 0:
                    ev.Enqueue(new PluginEvent.CombatDamage(c.Damage, c.DamageType, c.Critical, true));
                    break;
                case WorldEvent.DefenderNotification when c.Damage > 0:
                    ev.Enqueue(new PluginEvent.CombatDamage(c.Damage, c.DamageType, c.Critical, false));
                    break;
                case WorldEvent.KillerNotification when c.Name.Length > 0:
                    ev.Enqueue(new PluginEvent.KillNotification(c.Name));
                    break;
            }
        };
        w.EnchantmentAdded += e => ev.Enqueue(new PluginEvent.EnchantmentAdded(e.SpellId, e.Duration));
        w.EnchantmentRemoved += id => ev.Enqueue(new PluginEvent.EnchantmentRemoved(id));
        w.TransientMessage += t => ev.Enqueue(new PluginEvent.ChatWindowText(t, 0x05));
        // Refusals that carry only a code: the line retail's chat window shows for them.
        w.InventoryFailed += (_, code) => { if (RetailErrorText.For(code) is { } t) ev.Enqueue(new PluginEvent.ChatWindowText(t, 0x05)); };
        w.Error += (code, text) => { if (text.Length == 0 && RetailErrorText.For(code) is { } t) ev.Enqueue(new PluginEvent.ChatWindowText(t, 0x05)); };
        w.PlayerTeleported += () => _portalUntil = ctx.NowMs + 1500;

        ctx.Session.Chat += line => ev.Enqueue(new PluginEvent.ChatWindowText(ChatText(line), (int)line.ChatType));
    }

    private bool IsOurs(uint container)
        => container == _ctx.World.Player.Id || _ctx.World.Get(container)?.ContainerId == _ctx.World.Player.Id;

    /// <summary>The line as retail's chat window would print it.</summary>
    public static string ChatText(ChatLine l) => l.ChatType switch
    {
        0x02 when l.Sender.Length > 0 => $"{l.Sender} says, \"{l.Text}\"",
        0x03 when l.Sender.Length > 0 => $"{l.Sender} tells you, \"{l.Text}\"",
        0x0C when l.Sender.Length > 0 => $"{l.Sender} {l.Text}",
        _ => l.Text,
    };

    /// <summary>Call every host frame, after session.Tick().</summary>
    public void Tick()
    {
        bool inWorld = _ctx.Session.State == SessionState.InWorld && _ctx.World.Player.DescriptionReceived && _ctx.World.PlayerObject != null;
        if (inWorld && !_loggedIn)
        {
            _loggedIn = true;
            _lastCombatMode = _ctx.CurrentCombatMode;
            _host.LoginComplete();
        }
        else if (!inWorld && _loggedIn)
        {
            _loggedIn = false;
            _host.Logout();
        }

        _ctx.Portaling = _ctx.NowMs < _portalUntil;
        int mode = _ctx.CurrentCombatMode;
        if (mode != _lastCombatMode)
        {
            _ctx.Events.Enqueue(new PluginEvent.CombatModeChange(mode, _lastCombatMode));
            _lastCombatMode = mode;
        }
        _ctx.TickAreas();
        _host.Tick();
    }
}

public sealed partial class HostContext
{
    /// <summary>The external container (corpse, chest) being viewed; 0 if none.</summary>
    public uint GroundContainer { get; internal set; }

    /// <summary>Current / max health for an UpdateHealth ratio, when the max is known (0, 0 otherwise).</summary>
    public (uint Current, uint Max) HealthOf(uint id, float ratio)
    {
        uint max = MaxHealthOf(id);
        return max == 0 ? (0u, 0u) : ((uint)MathF.Round(max * ratio), max);
    }

    // Areas override these (partial methods: no-op until an area provides them).
    partial void MaxHealthHook(uint id, ref uint max);
    public uint MaxHealthOf(uint id)
    {
        uint max = 0;
        MaxHealthHook(id, ref max);
        return max;
    }

    partial void TickObjects();
    partial void TickPlayer();
    partial void TickActions();
    internal void TickAreas()
    {
        TickObjects();
        TickPlayer();
        TickActions();
    }
}
