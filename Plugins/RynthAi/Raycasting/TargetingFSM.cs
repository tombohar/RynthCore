using Decal.Adapter;
using Decal.Adapter.Wrappers;
using System;
using System.Collections.Generic;

namespace NexSuite.Plugins.RynthAi.Raycasting
{
    /// <summary>
    /// Provides line-of-sight checking for the combat system.
    /// Converts Decal API coordinates to global meter space and
    /// performs raycasting against loaded landblock geometry.
    /// 
    /// Coordinate system:
    ///   AC uses a landblock grid where each block is 192×192 meters.
    ///   Landcell ID format: 0xXXYYnnnn where XX=east-west block, YY=north-south block.
    ///   LocationX/Y are local offsets within the landblock (0-192 meters).
    ///   
    ///   We convert everything to "global meters" for raycasting:
    ///     GlobalX = (XX * 192) + LocationX
    ///     GlobalY = (YY * 192) + LocationY
    ///     GlobalZ = LocationZ (altitude, 0 = sea level)
    /// </summary>
    public class TargetingFSM
    {
        private readonly GeometryLoader _geoLoader;
        private readonly BlacklistManager _blacklist;

        // Attack type determines which raycast to use
        public enum AttackType
        {
            Linear,    // Bolts, streaks, crossbow bolts, melee
            BowArc,    // Bows — moderate arc, arrows go higher than you'd think
            ThrownArc, // Thrown weapons, atlatls — similar arc to bows
            MagicArc   // War/Void magic Arc spells — same trajectory as missile weapons
        }

        // AC arrows arc noticeably — they go HIGH. A lower velocity = higher arc.
        // At 70 yard range, arrows visibly arc 3-4 meters above the direct line.
        // This must match the actual game trajectory to detect ceiling hits in dungeons.
        public float BowArcVelocity { get; set; } = 25.0f;

        // Thrown weapons arc similarly to bows
        public float ThrownArcVelocity { get; set; } = 22.0f;

        // Magic arc spells (War/Void Arc) have the same trajectory as missile weapons
        public float MagicArcVelocity { get; set; } = 25.0f;

        // If true, use arc checks for missile weapons. If false, treat all as linear.
        public bool UseArcs { get; set; } = true;

        // Max scan distance in meters. Only check LOS for targets within this range.
        // Set from CombatManager based on MonsterRange + buffer.
        public float MaxScanDistanceMeters { get; set; } = 120.0f;

        public TargetingFSM(GeometryLoader geoLoader, BlacklistManager blacklist)
        {
            _geoLoader = geoLoader ?? throw new ArgumentNullException(nameof(geoLoader));
            _blacklist = blacklist ?? throw new ArgumentNullException(nameof(blacklist));
        }

        /// <summary>
        /// Checks if line-of-sight to a target is blocked by geometry.
        /// 
        /// Returns true if the target IS blocked (should skip this target).
        /// Returns false if the target is clear to attack.
        /// </summary>
        public bool IsTargetBlocked(CoreManager core, WorldObject target, AttackType attackType)
        {
            if (core == null || target == null)
                return false;

            if (!_geoLoader.IsInitialized)
                return false;

            try
            {
                Vector3 origin = GetPlayerPosition(core);
                if (origin == Vector3.Zero)
                    return false;

                Vector3 targetPos = GetObjectPosition(target, core);
                if (targetPos == Vector3.Zero)
                    return false;

                // Early-out: skip raycast if target is beyond scan distance
                float dx = targetPos.X - origin.X;
                float dy = targetPos.Y - origin.Y;
                float flatDist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (flatDist > MaxScanDistanceMeters)
                    return false; // Too far to bother checking, let combat handle range

                // Offset to chest height
                origin.Z += 1.0f;
                targetPos.Z += 1.0f;

                uint landcell = GetPlayerLandcell(core);
                uint cellPart = landcell & 0xFFFF;
                bool isDungeon = cellPart >= 0x0100;

                // Force linear checks indoors — arc trajectories hit ceilings
                if (isDungeon)
                    attackType = AttackType.Linear;

                var geometry = _geoLoader.GetLandblockGeometry(landcell);

                if (geometry == null || geometry.Count == 0)
                    return false;

                // Pre-filter: skip full ray test if no geometry near the path
                float margin = Math.Min(flatDist * 0.3f, 15.0f);
                margin = Math.Max(margin, 3.0f);
                if (!RaycastEngine.HasNearbyGeometry(origin, targetPos, geometry, margin))
                    return false;

                switch (attackType)
                {
                    case AttackType.Linear:
                        return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry);

                    case AttackType.BowArc:
                        if (UseArcs)
                        {
                            if (!RaycastEngine.IsArcPathBlocked(origin, targetPos, BowArcVelocity, geometry))
                                return false;
                            return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry);
                        }
                        return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry);

