using RynthCore.Engine.ImGuiBackend.Panels;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine.UI
{
    /// <summary>Stub of the engine's background writer: runs the write at once.</summary>
    internal static class UiBackgroundWriter
    {
        public static void Enqueue(string label, Action work) => work();
    }
}

namespace RadarSettingsTests
{
    // Offline tests for the radar's show boxes. See the csproj.
    internal static class Program
    {
        private static int _fails, _asserts;
        private static void Check(bool cond, string msg)
        {
            _asserts++;
            if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
        }

        private static readonly string[] FileKeys =
        {
            "showMonsters", "showNpcs", "showPortals", "showDoors", "showPlayers", "showFellows",
            "showPets", "showVendors", "showCorpses", "showOwnCorpses", "showLifestones", "showGroundItems",
        };

        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "rynth-radar-settings-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Run("KindNumbersMatchThePlugin", KindNumbersMatchThePlugin);
                Run("EachKindHasItsOwnBox", EachKindHasItsOwnBox);
                Run("CorpsesOffHidesYourCorpses", CorpsesOffHidesYourCorpses);
                Run("UnknownKindsHidden", UnknownKindsHidden);
                Run("SnapshotFiltersPerBox", SnapshotFiltersPerBox);
                Run("FileRoundTrip", () => FileRoundTrip(root));
                Run("NameAndSizeDefaults", NameAndSizeDefaults);
                Run("NameAndSizeFileRoundTrip", () => NameAndSizeFileRoundTrip(root));
                Run("StyleKeysClampAndIgnore", StyleKeysClampAndIgnore);
                Run("WorldMapBackdropSettings", () => WorldMapBackdropSettings(root));
                Run("RoundRadarAndClipping", () => RoundRadarAndClipping(root));
                Run("ClickThroughWheelZoom", () => ClickThroughWheelZoom(root));
            }
            finally
            {
                Environment.SetEnvironmentVariable("RYNTHCORE_RADAR_SETTINGS", null);
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine(_fails == 0 && _asserts > 0 ? $"PASS ({_asserts} checks)" : $"FAIL ({_fails} of {_asserts} checks)");
            return _fails == 0 && _asserts > 0 ? 0 : 1;
        }

        private static void Run(string name, Action body)
        {
            Console.WriteLine(name);
            SetAll(true);
            body();
        }

        private static void SetAll(bool on)
        {
            for (byte k = 0; k < RadarKind.Count; k++) RadarSettingsStore.ShowFlag(k) = on;
        }

        // RynthAi's RynthRadarUi (KindMonster = 0 ... KindGroundItem = 11) sends these numbers;
        // the legend's labels are bound to them.
        private static void KindNumbersMatchThePlugin()
        {
            Check(RadarKind.Monster == 0 && RadarKind.Npc == 1 && RadarKind.Portal == 2 && RadarKind.Door == 3, "kinds 0-3");
            Check(RadarKind.Player == 4 && RadarKind.Fellow == 5 && RadarKind.Pet == 6 && RadarKind.Vendor == 7, "kinds 4-7");
            Check(RadarKind.Corpse == 8 && RadarKind.OwnCorpse == 9 && RadarKind.Lifestone == 10 && RadarKind.GroundItem == 11, "kinds 8-11");
            Check(RadarKind.Count == 12, "12 kinds");
        }

        // Turning one box off hides that kind and nothing else (your corpses aside: they sit under Corpses).
        private static void EachKindHasItsOwnBox()
        {
            for (byte off = 0; off < RadarKind.Count; off++)
            {
                SetAll(true);
                RadarSettingsStore.ShowFlag(off) = false;
                for (byte k = 0; k < RadarKind.Count; k++)
                {
                    bool expected = k != off && !(off == RadarKind.Corpse && k == RadarKind.OwnCorpse);
                    Check(RadarSettingsStore.ShowKind(k) == expected,
                        $"{RadarKind.Name(off)} off: {RadarKind.Name(k)} shown={RadarSettingsStore.ShowKind(k)}, expected {expected}");
                }
                // and back on shows it again
                RadarSettingsStore.ShowFlag(off) = true;
                Check(RadarSettingsStore.ShowKind(off), $"{RadarKind.Name(off)} back on");
            }
        }

