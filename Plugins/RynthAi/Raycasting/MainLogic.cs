using System;
using Decal.Adapter;

namespace NexSuite.Plugins.RynthAi.Raycasting
{
    /// <summary>
    /// Main integration point for the raycasting system.
    /// Initializes the .dat file parsers, geometry loader, and targeting FSM.
    /// 
    /// Usage:
    ///   var raycast = new MainLogic();
    ///   raycast.Initialize(acFolderPath);  // Call once at startup
    ///   
    ///   // During combat:
    ///   bool blocked = raycast.IsTargetBlocked(core, targetWorldObject, attackType);
    /// </summary>
    public class MainLogic : IDisposable
    {
        private GeometryLoader _geoLoader;
        private BlacklistManager _blacklist;
        private TargetingFSM _targetingFSM;
        private bool _disposed;

        public GeometryLoader GeometryLoader => _geoLoader;
        public BlacklistManager Blacklist => _blacklist;
        public TargetingFSM TargetingFSM => _targetingFSM;
        public bool IsInitialized => _geoLoader?.IsInitialized ?? false;
        public string StatusMessage => _geoLoader?.StatusMessage ?? "Not created";

        public MainLogic()
        {
            _geoLoader = new GeometryLoader();
            _blacklist = new BlacklistManager();
            _targetingFSM = new TargetingFSM(_geoLoader, _blacklist);
        }

        /// <summary>
        /// Initializes the raycasting system by loading portal.dat and cell.dat.
        /// 
        /// acFolderPath: Optional explicit path to AC installation folder.
        ///               If null, will search common installation paths automatically.
        /// 
        /// Returns true if initialization succeeded (both .dat files loaded).
        /// </summary>
        public bool Initialize(string acFolderPath = null)
        {
            try
            {
                bool result = _geoLoader.Initialize(acFolderPath);
                if (result)
                {
                    System.Diagnostics.Debug.WriteLine("[Raycast] MainLogic initialized successfully");
                }
                else
                    System.Diagnostics.Debug.WriteLine($"[Raycast] MainLogic init failed: {_geoLoader.StatusMessage}");

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Raycast] MainLogic init error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Quick check: is a specific target blocked by geometry?
        /// Call this from CombatManager before engaging a target.
        /// </summary>
        public bool IsTargetBlocked(CoreManager core, Decal.Adapter.Wrappers.WorldObject target,
                                     TargetingFSM.AttackType attackType)
        {
            if (!IsInitialized || _targetingFSM == null)
                return false;

            return _targetingFSM.IsTargetBlocked(core, target, attackType);
        }

        /// <summary>
        /// Determines the attack type from the currently wielded weapon.
        /// </summary>
        public TargetingFSM.AttackType GetAttackType(CoreManager core)
        {
            return _targetingFSM?.DetermineAttackType(core) ?? TargetingFSM.AttackType.Linear;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _geoLoader?.Dispose();
            _blacklist?.ClearAll();

            _disposed = true;
            System.Diagnostics.Debug.WriteLine("[Raycast] MainLogic disposed");
        }
    }
}
