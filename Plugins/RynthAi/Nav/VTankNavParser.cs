using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

namespace NexSuite.Plugins.RynthAi
{
    public enum NavRouteType
    {
        Circular = 1,
        Linear = 2,
        Follow = 3,
        Once = 4
    }

    public enum NavPointType
    {
        Point = 0,
        Recall = 2,
        Pause = 3,
        Chat = 4,
        PortalNPC = 6
    }

    public class NavPoint
    {
        public NavPointType Type { get; set; }
        public double NS { get; set; }
        public double EW { get; set; }
        public double Z { get; set; }

        public int SpellId { get; set; }
        public int PauseTimeMs { get; set; }
        public string ChatCommand { get; set; }
        public string TargetName { get; set; }
        public int ObjectClass { get; set; }
        public bool IsTie { get; set; }

        // PortalNPC extra coordinate blocks (exit/destination coords)
        public double PortalExitNS { get; set; }
        public double PortalExitEW { get; set; }
        public double PortalExitZ { get; set; }
        public double PortalLandNS { get; set; }
        public double PortalLandEW { get; set; }
        public double PortalLandZ { get; set; }

        public override string ToString()
        {
            switch (Type)
            {
                // UI display converted to exact 3-decimal precision
                case NavPointType.Point: return $"[Point] {NS:F3}, {EW:F3}";
                case NavPointType.Recall: 
                    {
                        string spellName = SpellDatabase.GetSpellName(SpellId);
                        return $"[Recall] {spellName} ({SpellId})";
                    }
                case NavPointType.Pause: return $"[Pause] {PauseTimeMs / 1000.0}s";
                case NavPointType.Chat: return $"[Chat] {ChatCommand}";
                case NavPointType.PortalNPC: return $"[Portal] {TargetName}";
                default: return $"[Unknown] {Type}";
            }
        }
    }

    public class VTankNavParser
    {
        public NavRouteType RouteType { get; set; }
        public List<NavPoint> Points { get; set; } = new List<NavPoint>();

        public static VTankNavParser Load(string filePath)
        {
            var route = new VTankNavParser();
            string[] lines = File.ReadAllLines(filePath);

            if (lines.Length < 3) return route;

            if (!lines[0].Contains("uTank2 NAV 1.2"))
                throw new Exception("Invalid or unsupported VTank .nav file format.");

            route.RouteType = (NavRouteType)int.Parse(lines[1]);
            int pointCount = int.Parse(lines[2]);

            int idx = 3;
            for (int i = 0; i < pointCount && idx < lines.Length; i++)
            {
                var pt = new NavPoint();
                pt.Type = (NavPointType)int.Parse(lines[idx++]);

                pt.EW = double.Parse(lines[idx++], CultureInfo.InvariantCulture); // First coord is EW
                pt.NS = double.Parse(lines[idx++], CultureInfo.InvariantCulture); // Second coord is NS
                pt.Z = double.Parse(lines[idx++], CultureInfo.InvariantCulture);

                idx++; // Skip the 0 spacer line

                switch (pt.Type)
                {
                    case NavPointType.Point: break;
                    case NavPointType.Recall: pt.SpellId = int.Parse(lines[idx++]); break;
                    case NavPointType.Pause: pt.PauseTimeMs = int.Parse(lines[idx++]); break;
                    case NavPointType.Chat: pt.ChatCommand = lines[idx++]; break;
                    case NavPointType.PortalNPC:
                        pt.TargetName = lines[idx++];
                        pt.ObjectClass = int.Parse(lines[idx++]);
                        pt.IsTie = bool.Parse(lines[idx++]);
                        // Two extra coordinate blocks (exit + landing) - also EW/NS/Z format
                        pt.PortalExitEW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        pt.PortalExitNS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        pt.PortalExitZ  = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        idx++; // spacer
                        pt.PortalLandEW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        pt.PortalLandNS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        pt.PortalLandZ  = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        idx++; // spacer
                        break;
                }

                route.Points.Add(pt);
            }

            return route;
        }

        public void Save(string filePath)
        {
            using (System.IO.StreamWriter writer = new System.IO.StreamWriter(filePath, false))
            {
                writer.WriteLine("uTank2 NAV 1.2");
                writer.WriteLine((int)RouteType);
                writer.WriteLine(Points.Count);

                foreach (var pt in Points)
                {
                    writer.WriteLine((int)pt.Type);
                    writer.WriteLine(pt.EW.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteLine(pt.NS.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteLine(pt.Z.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteLine("0");

                    switch (pt.Type)
                    {
                        case NavPointType.Recall: writer.WriteLine(pt.SpellId); break;
                        case NavPointType.Pause: writer.WriteLine(pt.PauseTimeMs); break;
                        case NavPointType.Chat: writer.WriteLine(pt.ChatCommand); break;
                        case NavPointType.PortalNPC:
                            writer.WriteLine(pt.TargetName);
                            writer.WriteLine(pt.ObjectClass);
                            writer.WriteLine(pt.IsTie.ToString());
                            writer.WriteLine(pt.PortalExitEW.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            writer.WriteLine(pt.PortalExitNS.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            writer.WriteLine(pt.PortalExitZ.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            writer.WriteLine("0");
                            writer.WriteLine(pt.PortalLandEW.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            writer.WriteLine(pt.PortalLandNS.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            writer.WriteLine(pt.PortalLandZ.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            writer.WriteLine("0");
                            break;
                    }
                }
            }
        }
    }
}