        private static void CorpsesOffHidesYourCorpses()
        {
            RadarSettingsStore.ShowCorpses = false;
            Check(!RadarSettingsStore.ShowKind(RadarKind.Corpse), "Corpses off hides corpses");
            Check(!RadarSettingsStore.ShowKind(RadarKind.OwnCorpse), "Corpses off hides your corpses");
            RadarSettingsStore.ShowCorpses = true;
            RadarSettingsStore.ShowOwnCorpses = false;
            Check(RadarSettingsStore.ShowKind(RadarKind.Corpse), "Your corpses off keeps other corpses");
            Check(!RadarSettingsStore.ShowKind(RadarKind.OwnCorpse), "Your corpses off hides your corpses");
        }

        private static void UnknownKindsHidden()
        {
            foreach (byte k in new byte[] { 12, 13, 100, 255 })
                Check(!RadarSettingsStore.ShowKind(k), $"kind {k} hidden");
        }

        // One marker of each kind, as RynthAi writes the snapshot: the shown ones follow the boxes.
        private static void SnapshotFiltersPerBox()
        {
            var marks = string.Join(",", Enumerable.Range(0, RadarKind.Count)
                .Select(k => $"{{\"kind\":{k},\"x\":{k},\"y\":0,\"z\":0,\"label\":\"m{k}\",\"id\":{1000 + k}}}"));
            RadarSnapshot? snap = RadarSnapshot.Parse("{\"mapVersion\":1,\"markers\":[" + marks + "]}");
            Check(snap != null && snap.Markers.Count == RadarKind.Count, "snapshot parsed");
            if (snap == null) return;
            for (int k = 0; k < RadarKind.Count; k++)
                Check(snap.Markers[k].Kind == k && snap.Markers[k].Id == 1000 + k, $"marker {k} kind/id");

            RadarSettingsStore.ShowMonsters = false;
            RadarSettingsStore.ShowPlayers = false;
            RadarSettingsStore.ShowDoors = false;
            var shown = snap.Markers.Where(m => RadarSettingsStore.ShowKind(m.Kind)).Select(m => m.Kind).ToHashSet();
            Check(!shown.Contains(RadarKind.Monster) && !shown.Contains(RadarKind.Player) && !shown.Contains(RadarKind.Door),
                "monsters, players and doors filtered out");
            Check(shown.Contains(RadarKind.Fellow), "fellows are their own box: still shown with Players off");
            Check(shown.Count == RadarKind.Count - 3, $"the other kinds still shown ({shown.Count})");
        }

        // Every box loads from its key and saves back under it.
        private static void FileRoundTrip(string root)
        {
            string seed = Path.Combine(root, "seed.txt");
            var lines = new List<string> { "# test" };
            for (int k = 0; k < RadarKind.Count; k++)
                lines.Add($"{FileKeys[k]}={(k % 2 == 0 ? 0 : 1)}");   // even kinds off
            File.WriteAllLines(seed, lines);
            Environment.SetEnvironmentVariable("RYNTHCORE_RADAR_SETTINGS", seed);
            RadarSettingsStore.Load();
            for (byte k = 0; k < RadarKind.Count; k++)
                Check(RadarSettingsStore.ShowFlag(k) == (k % 2 == 1), $"loaded {FileKeys[k]}");

            string saved = Path.Combine(root, "saved.txt");
            Environment.SetEnvironmentVariable("RYNTHCORE_RADAR_SETTINGS", saved);
            SetAll(false);
            RadarSettingsStore.ShowFlag(RadarKind.Fellow) = true;
            RadarSettingsStore.Save();
            string text = File.Exists(saved) ? File.ReadAllText(saved) : "";
            for (int k = 0; k < RadarKind.Count; k++)
            {
                string want = $"{FileKeys[k]}={(k == RadarKind.Fellow ? 1 : 0)}";
                Check(text.Contains(want + "\n") || text.Contains(want + "\r\n"), $"saved {want}");
            }
        }
        // Names start on for the kinds you look for by name; sizes at 1x; nothing is written for defaults.
        private static void NameAndSizeDefaults()
        {
            RadarSettingsStore.ResetSizes();
            MarkerStyleSettings st = RadarSettingsStore.Markers;
            byte[] named = { RadarKind.Monster, RadarKind.Npc, RadarKind.Portal, RadarKind.Player, RadarKind.Fellow, RadarKind.Vendor };
            for (byte k = 0; k < RadarKind.Count; k++)
            {
                Check(st.NameShown(k) == named.Contains(k), $"{RadarKind.Name(k)} name default {st.NameShown(k)}");
                Check(st.SizeOf(k) == 1f, $"{RadarKind.Name(k)} size default");
                Check(st.NameSizeOf(k) == MarkerStyleSettings.DefaultName, $"{RadarKind.Name(k)} name size default");
            }
            Check(st.SelfSize == 1f, "your arrow default size");
            Check(RadarSettingsStore.CompassSize == 14f && RadarSettingsStore.CoordSize == 11f, "compass 14 px, coordinates 11 px");
            var lines = new List<string>();
            st.AppendLines(lines);
            Check(lines.Count == 0, $"defaults write no style lines ({lines.Count})");
            // A kind this engine doesn't know: 1x, no name.
            Check(st.SizeOf(200) == 1f && !st.NameShown(200), "unknown kind: 1x, no name");
        }

