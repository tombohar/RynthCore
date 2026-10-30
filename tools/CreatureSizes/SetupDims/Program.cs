using System.Globalization;
using System.Text;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;

// args: <dat dir> <setups.json> <out.json>
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
string datDir = args[0];
string[] ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(
    "[" + string.Join(",", System.Text.Json.JsonSerializer.Deserialize<uint[]>(File.ReadAllText(args[1]))!.Select(u => "\"" + u + "\"")) + "]")!;
DatManager.Initialize(datDir, keepOpen: true, loadCell: false);
var sb = new StringBuilder("{");
int ok = 0, bad = 0; bool first = true;
foreach (string s in ids)
{
    uint id = uint.Parse(s);
    SetupModel m = DatManager.PortalDat.ReadFromDat<SetupModel>(id);
    if (m == null) { bad++; continue; }
    if (!first) sb.Append(',');
    first = false;
    string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    sb.Append($"\"{id}\":[{F(m.Height)},{F(m.Radius)},{F(m.SelectionSphere.Origin.Z)},{F(m.SelectionSphere.Radius)},{F(m.SortingSphere.Origin.Z)},{F(m.SortingSphere.Radius)}]");
    ok++;
}
sb.Append('}');
File.WriteAllText(args[2], sb.ToString());
Console.WriteLine($"read {ok} setups, {bad} missing");
