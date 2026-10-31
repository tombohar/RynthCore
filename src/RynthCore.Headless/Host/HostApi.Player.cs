// ============================================================================
//  RynthCore.Headless - Host/HostApi.Player.cs
//  The PLAYER slots: vitals, attributes, skills, spellbook, enchantments, the server
//  clock, and the player's pose / heading / map coordinates.
//
//  Values follow the engine's off-thread answers (what plugins see there):
//    GetPlayerVitals        current + BUFFED max (enchantments and vitae), as AC's bars
//    GetPlayerBaseVitals    unbuffed max (formula of base attributes + init + ranks)
//    GetObjectAttribute     raw 1 = base (init + ranks), raw 0 = buffed
//    GetObjectAttribute2ndBaseLevel  1/3/5 = unbuffed max health/stamina/mana,
//                           2/4/6 = the current value
//    GetObjectSkill         BUFFED level + training (0 inactive, 1 untrained, 2 trained,
//                           3 specialized); GetObjectSkillBuffed raw 1 = base, 0 = buffed
//    ReadPlayerEnchantments spell id + expiry in server seconds (double.MaxValue for no
//                           duration); vitae left out; count written, -1 when unknown
//    GetServerTime          last time sync + local time since (0 before the first)
//    GetPlayerHeading       0 = east, counter-clockwise (the engine's NormalizeDecalHeading
//                           of AC's compass heading); the pose's quaternion is compass
//    GetCurCoords           EW = (lbX * 8 + x / 24 - 1019.5) / 10, NS likewise with lbY / y
//  Only the player is known this well: other objects answer "unavailable" (0 / -1).
//  Character titles aren't tracked: -1 (the SDK reads that as "not received").
// ============================================================================

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Headless.Host.Player;
using RynthCore.Headless.Messages;
using RynthCore.Headless.World;
using RynthCore.PluginSdk;

namespace RynthCore.Headless.Host;

