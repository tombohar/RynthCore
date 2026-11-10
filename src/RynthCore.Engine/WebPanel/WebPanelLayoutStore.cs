using System;
using System.IO;
using System.Text.Json;

namespace RynthCore.Engine.WebPanel;

/// <summary>
/// The panel's remembered placement: &lt;LocalAppData&gt;\RynthCore\WebPanel\layout.json, {"x","y","w","h"} as
/// fractions of the game client rect (WebPanelWindowPolicy.FromUserRect). A valid stored layout beats the
/// caller's percentages. Called on the panel thread only.
/// </summary>
internal static class WebPanelLayoutStore
{
    public static string PathFor(string localAppData) => Path.Combine(localAppData, "RynthCore", "WebPanel", "layout.json");

    public static bool TryLoad(string localAppData, out PanelFractions layout)
    {
        layout = default;
        try
        {
            string path = PathFor(localAppData);
            if (!File.Exists(path)) return false;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return false;
            if (!Num(r, "x", out double x) || !Num(r, "y", out double y) || !Num(r, "w", out double w) || !Num(r, "h", out double h)) return false;
            var f = new PanelFractions(x, y, w, h);
            if (!IsValid(f)) return false;
            layout = f;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsValid(PanelFractions f)
    {
        const double Slack = 1e-6;
        if (!double.IsFinite(f.X) || !double.IsFinite(f.Y) || !double.IsFinite(f.W) || !double.IsFinite(f.H)) return false;
        if (f.W <= 0 || f.H <= 0 || f.W > 1 + Slack || f.H > 1 + Slack) return false;
        if (f.X < 0 || f.Y < 0) return false;
        return f.X + f.W <= 1 + Slack && f.Y + f.H <= 1 + Slack;
    }

    /// <summary>Writes the layout (temp file, then a replace). False (and nothing written) when invalid or on an I/O error.</summary>
    public static bool Save(string localAppData, PanelFractions f)
    {
        if (!IsValid(f)) return false;
        try
        {
            string path = PathFor(localAppData);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartObject();
                w.WriteNumber("x", f.X);
                w.WriteNumber("y", f.Y);
                w.WriteNumber("w", f.W);
                w.WriteNumber("h", f.H);
                w.WriteEndObject();
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Num(JsonElement r, string name, out double v)
    {
        v = 0;
        return r.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out v);
    }
}
