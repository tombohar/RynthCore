using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace NexSuite.Plugins.RynthAi
{
    public class RouteUI
    {
        private readonly UISettings _settings;
        private readonly CoreManager _core;

        public Action OnSettingsChanged;

        // --- ROUTE STATE ---
        private readonly string[] _routeTypes = { "Once", "Circular", "Linear", "Follow" };
        private readonly string[] _addModes = { "End", "Above", "Below" };
        private int _addModeIdx = 0;
        private int _selectedRouteIndex = -1;

        // --- GRAPHICS STATE ---
        public bool NeedsRouteGraphicsRefresh = false;
        private List<D3DObj> _routeGraphics = new List<D3DObj>();
        public List<D3DObj> TestD3DObjects = new List<D3DObj>();

        public RouteUI(UISettings settings, CoreManager core)
        {
            _settings = settings;
            _core = core;
        }

        public void Render()
        {
            string activeNavName = string.IsNullOrEmpty(_settings.CurrentNavPath) ? "None (Unsaved)" : Path.GetFileName(_settings.CurrentNavPath);
            ImGui.TextColored(new Vector4(1, 1, 0, 1), $"Active Nav: {activeNavName}");
            ImGui.Separator();

            int rTypeIdx = 0;
            if (_settings.CurrentRoute.RouteType == NavRouteType.Circular) rTypeIdx = 1;
            else if (_settings.CurrentRoute.RouteType == NavRouteType.Linear) rTypeIdx = 2;
            else if (_settings.CurrentRoute.RouteType == NavRouteType.Follow) rTypeIdx = 3;

            ImGui.SetNextItemWidth(100);
            if (ImGui.Combo("Route Type", ref rTypeIdx, _routeTypes, _routeTypes.Length))
            {
                if (rTypeIdx == 0) _settings.CurrentRoute.RouteType = NavRouteType.Once;
                else if (rTypeIdx == 1) _settings.CurrentRoute.RouteType = NavRouteType.Circular;
                else if (rTypeIdx == 2) _settings.CurrentRoute.RouteType = NavRouteType.Linear;
                else if (rTypeIdx == 3) _settings.CurrentRoute.RouteType = NavRouteType.Follow;

                TryAutoSaveNav();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.Combo("Insert", ref _addModeIdx, _addModes, _addModes.Length);

            ImGui.Spacing();

            if (ImGui.Button("Add Waypoint", new Vector2(100, 25)))
            {
                var me = _core.WorldFilter[_core.CharacterFilter.Id];
                if (me != null)
                {
                    var newPt = new NavPoint
                    {
                        NS = me.Coordinates().NorthSouth,
                        EW = me.Coordinates().EastWest,
                        Z = me.Offset().Z
                    };
                    InsertPoint(newPt);
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Add Portal", new Vector2(100, 25)))
            {
                try
                {
                    var me = _core.WorldFilter[_core.CharacterFilter.Id];
                    if (me != null)
                    {
                        var myPos = me.Coordinates();
                        WorldObject best = null;
                        double bestDist = double.MaxValue;
                        foreach (WorldObject wo in _core.WorldFilter.GetLandscape())
                        {
                            if (wo == null) continue;
                            if (wo.ObjectClass == ObjectClass.Portal || wo.ObjectClass == ObjectClass.Npc || wo.ObjectClass == ObjectClass.Vendor)
                            {
                                try
                                {
                                    double dNS = wo.Coordinates().NorthSouth - myPos.NorthSouth;
                                    double dEW = wo.Coordinates().EastWest - myPos.EastWest;
                                    double d = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;
                                    if (d < bestDist) { bestDist = d; best = wo; }
                                }
                                catch { }
                            }
                        }
                        if (best != null && bestDist < 20.0)
                        {
                            var newPt = new NavPoint
                            {
                                Type = NavPointType.PortalNPC,
                                NS = myPos.NorthSouth,
                                EW = myPos.EastWest,
                                Z = me.Offset().Z,
                                TargetName = best.Name,
                                ObjectClass = (int)best.ObjectClass,
                                IsTie = false
                            };
                            InsertPoint(newPt);
                            _core.Actions.AddChatText($"[RynthAi] Added Portal/NPC: {best.Name} ({bestDist:F1}yd)", 1);
                        }
                        else
                        {
                            _core.Actions.AddChatText("[RynthAi] No portal/NPC found within 20 yards.", 1);
                        }
                    }
                }
                catch (Exception ex) { _core.Actions.AddChatText($"[RynthAi] Error: {ex.Message}", 1); }
            }

            ImGui.SameLine();
            if (ImGui.Button("Add Recall", new Vector2(100, 25)))
            {
                ImGui.OpenPopup("RecallPopup");
            }
            if (ImGui.BeginPopup("RecallPopup"))
            {
                int[] ids = {
                    48, 2645, 2647,
                    1635, 1636,
                    157, 158, 1637,
                    2648, 2649, 2650,
                    2931, 2023, 2041, 2358, 2813, 2941, 2943,
                    3865, 3929, 3930, 4084, 4198, 4213,
                    4907, 4908, 4909,
                    5175, 5330, 5541, 6150, 6321, 6322
                };

                for (int ri = 0; ri < ids.Length; ri++)
                {
                    string name = SpellDatabase.GetSpellName(ids[ri]);
                    if (ImGui.Selectable($"{name} ({ids[ri]})"))
                    {
                        var me = _core.WorldFilter[_core.CharacterFilter.Id];
                        if (me != null)
                        {
                            var newPt = new NavPoint
                            {
                                Type = NavPointType.Recall,
                                NS = me.Coordinates().NorthSouth,
                                EW = me.Coordinates().EastWest,
                                Z = me.Offset().Z,
                                SpellId = ids[ri]
                            };
                            InsertPoint(newPt);
                            _core.Actions.AddChatText($"[RynthAi] Added Recall: {name} ({ids[ri]})", 1);
                        }
                    }
                }
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear Route", new Vector2(100, 25)))
            {
                _settings.CurrentRoute.Points.Clear();
                _settings.ActiveNavIndex = 0;
                TryAutoSaveNav();
                DisposeRouteGraphics();
                OnSettingsChanged?.Invoke();
            }

            ImGui.SameLine();
            if (ImGui.Button("Save Route", new Vector2(100, 25)))
            {
                if (!string.IsNullOrEmpty(_settings.CurrentNavPath))
                {
                    try { _settings.CurrentRoute.Save(_settings.CurrentNavPath); _core.Actions.AddChatText("[RynthAi] Route saved.", 1); } catch { }
                }
            }

            if (ImGui.BeginListBox("##RoutePoints", new Vector2(-1, 200)))
            {
                for (int i = 0; i < _settings.CurrentRoute.Points.Count; i++)
                {
                    // Push a unique ID so ImGui doesn't confuse the 50 different "X" buttons
                    ImGui.PushID($"route_pt_{i}");

                    // Draw the red "X" button
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                    if (ImGui.Button("X", new Vector2(20, 20)))
                    {
                        // 1. Remove the point
                        _settings.CurrentRoute.Points.RemoveAt(i);

                        // 2. Safely shift our selected/active indexes so we don't crash
                        if (_selectedRouteIndex == i) _selectedRouteIndex = -1;
                        else if (_selectedRouteIndex > i) _selectedRouteIndex--;

                        if (_settings.ActiveNavIndex == i) _settings.ActiveNavIndex = 0;
                        else if (_settings.ActiveNavIndex > i) _settings.ActiveNavIndex--;

                        // 3. Save, update 3D graphics, and notify the plugin
                        TryAutoSaveNav();
                        UpdateRouteGraphics();
                        OnSettingsChanged?.Invoke();

                        // 4. Pop the styles and break the loop for this single frame to prevent index out-of-range errors
                        ImGui.PopStyleColor();
                        ImGui.PopID();
                        break;
                    }
                    ImGui.PopStyleColor();

                    ImGui.SameLine();

                    // Draw the selectable row text
                    string prefix = "   ";
                    if (i == _settings.ActiveNavIndex) prefix = "==>";

                    bool isSelected = (_selectedRouteIndex == i);
                    if (ImGui.Selectable($"{prefix} [{i}] {_settings.CurrentRoute.Points[i]}", isSelected))
                    {
                        _selectedRouteIndex = i;
                    }

                    ImGui.PopID();
                }
                ImGui.EndListBox();
            }
        }

        public void InsertPoint(NavPoint newPt)
        {
            if (_addModeIdx == 0 || _settings.CurrentRoute.Points.Count == 0)
                _settings.CurrentRoute.Points.Add(newPt);
            else if (_addModeIdx == 1 && _selectedRouteIndex >= 0)
                _settings.CurrentRoute.Points.Insert(_selectedRouteIndex, newPt);
            else if (_addModeIdx == 2 && _selectedRouteIndex >= 0)
                _settings.CurrentRoute.Points.Insert(_selectedRouteIndex + 1, newPt);
            else
                _settings.CurrentRoute.Points.Add(newPt);

            TryAutoSaveNav();
            UpdateRouteGraphics();
            OnSettingsChanged?.Invoke();
        }

        public void TryAutoSaveNav()
        {
            if (!string.IsNullOrEmpty(_settings.CurrentNavPath))
            {
                try { _settings.CurrentRoute.Save(_settings.CurrentNavPath); } catch { }
            }
        }

        public void UpdateRouteGraphics()
        {
            DisposeRouteGraphics();

            if (_settings.CurrentRoute?.Points == null || _settings.CurrentRoute.Points.Count == 0)
                return;

            if (_core?.D3DService == null)
                return;

            int colorAqua = unchecked((int)0xFF00FFFF);
            int colorBlue = unchecked((int)0xFF0088FF);
            int colorRed = unchecked((int)0xFFFF2222);

            float ringScale = Math.Max(0.5f, (float)_settings.FollowNavMin);

            try
            {
                for (int i = 0; i < _settings.CurrentRoute.Points.Count; i++)
                {
                    var pt = _settings.CurrentRoute.Points[i];
                    int wpColor = (i == _settings.ActiveNavIndex) ? colorRed : colorAqua;

                    var ring = _core.D3DService.MarkCoordsWithShape(
                        (float)pt.NS, (float)pt.EW, (float)pt.Z + 0.5f,
                        Decal.Adapter.Wrappers.D3DShape.Ring, wpColor);
                    ring.Visible = true;
                    ring.ScaleX = ringScale;
                    ring.ScaleY = ringScale;
                    ring.ScaleZ = 0.1f;
                    try { ring.Anchor((float)pt.NS, (float)pt.EW, (float)pt.Z + 0.5f); } catch { }
                    _routeGraphics.Add(ring);

                    int nextIdx = i + 1;
                    if (nextIdx >= _settings.CurrentRoute.Points.Count)
                    {
                        if (_settings.CurrentRoute.RouteType == NavRouteType.Circular) nextIdx = 0;
                        else continue;
                    }

                    var nextPt = _settings.CurrentRoute.Points[nextIdx];
                    double dNS = nextPt.NS - pt.NS;
                    double dEW = nextPt.EW - pt.EW;
                    double dZ = nextPt.Z - pt.Z;
                    double dist2D = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;

                    int dots = Math.Min(150, Math.Max(2, (int)dist2D));

                    for (int d = 1; d < dots; d++)
                    {
                        double f = (double)d / dots;
                        var dot = _core.D3DService.MarkCoordsWithShape(
                            (float)(pt.NS + dNS * f),
                            (float)(pt.EW + dEW * f),
                            (float)(pt.Z + dZ * f) + 0.5f,
                            Decal.Adapter.Wrappers.D3DShape.Sphere, colorBlue);
                        dot.Visible = true;
                        dot.Scale(0.15f);
                        _routeGraphics.Add(dot);
                    }
                }
            }
            catch { }
        }

        public void DisposeRouteGraphics()
        {
            foreach (var o in _routeGraphics) { try { o.Visible = false; ((IDisposable)o).Dispose(); } catch { } }
            _routeGraphics.Clear();
            foreach (var o in TestD3DObjects) { try { o.Visible = false; ((IDisposable)o).Dispose(); } catch { } }
            TestD3DObjects.Clear();
        }
    }
}