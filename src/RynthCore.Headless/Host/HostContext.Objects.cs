// ============================================================================
//  RynthCore.Headless - Host/HostContext.Objects.cs
//  The objects area's per-character state, kept from the world model's events:
//    - identify replies: when (Unix seconds), success or not, the spell book, the
//      creature profile's vitals (AppraisalHooks / ObjectQualityCache in the engine);
//    - doors and levers: the last On / Off forward command of an UpdateMotion
//      (DoMotionHooks);
//    - when the server last said anything about an object (ObjectFreshness), for
//      GetObjectServerAge and RemoveClientObject's 5 s rule.
//  Everything for an object is dropped when it is deleted.
// ============================================================================

using RynthCore.Headless.Host.Objects;
using RynthCore.Headless.Messages;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Host;

public sealed partial class HostContext
{
    /// <summary>A creature's vitals from its last identify's creature profile (health follows UpdateHealth).</summary>
    public readonly record struct TargetVitals(uint Health, uint MaxHealth, uint Stamina, uint MaxStamina, uint Mana, uint MaxMana);

    private readonly object _objGate = new();
    private readonly Dictionary<uint, long> _objIdTime = new();          // Unix seconds of the last identify reply
    private readonly HashSet<uint> _objAppraised = new();
    private readonly Dictionary<uint, uint[]> _objSpells = new();         // spell book of the last identify
    private readonly Dictionary<uint, TargetVitals> _objVitals = new();
    private readonly Dictionary<uint, bool> _objMotionOn = new();
    private readonly Dictionary<uint, long> _objHeardMs = new();          // NowMs of the server's last message about it

    /// <summary>Doors' On (0x4000000B) / Off (0x4000000C) forward commands, by their low 16 bits as UpdateMotion carries them.</summary>
    private const uint MotionOnLow = 0x000B, MotionOffLow = 0x000C;
    public const long RemoveMinQuietMs = 5000;

    partial void InitObjects()
    {
        WorldState w = World;
        // Identifies the world model took before this context existed (a replay, a late host).
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (WorldObject o in w.Objects.Values)
        {
            if (!o.Identified) continue;
            _objAppraised.Add(o.Id);
            _objIdTime[o.Id] = now;
        }

        w.ObjectCreated += o => Heard(o.Id);
        w.ObjectUpdated += o => Heard(o.Id);
        w.ObjectStateChanged += o => Heard(o.Id);
        w.ObjectMoved += Heard;
        w.ObjectDeleted += ForgetObjectState;
        w.ObjectMotion += OnObjectMotion;
        w.IdentifyDetailed += OnIdentifyDetails;
        w.HealthUpdated += OnHealthRatio;
    }

    private void Heard(uint id)
    {
        lock (_objGate) _objHeardMs[id] = NowMs;
    }

    private void OnObjectMotion(uint id, InterpretedMotion m)
    {
        if (id != World.Player.Id) Heard(id);
        uint low = m.ForwardCommand & 0xFFFF;
        if (low is not (MotionOnLow or MotionOffLow)) return;
        lock (_objGate) _objMotionOn[id] = low == MotionOnLow;
    }

    private void OnIdentifyDetails(IdentifyDetails d)
    {
        TargetVitals? v = null;
        lock (_objGate)
        {
            _objAppraised.Add(d.ObjectId);
            _objIdTime[d.ObjectId] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (d.Spells.Length > 0) _objSpells[d.ObjectId] = d.Spells;
            else if (d.HasSpellBook) _objSpells.Remove(d.ObjectId);
            // As the engine: max health even on a failed assess roll; nothing implausible.
            if (d.HasCreatureProfile && d.HealthMax > 0 && d.HealthMax < 1_000_000)
            {
                v = new TargetVitals(Math.Min(d.Health, d.HealthMax), d.HealthMax, d.Stamina, d.StaminaMax, d.Mana, d.ManaMax);
                _objVitals[d.ObjectId] = v.Value;
            }
        }
        // The engine reports an identify's creature profile as a health update too.
        if (v is { } tv)
            Events.Enqueue(new PluginEvent.UpdateHealth(d.ObjectId, (float)tv.Health / tv.MaxHealth, tv.Health, tv.MaxHealth));
    }