                    case AttackType.ThrownArc:
                        if (UseArcs)
                        {
                            if (!RaycastEngine.IsArcPathBlocked(origin, targetPos, ThrownArcVelocity, geometry))
                                return false;
                            return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry);
                        }
                        return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry);

                    case AttackType.MagicArc:
                        // Magic Arc spells have the same trajectory as missile weapons
                        if (UseArcs && !isDungeon) // Don't arc-check in dungeons (ceiling clips)
                        {
                            if (!RaycastEngine.IsArcPathBlocked(origin, targetPos, MagicArcVelocity, geometry))
                                return false;
                            return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry);
                        }
                        return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry);

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Targeting] Error checking LOS: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finds the best unblocked target from a list of candidates.
        /// Returns the target ID, or 0 if no clear target is found.
        /// </summary>
        public int FindBestTarget(CoreManager core, List<WorldObject> candidates, AttackType attackType)
        {
            if (core == null || candidates == null || candidates.Count == 0)
                return 0;

            foreach (var candidate in candidates)
            {
                try
                {
                    int id = candidate.Id;

                    // Skip blacklisted targets
                    if (_blacklist.IsBlacklisted(id))
                        continue;

                    // Skip blocked targets
                    if (IsTargetBlocked(core, candidate, attackType))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Targeting] Target {id} blocked by geometry, skipping");
                        continue;
                    }

                    return id; // Found a clear target
                }
                catch
                {
                    continue;
                }
            }

            return 0; // No clear targets
        }

        /// <summary>
        /// Gets the player's position in global meter coordinates.
        /// Uses Decal's HooksWrapper (accessed via CoreManager.Actions).
        /// 
        /// Landcell format: 0xXXYYnnnn
        ///   XX = east-west block index (0-254)
        ///   YY = north-south block index (0-254)
        /// 
        /// LocationX/Y = offset within landblock (0-192 meters for outdoors)
        /// LocationZ = altitude in meters (0 = sea level)
        /// 
        /// Global conversion:
        ///   GlobalX = XX * 192 + LocationX
        ///   GlobalY = YY * 192 + LocationY  
        ///   GlobalZ = LocationZ
        /// </summary>
        private Vector3 GetPlayerPosition(CoreManager core)
        {
            try
            {
                uint landcell = (uint)core.Actions.Landcell;

                uint blockX = (landcell >> 24) & 0xFF;
                uint blockY = (landcell >> 16) & 0xFF;

                float localX = (float)core.Actions.LocationX;
                float localY = (float)core.Actions.LocationY;
                float localZ = (float)core.Actions.LocationZ;

                float globalX = blockX * 192.0f + localX;
                float globalY = blockY * 192.0f + localY;

                return new Vector3(globalX, globalY, localZ);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Targeting] Error getting player pos: {ex.Message}");
                return Vector3.Zero;
            }
        }

        /// <summary>
        /// Gets a world object's position in global meter coordinates.
        /// Uses Decal's CoordsObject (EastWest/NorthSouth) and converts to meters.
        /// 
        /// From Decal docs:
        ///   EW = ((Landcell >> 24) + LocationX / 24.0 - 1019.5) / 10.0
        ///   NS = (((Landcell >> 16) & 0xFF) + LocationY / 24.0 - 1019.5) / 10.0
        /// 
        /// Reverse conversion (EW/NS → global meters):
        ///   GlobalX = (EW * 10.0 + 1019.5) * 24.0
        ///   GlobalY = (NS * 10.0 + 1019.5) * 24.0
        /// </summary>
        private Vector3 GetObjectPosition(WorldObject obj, CoreManager core)
        {
            try
            {
                var coords = obj.Coordinates();
                if (coords == null)
                    return Vector3.Zero;

                double ew = coords.EastWest;
                double ns = coords.NorthSouth;

                float globalX = (float)((ew * 10.0 + 1019.5) * 24.0);
                float globalY = (float)((ns * 10.0 + 1019.5) * 24.0);

                // Z coordinate: MUST come from RawCoordinates.
                // If unavailable, return Zero to skip LOS (safer than guessing wrong Z).
                float z = 0;
                try
                {
                    var raw = obj.RawCoordinates();
                    if (raw != null)
                        z = (float)raw.Z;
                    else
                        return Vector3.Zero; // Can't determine Z — skip LOS
                }
                catch
                {
                    return Vector3.Zero; // Can't determine Z — skip LOS
                }

                return new Vector3(globalX, globalY, z);
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        /// <summary>
        /// Gets the player's current Landcell value for geometry lookup.
        /// </summary>
        private uint GetPlayerLandcell(CoreManager core)
        {
            try
            {
                return (uint)core.Actions.Landcell;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Determines the attack type based on combat mode and wielded weapon.
        /// Magic mode ALWAYS returns Linear (spells travel straight).
        /// Bows get a flat arc (high velocity). Thrown weapons arc more.
        /// Melee and crossbows are Linear.
        /// </summary>
        public AttackType DetermineAttackType(CoreManager core)
        {
            try
            {
                // MAGIC MODE: Always linear — spells (bolts, streaks, arcs, rings)
                // all use straight-line LOS in AC, regardless of equipped weapon
                if (core.Actions.CombatMode == CombatState.Magic)
                    return AttackType.Linear;

                // PEACE MODE: Default to linear
                if (core.Actions.CombatMode == CombatState.Peace)
                    return AttackType.Linear;

                // MELEE MODE: Linear (range check only, no projectile arc)
                if (core.Actions.CombatMode == CombatState.Melee)
                    return AttackType.Linear;

                // MISSILE MODE: Check the actual weapon for bow vs crossbow vs thrown
                foreach (var wo in core.WorldFilter.GetInventory())
                {
                    if (wo.Values((LongValueKey)10, 0) > 0) // CurrentWieldedLocation
                    {
                        if (wo.ObjectClass == ObjectClass.MissileWeapon)
                        {
                            string name = wo.Name?.ToLower() ?? "";

                            // Crossbows fire bolts in a straight line
                            if (name.Contains("crossbow"))
                                return AttackType.Linear;

                            // Atlatls and thrown weapons arc more
                            if (name.Contains("atlatl") || name.Contains("thrown") || name.Contains("dart"))
                                return UseArcs ? AttackType.ThrownArc : AttackType.Linear;

                            // Regular bows — fast, flat arc
                            if (name.Contains("bow"))
                                return UseArcs ? AttackType.BowArc : AttackType.Linear;

                            // Unknown missile weapon — treat as bow arc
                            return UseArcs ? AttackType.BowArc : AttackType.Linear;
                        }

                        // Wand in missile mode? Shouldn't happen, but treat as linear
                        if (wo.ObjectClass == ObjectClass.WandStaffOrb)
                            return AttackType.Linear;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Targeting] Error determining attack type: {ex.Message}");
            }

            return AttackType.Linear; // Default to linear
        }
    }
}
