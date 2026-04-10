using System;
using System.Text.RegularExpressions;
using Decal.Adapter.Wrappers;

namespace NexSuite.Plugins.RynthAi
{
    public class LuaBridge
    {
        private readonly PluginCore _core;
        // Returns the actual North/South coordinate (e.g. 42.5)
        public double GetPlayerNS()
        {
            return Decal.Adapter.CoreManager.Current.WorldFilter[Decal.Adapter.CoreManager.Current.CharacterFilter.Id].Coordinates().NorthSouth;
        }

        // Returns the actual East/West coordinate (e.g. -33.7)
        public double GetPlayerEW()
        {
            return Decal.Adapter.CoreManager.Current.WorldFilter[Decal.Adapter.CoreManager.Current.CharacterFilter.Id].Coordinates().EastWest;
        }

        // Returns the altitude in meters
        public double GetPlayerZ()
        {
            return Decal.Adapter.CoreManager.Current.Actions.LocationZ;
        }

        public LuaBridge(PluginCore core)
        {
            _core = core;
        }

        // --- MOVEMENT ---
        public void GoTo(string ns, string ew)
        {
            double dNS = ParseCoord(ns);
            double dEW = ParseCoord(ew);

            _core.UI.Settings.CurrentRoute.Points.Clear();
            _core.UI.Settings.CurrentRoute.Points.Add(new NavPoint { NS = dNS, EW = dEW, Type = NavPointType.Point });
            _core.UI.Settings.CurrentRoute.RouteType = NavRouteType.Once;
            _core.UI.Settings.ActiveNavIndex = 0;

            _core.UI.Settings.IsMacroRunning = true;
            _core.UI.Settings.EnableNavigation = true;

            if (_core.UI?.RouteTab != null) _core.UI.RouteTab.NeedsRouteGraphicsRefresh = true;
            _core.PluginHost.Actions.AddChatText($"[RynthLua] Navigating to: {ns}, {ew}", 1);
        }

        public void Stop()
        {
            _core.UI.Settings.IsMacroRunning = false;
            _core.UI.Settings.EnableNavigation = false;
            _core.PluginHost.Actions.AddChatText("[RynthLua] Macro Stopped.", 1);
        }

        // --- INTERACTION ---
        public void UsePortal(string portalName)
        {
            foreach (WorldObject wo in _core.PluginCoreManager.WorldFilter.GetLandscape())
            {
                if (wo.Name.IndexOf(portalName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (wo.ObjectClass == ObjectClass.Portal || wo.ObjectClass == ObjectClass.Npc)
                    {
                        // The correct Decal API call: UseItem(GUID, ContainerID)
                        // We use 0 for the container because portals are in the world.
                        _core.PluginHost.Actions.UseItem(wo.Id, 0);
                        return;
                    }
                }
            }
        }

        // --- UTILS ---
        private double ParseCoord(string val)
        {
            if (string.IsNullOrEmpty(val)) return 0;
            string clean = Regex.Replace(val, "[^0-9.-]", "");
            if (!double.TryParse(clean, out double result)) return 0;
            return (val.ToUpper().Contains("S") || val.ToUpper().Contains("W")) ? -result : result;
        }

        public bool IsPathClear(double startNS, double startEW, double startZ, double endNS, double endEW, double endZ)
        {
            try
            {
                // Use '_core' instead of '_plugin'
                if (_core.Raycast == null || !_core.Raycast.IsInitialized)
                    return true;

                // Convert EW/NS to Global Meters
                float startX = (float)((startEW * 10.0 + 1019.5) * 24.0);
                float startY = (float)((startNS * 10.0 + 1019.5) * 24.0);
                var origin = new RynthAi.Raycasting.Vector3(startX, startY, (float)startZ + 1.0f);

                float endX = (float)((endEW * 10.0 + 1019.5) * 24.0);
                float endY = (float)((endNS * 10.0 + 1019.5) * 24.0);
                var destination = new RynthAi.Raycasting.Vector3(endX, endY, (float)endZ + 1.0f);

                // Access Landcell safely via CoreManager
                uint landcell = (uint)Decal.Adapter.CoreManager.Current.Actions.Landcell;
                var geometry = _core.Raycast.GeometryLoader.GetLandblockGeometry(landcell);

                // Test the line
                return !RynthAi.Raycasting.RaycastEngine.IsLinearPathBlocked(origin, destination, geometry);
            }
            catch
            {
                return true; // Default to assuming clear if it fails, to prevent the bot from freezing
            }
        }
    }
}