    private void OnHealthRatio(uint id, float ratio)
    {
        Heard(id);
        lock (_objGate)
        {
            if (_objVitals.TryGetValue(id, out TargetVitals v))
                _objVitals[id] = v with { Health = (uint)MathF.Round(v.MaxHealth * Math.Clamp(ratio, 0f, 1f)) };
        }
    }

    private void ForgetObjectState(uint id)
    {
        lock (_objGate)
        {
            _objIdTime.Remove(id);
            _objAppraised.Remove(id);
            _objSpells.Remove(id);
            _objVitals.Remove(id);
            _objMotionOn.Remove(id);
            _objHeardMs.Remove(id);
        }
    }

    partial void MaxHealthHook(uint id, ref uint max)
    {
        lock (_objGate)
            if (_objVitals.TryGetValue(id, out TargetVitals v) && v.MaxHealth > 0) max = v.MaxHealth;
        // The player's own health bar: the buffed maximum from the character sheet.
        if (max == 0 && id != 0 && id == World.Player.Id) max = PlayerMaxVital(1);
    }

    // ── queries the API reads ──────────────────────────────────────────────

    public bool HasAppraisalData(uint id)
    {
        lock (_objGate)
            if (_objAppraised.Contains(id)) return true;
        return World.Get(id)?.Identified == true;
    }

    /// <summary>Unix seconds of the last identify reply; 0 never.</summary>
    public long LastIdTime(uint id)
    {
        lock (_objGate) return _objIdTime.TryGetValue(id, out long t) ? t : 0;
    }

    /// <summary>The last identify's spell book (the world model's copy for identifies before this context); null when none.</summary>
    public uint[]? IdentifySpells(uint id)
    {
        lock (_objGate)
            if (_objSpells.TryGetValue(id, out uint[]? s)) return s;
        WorldObject? o = World.Get(id);
        return o != null && o.Identified && o.SpellBook.Length > 0 ? o.SpellBook : null;
    }

    public bool TryGetTargetVitals(uint id, out TargetVitals v)
    {
        lock (_objGate) return _objVitals.TryGetValue(id, out v);
    }

    public bool TryGetMotionOn(uint id, out bool on)
    {
        lock (_objGate) return _objMotionOn.TryGetValue(id, out on);
    }

    /// <summary>Milliseconds since the server's last message about the object; -1 none seen (or deleted).</summary>
    public long ServerAgeMs(uint id)
    {
        lock (_objGate)
            return _objHeardMs.TryGetValue(id, out long t) ? Math.Max(0, NowMs - t) : -1;
    }

    /// <summary>Why <see cref="RemoveClientObject"/> would refuse, or null (the engine's DeleteObjectHooks.RemoveRefusal).</summary>
    public string? RemoveRefusal(uint id)
    {
        if (id == 0) return "no object";
        uint me = World.Player.Id;
        if (me == 0 || id == me) return "the player";
        WorldObject? o = World.Get(id);
        if (o == null || !ObjectProps.Created(o)) return "not in the client's object table";
        if ((o.ItemType & 0x10) == 0) return "not a creature";
        if ((o.Desc.DescriptionFlags & ObjectProps.BfPlayer) != 0) return "a player";
        long age = ServerAgeMs(id);
        if (age >= 0 && age < RemoveMinQuietMs) return $"the server sent a message about it {age} ms ago";
        return null;
    }

    /// <summary>
    /// v83 RemoveClientObject: drops a ghost creature from the world model and tells the plugins
    /// (OnDeleteObject). 1 removed, 0 refused.
    /// </summary>
    public int RemoveClientObject(uint id)
    {
        string? why = RemoveRefusal(id);
        if (why != null)
        {
            Log($"RemoveClientObject 0x{id:X8}: refused - {why}.");
            return 0;
        }
        if (!World.ForgetObject(id)) return 0;
        ForgetObjectState(id);
        Events.Enqueue(new PluginEvent.DeleteObject(id));
        return 1;
    }
}