internal static unsafe partial class HostApi
{
    static partial void FillPlayer(RynthCoreApiNative* api)
    {
        api->GetPlayerVitalsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, uint*, uint*, uint*, uint*, int>)&GetPlayerVitals;
        api->GetPlayerBaseVitalsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, uint*, int>)&GetPlayerBaseVitals;
        api->GetObjectAttribute2ndBaseLevelFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)&GetObjectAttribute2ndBaseLevel;
        api->GetObjectSkillFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)&GetObjectSkill;
        api->GetObjectSkillBuffedFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int*, int>)&GetObjectSkillLevel;
        api->GetObjectAttributeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, uint*, int>)&GetObjectAttribute;
        api->GetVitaeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, float>)&GetVitae;
        api->IsSpellKnownFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&IsSpellKnown;
        api->ReadKnownSpellsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, int, int>)&ReadKnownSpells;
        api->ReadPlayerEnchantmentsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, double*, int, int>)&ReadPlayerEnchantments;
        api->ReadObjectEnchantmentsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, double*, int, int>)&ReadObjectEnchantments;
        api->GetServerTimeFn = (IntPtr)(delegate* unmanaged[Cdecl]<double>)&GetServerTime;
        api->GetPlayerPoseFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, float*, float*, float*, float*, float*, float*, float*, int>)&GetPlayerPose;
        api->GetPlayerHeadingFn = (IntPtr)(delegate* unmanaged[Cdecl]<float*, int>)&GetPlayerHeading;
        api->GetCurCoordsFn = (IntPtr)(delegate* unmanaged[Cdecl]<double*, double*, int>)&GetCurCoords;
        api->GetCharacterTitlesFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, int, uint*, int>)&GetCharacterTitles;
    }

    /// <summary>The context when the player's description is known (and, with an id, that id is the player).</summary>
    private static HostContext? OwnPlayerCtx(uint objectId = 0)
    {
        HostContext? c = Ctx;
        if (c == null || !c.PlayerKnown) return null;
        if (objectId != 0 && objectId != c.World.Player.Id) return null;
        return c;
    }

    // ── vitals ──────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerVitals(uint* health, uint* maxHealth, uint* stamina, uint* maxStamina, uint* mana, uint* maxMana)
    {
        try
        {
            if (health == null || maxHealth == null || stamina == null || maxStamina == null || mana == null || maxMana == null) return 0;
            HostContext? c = OwnPlayerCtx();
            if (c == null) return 0;
            CharacterSheet s = c.Sheet();
            if (!s.HasVital(1)) return 0;
            uint h = s.VitalCurrent(1), mh = s.VitalBuffedMax(1);
            uint st = s.VitalCurrent(2), mst = s.VitalBuffedMax(2);
            uint m = s.VitalCurrent(3), mm = s.VitalBuffedMax(3);
            *health = h; *maxHealth = mh;
            *stamina = st; *maxStamina = mst;
            *mana = m; *maxMana = mm;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerBaseVitals(uint* baseMaxHp, uint* baseMaxStam, uint* baseMaxMana)
    {
        try
        {
            if (baseMaxHp == null || baseMaxStam == null || baseMaxMana == null) return 0;
            HostContext? c = OwnPlayerCtx();
            if (c == null) return 0;
            CharacterSheet s = c.Sheet();
            uint hp = s.VitalBaseMax(1), st = s.VitalBaseMax(2), mn = s.VitalBaseMax(3);
            if (hp == 0 && st == 0 && mn == 0) return 0;
            *baseMaxHp = hp; *baseMaxStam = st; *baseMaxMana = mn;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>stype: 1 MaxHealth, 2 Health, 3 MaxStamina, 4 Stamina, 5 MaxMana, 6 Mana.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectAttribute2ndBaseLevel(uint objectId, uint stype, uint* value)
    {
        try
        {
            if (value == null || stype < 1 || stype > 6 || objectId == 0) return 0;
            HostContext? c = OwnPlayerCtx(objectId);
            if (c == null) return 0;
            CharacterSheet s = c.Sheet();
            uint vital = (stype + 1) / 2;
            if (!s.HasVital(vital)) return 0;
            uint v = (stype & 1) != 0 ? s.VitalBaseMax(vital) : s.VitalCurrent(vital);
            if (v == 0) return 0;
            *value = v;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static float GetVitae(uint playerId)
    {
        try
        {
            HostContext? c = OwnPlayerCtx(playerId);
            return c == null || playerId == 0 ? 1.0f : c.Sheet().Vitae;
        }
        catch { return 1.0f; }
    }

    // ── attributes and skills ───────────────────────────────────────────────

    /// <summary>stype 1 Strength .. 6 Self; raw 1 = base (init + ranks), 0 = buffed.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectAttribute(uint objectId, uint stype, int raw, uint* value)
    {
        try
        {
            if (value == null || stype < 1 || stype > 6 || objectId == 0) return 0;
            HostContext? c = OwnPlayerCtx(objectId);
            if (c == null) return 0;
            CharacterSheet s = c.Sheet();
            if (!s.HasAttribute(stype)) return 0;
            uint v = raw != 0 ? s.AttributeBase(stype) : s.AttributeBuffed(stype);
            if (v == 0) return 0;
            *value = v;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>The buffed level and the training class (0 inactive, 1 untrained, 2 trained, 3 specialized).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectSkill(uint objectId, uint skill, int* buffed, int* training)
    {
        try
        {
            if (buffed == null || training == null || objectId == 0) return 0;
            HostContext? c = OwnPlayerCtx(objectId);
            if (c == null) return 0;
            CharacterSheet s = c.Sheet();
            if (!s.TryGetSkill(skill, out SkillInfo si)) return 0;
            int level = s.SkillBuffed(si);
            *buffed = level;
            *training = (int)si.Training;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>raw 1 = base (attributes unbuffed, no enchantments), 0 = buffed.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectSkillLevel(uint objectId, uint skill, int raw, int* level)
    {
        try
        {
            if (level == null || objectId == 0) return 0;
            HostContext? c = OwnPlayerCtx(objectId);
            if (c == null) return 0;
            CharacterSheet s = c.Sheet();
            if (!s.TryGetSkill(skill, out SkillInfo si)) return 0;
            *level = raw != 0 ? s.SkillBase(si) : s.SkillBuffed(si);
            return 1;
        }
        catch { return 0; }
    }

    // ── spells ──────────────────────────────────────────────────────────────

    /// <summary>1 known, 0 not, -1 unknown (not the player / no description yet).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsSpellKnown(uint objectId, uint spellId)
    {
        try
        {
            if (objectId == 0) return -1;
            HostContext? c = OwnPlayerCtx(objectId);
            if (c == null) return -1;
            return c.World.Player.Spells.Contains(spellId) ? 1 : 0;
        }
        catch { return -1; }
    }

    /// <summary>The spellbook (ascending ids); the count written, -1 when unknown or empty.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadKnownSpells(uint* spellIds, int maxCount)
    {
        try
        {
            if (spellIds == null || maxCount <= 0) return -1;
            HostContext? c = OwnPlayerCtx();
            if (c == null) return -1;
            uint[] book = c.World.Player.Spells.ToArray();
            if (book.Length == 0) return -1;
            Array.Sort(book);
            int n = Math.Min(book.Length, maxCount);
            for (int i = 0; i < n; i++) spellIds[i] = book[i];
            return n;
        }
        catch { return -1; }
    }

    // ── enchantments ────────────────────────────────────────────────────────

    private static int CopyOwnEnchantments(HostContext c, uint* spellIds, double* expiryTimes, int maxCount)
    {
        Enchantment[] list = c.World.Player.Enchantments.ToArray();
        int n = 0;
        foreach (Enchantment e in list)
        {
            if (n >= maxCount) break;
            if (e.SpellId == CharacterSheet.VitaeSpell || e.SpellId == 0) continue;
            spellIds[n] = e.SpellId;
            expiryTimes[n] = c.EnchantmentExpiry(e);
            n++;
        }
        return n;
    }

    /// <summary>The player's enchantments: spell id + expiry (server seconds). Count written; -1 when unknown.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadPlayerEnchantments(uint* spellIds, double* expiryTimes, int maxCount)
    {
        try
        {
            if (maxCount <= 0) return 0;
            if (spellIds == null || expiryTimes == null) return -1;
            HostContext? c = OwnPlayerCtx();
            return c == null ? -1 : CopyOwnEnchantments(c, spellIds, expiryTimes, maxCount);
        }
        catch { return -1; }
    }

    /// <summary>Only the player's own registry is known; any other object is -1 (unavailable).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ReadObjectEnchantments(uint objectId, uint* spellIds, double* expiryTimes, int maxCount)
    {
        try
        {
            if (maxCount <= 0) return 0;
            if (spellIds == null || expiryTimes == null || objectId == 0) return -1;
            HostContext? c = OwnPlayerCtx(objectId);
            return c == null ? -1 : CopyOwnEnchantments(c, spellIds, expiryTimes, maxCount);
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static double GetServerTime()
    {
        try { return Ctx?.ServerTimeNow ?? 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCharacterTitles(uint* ids, int maxCount, uint* currentTitle) => -1;

    // ── pose ────────────────────────────────────────────────────────────────

    private static bool TryOwnPosition(out WorldPosition p)
    {
        p = default;
        HostContext? c = Ctx;
        if (c == null || c.World.Player.Id == 0) return false;
        if (c.World.PlayerObject?.Position is not { } pos || pos.Cell == 0) return false;
        p = pos;
        return true;
    }

    /// <summary>0 = east, counter-clockwise (degrees), from a yaw quaternion (AC's compass yaw: 0 = north, clockwise).</summary>
    internal static float DecalHeadingFromQuaternion(float qw, float qz)
    {
        double yaw = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);    // counter-clockwise from north
        double h = 90.0 + yaw;
        h %= 360.0;
        if (h < 0) h += 360.0;
        if (h >= 360.0) h -= 360.0;
        return (float)h;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerPose(uint* objCellId, float* x, float* y, float* z, float* qw, float* qx, float* qy, float* qz)
    {
        try
        {
            if (objCellId == null || x == null || y == null || z == null || qw == null || qx == null || qy == null || qz == null) return 0;
            if (!TryOwnPosition(out WorldPosition p)) return 0;
            *objCellId = p.Cell;
            *x = p.X; *y = p.Y; *z = p.Z;
            *qw = p.Qw; *qx = p.Qx; *qy = p.Qy; *qz = p.Qz;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPlayerHeading(float* headingDegrees)
    {
        try
        {
            if (headingDegrees == null) return 0;
            if (!TryOwnPosition(out WorldPosition p)) return 0;
            *headingDegrees = DecalHeadingFromQuaternion(p.Qw, p.Qz);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCurCoords(double* northSouth, double* eastWest)
    {
        try
        {
            if (northSouth == null || eastWest == null) return 0;
            if (!TryOwnPosition(out WorldPosition p)) return 0;
            int lbX = (int)((p.Cell >> 24) & 0xFF);
            int lbY = (int)((p.Cell >> 16) & 0xFF);
            *eastWest = (lbX * 8.0 + p.X / 24.0 - 1019.5) / 10.0;
            *northSouth = (lbY * 8.0 + p.Y / 24.0 - 1019.5) / 10.0;
            return 1;
        }
        catch { return 0; }
    }
}