        // Every size, name box and the compass/coordinate sizes save under their key and load back.
        private static void NameAndSizeFileRoundTrip(string root)
        {
            string saved = Path.Combine(root, "styles.txt");
            Environment.SetEnvironmentVariable("RYNTHCORE_RADAR_SETTINGS", saved);
            RadarSettingsStore.ResetSizes();
            MarkerStyleSettings st = RadarSettingsStore.Markers;
            st.Size[RadarKind.Portal] = 2.5f;
            st.ShowName[RadarKind.Monster] = false;
            st.ShowName[RadarKind.Corpse] = true;
            st.NameSize[RadarKind.Player] = 16f;
            st.SelfSize = 1.5f;
            RadarSettingsStore.CompassSize = 20f;
            RadarSettingsStore.CoordSize = 9f;
            RadarSettingsStore.Save();
            string text = File.ReadAllText(saved).Replace("\r\n", "\n");
            foreach (string want in new[] { "size.portal=2.5", "name.monster=0", "name.corpse=1", "nameSize.player=16",
                         "size.self=1.5", "compassSize=20", "coordSize=9" })
                Check(text.Contains(want + "\n"), $"saved {want}");
            Check(!text.Contains("size.monster") && !text.Contains("name.npc"), "defaults are not written");

            // Load into a fresh settings object, as the store's own Load does line by line.
            var again = new MarkerStyleSettings(new bool[RadarKind.Count]);
            foreach (string line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) again.TryLoad(line[..eq], line[(eq + 1)..]);
            }
            Check(again.Size[RadarKind.Portal] == 2.5f, "portal size loaded");
            Check(again.ShowName[RadarKind.Corpse], "corpse name loaded");
            Check(!again.ShowName[RadarKind.Monster], "monster name off loads");
            Check(again.NameSize[RadarKind.Player] == 16f, "player name size loaded");
            Check(again.SelfSize == 1.5f, "your arrow size loaded");
        }

        // Out-of-range values are pulled in, junk is ignored, other keys are left alone.
        private static void StyleKeysClampAndIgnore()
        {
            var st = new MarkerStyleSettings(new bool[RadarKind.Count]);
            Check(st.TryLoad("size.door", "99") && st.Size[RadarKind.Door] == MarkerStyleSettings.MaxSize, "huge size clamped");
            Check(st.TryLoad("size.door", "0.01") && st.Size[RadarKind.Door] == MarkerStyleSettings.MinSize, "tiny size clamped");
            Check(st.TryLoad("nameSize.npc", "500") && st.NameSize[RadarKind.Npc] == MarkerStyleSettings.MaxName, "huge name size clamped");
            Check(st.TryLoad("nameSize.npc", "1") && st.NameSize[RadarKind.Npc] == MarkerStyleSettings.MinName, "tiny name size clamped");
            st.Size[RadarKind.Pet] = 2f;
            Check(st.TryLoad("size.pet", "abc") && st.Size[RadarKind.Pet] == 2f, "junk number keeps the value");
            Check(st.TryLoad("size.pet", "NaN") && st.Size[RadarKind.Pet] == 2f, "NaN keeps the value");
            Check(!st.TryLoad("size.nonsense", "2"), "unknown kind key is not ours");
            Check(!st.TryLoad("color.monster", "FFFF0000"), "colour keys are not ours");
            Check(!st.TryLoad("zoom", "2"), "plain keys are not ours");
        }
        // The world map backdrop: off by default, and its switch and strength save under their keys.
        private static void WorldMapBackdropSettings(string root)
        {
            Check(!RadarSettingsStore.ShowWorldMap, "backdrop off by default");
            Check(RadarSettingsStore.WorldMapOpacity == 0.8f, "backdrop strength default 0.8");
            string saved = Path.Combine(root, "backdrop.txt");
            Environment.SetEnvironmentVariable("RYNTHCORE_RADAR_SETTINGS", saved);
            RadarSettingsStore.ShowWorldMap = true;
            RadarSettingsStore.WorldMapOpacity = 0.35f;
            RadarSettingsStore.Save();
            string text = File.ReadAllText(saved).Replace("\r\n", "\n");
            Check(text.Contains("worldMap=1\n"), "saved worldMap=1");
            Check(text.Contains("worldMapOpacity=0.35\n"), "saved worldMapOpacity=0.35");
            RadarSettingsStore.ShowWorldMap = false;
            RadarSettingsStore.WorldMapOpacity = 0.8f;
            RadarSettingsStore.Save();
            text = File.ReadAllText(saved).Replace("\r\n", "\n");
            Check(text.Contains("worldMap=0\n"), "saved worldMap=0 when off");
        }
        // The round radar: the setting saves, and the geometry cuts shapes to the circle.
        // Docked click-through with "Mouse wheel still zooms" (2026-10-29): the wheel over the radar is
        // the radar's, everywhere else and in every other case it stays the game's.
        private static void ClickThroughWheelZoom(string root)
        {
            Check(!RadarSettingsStore.ClickThroughWheelZoom, "off by default");
            string saved = Path.Combine(root, "wheel.txt");
            Environment.SetEnvironmentVariable("RYNTHCORE_RADAR_SETTINGS", saved);
            RadarSettingsStore.ClickThroughWheelZoom = true;
            RadarSettingsStore.Save();
            Check(File.ReadAllText(saved).Replace("\r\n", "\n").Contains("clickThroughWheel=1\n"), "saved clickThroughWheel=1");
            RadarSettingsStore.ClickThroughWheelZoom = false;
            RadarSettingsStore.Save();
            Check(File.ReadAllText(saved).Replace("\r\n", "\n").Contains("clickThroughWheel=0\n"), "saved clickThroughWheel=0");
            // (The store loads once per process, so a file from before - no clickThroughWheel line - is
            // covered by the default above: Load only sets the switch from its own line.)

            var min = new System.Numerics.Vector2(100, 100);
            var max = new System.Numerics.Vector2(300, 300);
            var mid = new System.Numerics.Vector2(200, 200);
            Check(RadarWheel.Owns(true, true, false, false, mid, min, max, null), "on, click-through, mouse on the radar: the radar's");
            Check(!RadarWheel.Owns(false, true, false, false, mid, min, max, null), "option off: the game's");
            Check(!RadarWheel.Owns(true, false, false, false, mid, min, max, null), "not click-through this frame (Ctrl, a dot): the window has the wheel itself");
            Check(!RadarWheel.Owns(true, true, true, false, mid, min, max, null), "popped out: always interactive");
            Check(!RadarWheel.Owns(true, true, false, true, mid, min, max, null), "another RynthCore window has the mouse");
            Check(!RadarWheel.Owns(true, true, false, false, new System.Numerics.Vector2(99, 200), min, max, null), "just left of the radar");
            Check(!RadarWheel.Owns(true, true, false, false, new System.Numerics.Vector2(200, 301), min, max, null), "just under it");
            Check(RadarWheel.Owns(true, true, false, false, max, min, max, null), "its corner counts (square)");
            var circle = new RoundMask(mid, 100);
            Check(!RadarWheel.Owns(true, true, false, false, new System.Numerics.Vector2(105, 105), min, max, circle), "round: the box's corner is outside the circle");
            Check(RadarWheel.Owns(true, true, false, false, new System.Numerics.Vector2(200, 105), min, max, circle), "round: inside the circle");

            Check(Math.Abs(RadarWheel.Zoomed(1.5f, 1f) - 1.65f) < 0.0001f, "one notch up: +0.15");
            Check(Math.Abs(RadarWheel.Zoomed(1.5f, -2f) - 1.2f) < 0.0001f, "two down: -0.30");
            Check(RadarWheel.Zoomed(5.95f, 3f) == RadarWheel.MaxZoom && RadarWheel.Zoomed(0.6f, -3f) == RadarWheel.MinZoom, "kept to 0.5..6 like the slider");
        }

        private static void RoundRadarAndClipping(string root)
        {
            Check(!RadarSettingsStore.RoundRadar, "round off by default");
            string saved = Path.Combine(root, "round.txt");
            Environment.SetEnvironmentVariable("RYNTHCORE_RADAR_SETTINGS", saved);
            RadarSettingsStore.RoundRadar = true;
            RadarSettingsStore.Save();
            Check(File.ReadAllText(saved).Replace("\r\n", "\n").Contains("round=1\n"), "saved round=1");
            RadarSettingsStore.RoundRadar = false;

            var m = new RoundMask(new System.Numerics.Vector2(100, 100), 50);
            Check(m.Contains(new System.Numerics.Vector2(100, 149)) && !m.Contains(new System.Numerics.Vector2(100, 151)), "Contains: the edge");
            Check(m.ContainsBox(new System.Numerics.Vector2(80, 80), new System.Numerics.Vector2(120, 120)), "a small box in the middle is inside");
            Check(!m.ContainsBox(new System.Numerics.Vector2(60, 60), new System.Numerics.Vector2(140, 140)), "a box with its corners out is not inside");
            Check(m.OverlapsBox(new System.Numerics.Vector2(60, 60), new System.Numerics.Vector2(140, 140)), "...but it overlaps");
            Check(!m.OverlapsBox(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(40, 40)), "a far corner box doesn't overlap");

            // Segments: inside stays, outside goes, across is cut at the circle.
            var a = new System.Numerics.Vector2(90, 100); var b = new System.Numerics.Vector2(110, 100);
            Check(CircleClip.Segment(m, ref a, ref b) && a.X == 90 && b.X == 110, "a segment inside is unchanged");
            a = new System.Numerics.Vector2(0, 0); b = new System.Numerics.Vector2(20, 20);
            Check(!CircleClip.Segment(m, ref a, ref b), "a segment outside is dropped");
            a = new System.Numerics.Vector2(0, 100); b = new System.Numerics.Vector2(200, 100);
            Check(CircleClip.Segment(m, ref a, ref b) && Math.Abs(a.X - 50) < 0.01f && Math.Abs(b.X - 150) < 0.01f, $"a segment across is cut to x 50..150 ({a.X}..{b.X})");

            // Polygons: inside unchanged in area, outside dropped, a big square cut to about the circle's area.
            var outP = new System.Numerics.Vector2[CircleClip.MaxPoints];
            var outUv = new System.Numerics.Vector2[CircleClip.MaxPoints];
            var small = new[] { new System.Numerics.Vector2(90, 90), new System.Numerics.Vector2(110, 90), new System.Numerics.Vector2(110, 110), new System.Numerics.Vector2(90, 110) };
            int n = CircleClip.Polygon(m, small, default, outP, default);
            Check(n == 4 && Math.Abs(Area(outP, n) - 400) < 0.01f, $"a small square inside keeps its area ({n} points, {Area(outP, n)})");
            var far = new[] { new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(10, 0), new System.Numerics.Vector2(10, 10), new System.Numerics.Vector2(0, 10) };
            Check(CircleClip.Polygon(m, far, default, outP, default) == 0, "a square outside is dropped");
            var big = new[] { new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(200, 0), new System.Numerics.Vector2(200, 200), new System.Numerics.Vector2(0, 200) };
            var uvs = new[] { new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(1, 0), new System.Numerics.Vector2(1, 1), new System.Numerics.Vector2(0, 1) };
            n = CircleClip.Polygon(m, big, uvs, outP, outUv);
            float circle = MathF.PI * 50 * 50, area = Area(outP, n);
            Check(n >= 40 && area > circle * 0.99f && area <= circle, $"a square round the circle is cut to the circle's area ({area} of {circle}, {n} points)");
            bool onCircle = true, uvOk = true;
            for (int i = 0; i < n; i++)
            {
                float d = (outP[i] - m.Centre).Length();
                if (d > 50.01f) onCircle = false;
                // the uv follows the position: x/200, y/200
                if (Math.Abs(outUv[i].X - outP[i].X / 200f) > 0.001f || Math.Abs(outUv[i].Y - outP[i].Y / 200f) > 0.001f) uvOk = false;
            }
            Check(onCircle, "every cut point is within the circle");
            Check(uvOk, "texture coordinates follow the cut points");
            // Half in: a rect from x 100 to 200 keeps the half circle.
            var half = new[] { new System.Numerics.Vector2(100, 0), new System.Numerics.Vector2(200, 0), new System.Numerics.Vector2(200, 200), new System.Numerics.Vector2(100, 200) };
            n = CircleClip.Polygon(m, half, default, outP, default);
            Check(n >= 3 && Math.Abs(Area(outP, n) - circle / 2) < circle * 0.01f, $"half the circle ({Area(outP, n)} of {circle / 2})");
        }

        private static float Area(System.Numerics.Vector2[] p, int n)
        {
            float a = 0;
            for (int i = 0; i < n; i++) { var u = p[i]; var v = p[(i + 1) % n]; a += u.X * v.Y - v.X * u.Y; }
            return Math.Abs(a) * 0.5f;
        }
    }
}
