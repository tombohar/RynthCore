using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Plugin.RynthAi.Nav;

public enum NavRouteType
{
    Once = 4,
    Circular = 1,
    Linear = 2,
    Follow = 3
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
    public string ChatCommand { get; set; } = "";
    public string TargetName { get; set; } = "";
    public int ObjectClass { get; set; }
    public bool IsTie { get; set; }

    public double PortalExitNS { get; set; }
    public double PortalExitEW { get; set; }
    public double PortalExitZ { get; set; }
    public double PortalLandNS { get; set; }
    public double PortalLandEW { get; set; }
    public double PortalLandZ { get; set; }

    public override string ToString()
    {
        return Type switch
        {
            NavPointType.Point => $"[Point] {NS:F3}, {EW:F3}",
            NavPointType.Recall => $"[Recall] spell {SpellId}",
            NavPointType.Pause => $"[Pause] {PauseTimeMs / 1000.0}s",
            NavPointType.Chat => $"[Chat] {ChatCommand}",
            NavPointType.PortalNPC => $"[Portal] {TargetName}",
            _ => $"[Unknown] {Type}"
        };
    }
}

public class VTankNavParser
{
    public NavRouteType RouteType { get; set; }
    public List<NavPoint> Points { get; set; } = new();

    public static VTankNavParser Load(string filePath)
    {
        var route = new VTankNavParser();
        string[] lines = File.ReadAllLines(filePath);

        if (lines.Length < 3) return route;
        if (!lines[0].Contains("uTank2 NAV 1.2"))
            return route;

        route.RouteType = (NavRouteType)int.Parse(lines[1]);
        int pointCount = int.Parse(lines[2]);

        int idx = 3;
        for (int i = 0; i < pointCount && idx < lines.Length; i++)
        {
            var pt = new NavPoint();
            pt.Type = (NavPointType)int.Parse(lines[idx++]);
            pt.EW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
            pt.NS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
            pt.Z = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
            idx++; // spacer

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
                    pt.PortalExitEW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    pt.PortalExitNS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    pt.PortalExitZ = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    idx++; // spacer
                    pt.PortalLandEW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    pt.PortalLandNS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    pt.PortalLandZ = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                    idx++; // spacer
                    break;
            }

            route.Points.Add(pt);
        }

        return route;
    }

    public void Save(string filePath)
    {
        using var writer = new StreamWriter(filePath, false);
        writer.WriteLine("uTank2 NAV 1.2");
        writer.WriteLine((int)RouteType);
        writer.WriteLine(Points.Count);

        foreach (var pt in Points)
        {
            writer.WriteLine((int)pt.Type);
            writer.WriteLine(pt.EW.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(pt.NS.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(pt.Z.ToString(CultureInfo.InvariantCulture));
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
                    writer.WriteLine(pt.PortalExitEW.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(pt.PortalExitNS.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(pt.PortalExitZ.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("0");
                    writer.WriteLine(pt.PortalLandEW.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(pt.PortalLandNS.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(pt.PortalLandZ.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("0");
                    break;
            }
        }
    }
}
