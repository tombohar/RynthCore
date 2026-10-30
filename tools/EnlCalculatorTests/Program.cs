using System.Text;
using RynthCore.Engine.UI.Data;

namespace EnlCalculatorTests;

// Offline tests for the Enl calculator's data. See the csproj.
internal static class Program
{
    private static int _fails, _asserts;
    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }
    private static void Eq<T>(T actual, T expected, string msg) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{msg}: expected '{expected}', got '{actual}'");

    private static int Main()
    {
        FullSample();
        OlderIltWithoutCalculator();
        NoEnlObjectAndBadPayloads();
        NullAndMistypedContainers();
        PartialAndMalformedChainRows();
        PartialAndMalformedLevelAndMaterialRows();
        UnknownFieldsAreIgnored();
        RowCap();
        ViewLimits();
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? $"PASS ({_asserts} checks)" : $"FAIL ({_fails} of {_asserts} checks failed)");
        return _fails == 0 ? 0 : 1;
    }

    // What RynthILT's AppendEnlCalculatorJson emits (IltProgression.cs, RynthSuite integration/pr-fixes-2026-10-18),
    // inside AppendEnlJson's "enl" object, for a character at enlightenment 298 planning to 303:
    // levels 299-303 (EnlStepCost: medallions to 300, sigils from 301, Compact lum), materials from the totals,
    // the 8 chain rows (only when sigils are needed), chainNote, ecoins, ecoinsLum. Property order as emitted.
    private const string Sample = """
    {"available":true,"world":"InfiniteLeaftide",
     "aug":{"off":false,"status":"loaded 2026-10-18 09:12","lumPerCoin":0,"rows":[],"total":"","short":"","banked":"Banked luminance: 650.00B"},
     "enl":{"off":false,"header":"Enlightenment 298   Level 275   Unassigned XP 1.20T",
       "next":"Next (299): 294 Enlightenment Medallions (have 300) + 299.00B lum","enl":298,"target":303,
       "plan":["589 Enlightenment Medallions","891 Enlightenment Sigils","2.41T luminance (banked 650.00B)"],
       "coinsPerToken":0,"tokensShort":"",
       "levels":[
         {"t":299,"item":"Medallions","count":294,"lum":"299.00B","ok":true},
         {"t":300,"item":"Medallions","count":295,"lum":"300.00B","ok":false},
         {"t":301,"item":"Sigils","count":296,"lum":"602.00B","ok":false},
         {"t":302,"item":"Sigils","count":297,"lum":"604.00B","ok":false},
         {"t":303,"item":"Sigils","count":298,"lum":"606.00B","ok":false}],
       "materials":[
         {"label":"Luminance","need":"1.76T","have":"650.00B","ok":false},
         {"label":"Medallions","need":"289","have":"300","ok":false},
         {"label":"Sigils","need":"891","have":"0","ok":false}],
       "chain":[
         {"label":"Ascension Coins","need":891,"have":40,"ok":false,"craft":true},
         {"label":"Lifeblood Elixirs","need":891,"have":0,"ok":false,"craft":true},
         {"label":"Lifebloom Resins","need":1782,"have":12,"ok":false,"craft":true},
         {"label":"E-coins","need":1234567,"have":1500,"ok":false,"craft":false},
         {"label":"Ancient Yalani Trinkets","need":0,"have":3,"ok":true,"craft":false},
         {"label":"Necrolytic Extracts","need":891,"have":0,"ok":false,"craft":false},
         {"label":"Life Splitters","need":0,"have":0,"ok":true,"craft":false},
         {"label":"Plants","need":1782,"have":9,"ok":false,"craft":false}],
       "chainNote":"Sigil = Lifeblood Elixir + Ascension Coin. 891 to craft; Need = crafts left (in-between items) or mats to get (raw items).",
       "ecoins":"E-coins needed: 1,234,567   have 1,500 (bank + pack)   Ascension Coins use 851",
       "ecoinsLum":"Lum to buy e-coins: ~12.35T",
       "blockers":["Need 2.41T banked luminance."],
       "serverChecks":"Server also checks: not in a dungeon, free pack slots (40 free), all luminance augs (past 10), society master (past 30).",
       "auto":false,"armed":false,"spendFirst":true,"checkEvery":60,"status":"idle"}}
    """;

    /// <summary>The sample with the enl object's body replaced (everything else of the payload stays).</summary>
    private static string WithEnl(string enlBody) =>
        "{\"available\":true,\"world\":\"W\",\"aug\":{\"off\":false,\"status\":\"ok\",\"banked\":\"B\"},\"enl\":{" + enlBody + "}}";

    private static EnlPlanner Enl(string enlBody) => ProgressionSource.ParseJson(WithEnl(enlBody)).Enl;

    // ── The full payload ────────────────────────────────────────────────

    private static void FullSample()
    {
        ProgressionInfo info = ProgressionSource.ParseJson(Sample);
        Check(info.Available, "full: available");
        Eq(info.World, "InfiniteLeaftide", "full: world");
        Eq(info.Aug.Banked, "Banked luminance: 650.00B", "full: the Augmentation planner still parses");
        EnlPlanner e = info.Enl;
        Eq(e.Level, 298, "full: enl level"); Eq(e.Target, 303, "full: target");
        Eq(e.Plan.Length, 3, "full: plan lines"); Eq(e.Blockers.Length, 1, "full: blockers");
        Eq(e.CheckEvery, 60, "full: checkEvery"); Check(e.SpendFirst, "full: spendFirst"); Eq(e.Status, "idle", "full: status");
        Check(e.HasCalculator, "full: HasCalculator");

        Eq(e.Levels.Length, 5, "full: level rows");
        Eq(e.Levels[0].Level, 299, "level 0: t"); Eq(e.Levels[0].Item, "Medallions", "level 0: item");
        Eq(e.Levels[0].Count, 294, "level 0: count"); Eq(e.Levels[0].Lum, "299.00B", "level 0: lum"); Check(e.Levels[0].Ok, "level 0: ok");
        Check(!e.Levels[1].Ok, "level 1: not ok"); Eq(e.Levels[2].Item, "Sigils", "level 2: item"); Eq(e.Levels[4].Level, 303, "level 4: t");
        Eq(e.Levels[0].LevelText, "299", "level 0: LevelText"); Eq(e.Levels[2].CountText, "296", "level 2: CountText");
        Check(ReferenceEquals(e.Levels[0].LevelText, e.Levels[0].LevelText), "level text is formatted once");

        Eq(e.Materials.Length, 3, "full: material rows");
        Eq(e.Materials[0].Label, "Luminance", "material 0: label"); Eq(e.Materials[0].Need, "1.76T", "material 0: need");
        Eq(e.Materials[0].Have, "650.00B", "material 0: have"); Check(!e.Materials[0].Ok, "material 0: not ok");
        Eq(e.Materials[2].Label, "Sigils", "material 2: label"); Eq(e.Materials[2].Have, "0", "material 2: have");

        Eq(e.Chain.Length, 8, "full: chain rows");
        Eq(e.Chain[0].Label, "Ascension Coins", "chain 0: label"); Eq(e.Chain[0].Need, 891L, "chain 0: need");
        Eq(e.Chain[0].Have, 40L, "chain 0: have"); Check(e.Chain[0].Craft, "chain 0: craft"); Check(!e.Chain[0].Ok, "chain 0: not ok");
        Eq(e.Chain[3].Label, "E-coins", "chain 3: label"); Eq(e.Chain[3].Need, 1234567L, "chain 3: need > a short");
        Check(!e.Chain[3].Craft, "chain 3: not craft"); Eq(e.Chain[3].NeedText, "1,234,567", "chain 3: NeedText"); Eq(e.Chain[3].HaveText, "1,500", "chain 3: HaveText");
        Eq(e.Chain[0].DisplayLabel, "Ascension Coins (craft)", "chain 0: craft rows are tagged"); Eq(e.Chain[3].DisplayLabel, "E-coins", "chain 3: raw rows are not");
        Check(e.Chain[4].Ok && !e.Chain[4].Craft, "chain 4: ok raw row"); Eq(e.Chain[7].Label, "Plants", "chain 7: label");
        Check(e.ChainNote.StartsWith("Sigil = Lifeblood Elixir", StringComparison.Ordinal), "full: chainNote");
        Check(e.Ecoins.Contains("1,234,567", StringComparison.Ordinal), "full: ecoins");
        Eq(e.EcoinsLum, "Lum to buy e-coins: ~12.35T", "full: ecoinsLum");
    }

    // ── Graceful absence ────────────────────────────────────────────────

    private static void OlderIltWithoutCalculator()
    {
        // An ILT from before the calculator: the enl object has the planner fields only.
        EnlPlanner e = Enl("\"off\":false,\"header\":\"H\",\"next\":\"N\",\"enl\":10,\"target\":15,\"plan\":[\"5 Enlightenment Tokens\"],"
                           + "\"coinsPerToken\":0,\"tokensShort\":\"\",\"blockers\":[],\"serverChecks\":\"S\",\"auto\":false,\"armed\":false,"
                           + "\"spendFirst\":false,\"checkEvery\":60,\"status\":\"\"");
        Eq(e.Levels.Length, 0, "older ILT: no levels"); Eq(e.Materials.Length, 0, "older ILT: no materials"); Eq(e.Chain.Length, 0, "older ILT: no chain");
        Eq(e.ChainNote, "", "older ILT: chainNote"); Eq(e.Ecoins, "", "older ILT: ecoins"); Eq(e.EcoinsLum, "", "older ILT: ecoinsLum");
        Check(!e.HasCalculator, "older ILT: no calculator");
        Eq(e.Header, "H", "older ILT: the planner itself still reads"); Eq(e.Plan.Length, 1, "older ILT: plan");

        EnlPlanner empty = Enl("");
        Check(!empty.HasCalculator && empty.Levels.Length == 0 && empty.Chain.Length == 0 && empty.Materials.Length == 0, "empty enl object: nothing");
    }

    private static void NoEnlObjectAndBadPayloads()
    {
        ProgressionInfo noEnl = ProgressionSource.ParseJson("{\"available\":true,\"world\":\"W\"}");
        Check(noEnl.Available && !noEnl.Enl.HasCalculator, "no enl object: default planner, nothing to draw");

        foreach (string bad in new[] { "{}", "", "   ", "not json", "{\"available\":", "[1,2,3]", "null", "{\"enl\":5}", "{\"available\":\"yes\"}" })
        {
            ProgressionInfo info = null!;
            try { info = ProgressionSource.ParseJson(bad); }
            catch (Exception ex) { Check(false, $"bad payload '{bad}' threw {ex.GetType().Name}"); continue; }
            Check(info != null && !info.Available && !info.Enl.HasCalculator, $"bad payload '{bad}': empty snapshot, tab hidden");
        }
        Check(!ProgressionSource.ParseJson(null).Available, "null payload: empty snapshot");
    }

    private static void NullAndMistypedContainers()
    {
        // null / scalar / object where an array belongs: empty array, and the sibling fields still read.
        foreach (string bad in new[] { "null", "\"oops\"", "12", "true", "{\"t\":1}", "{}", "[]" })
        {
            ProgressionInfo info = ProgressionSource.ParseJson(WithEnl(
                $"\"header\":\"H\",\"levels\":{bad},\"materials\":{bad},\"chain\":{bad},\"chainNote\":\"note\",\"enl\":7"));
            Check(info.Available, $"containers = {bad}: snapshot stays available");
            Eq(info.Enl.Levels.Length + info.Enl.Materials.Length + info.Enl.Chain.Length, 0, $"containers = {bad}: empty arrays");
            Eq(info.Enl.Header, "H", $"containers = {bad}: header before"); Eq(info.Enl.ChainNote, "note", $"containers = {bad}: chainNote after");
            Eq(info.Enl.Level, 7, $"containers = {bad}: enl after"); Eq(info.Aug.Status, "ok", $"containers = {bad}: Augmentation planner untouched");
        }

        // JSON null into a string: the UI reads .Length, so ParseJson turns it into "".
        EnlPlanner e = Enl("\"chainNote\":null,\"ecoins\":null,\"ecoinsLum\":null,\"levels\":[{\"t\":1,\"ok\":true}]");
        Eq(e.ChainNote, "", "null chainNote"); Eq(e.Ecoins, "", "null ecoins"); Eq(e.EcoinsLum, "", "null ecoinsLum");
        Eq(e.Levels.Length, 1, "null strings: rows still read");
    }

    // ── Partial and malformed rows ──────────────────────────────────────

    private static void PartialAndMalformedChainRows()
    {
        EnlPlanner e = Enl("\"header\":\"H\",\"chain\":["
            + "{\"label\":\"Only label\"},"                                          // 0: partial
            + "{},"                                                                  // 1: empty object
            + "{\"label\":\"Str nums\",\"need\":\"12\",\"have\":\"1,000\",\"ok\":true},"    // 2: numbers as strings
            + "{\"label\":\"Bad nums\",\"need\":\"abc\",\"have\":{\"x\":[1,2]},\"ok\":\"yes\",\"craft\":1},"   // 3: wrong types
            + "42,\"text\",null,[1,2],true,"                                         // not objects: skipped
            + "{\"label\":null,\"need\":null,\"have\":1.0e3,\"ok\":null,\"craft\":true},"   // 7: nulls, a float
            + "{\"label\":\"Huge\",\"need\":1e30,\"have\":-1e30},"                   // 8: out of range
            + "{\"label\":\"Last\",\"need\":5,\"have\":2,\"ok\":false,\"craft\":true}"   // 9: still read after all that
            + "],\"chainNote\":\"after\",\"enl\":3");
        Eq(e.Header, "H", "chain: header before"); Eq(e.ChainNote, "after", "chain: chainNote after the rows"); Eq(e.Level, 3, "chain: enl after the rows");
        Eq(e.Chain.Length, 7, "chain: 7 object rows kept, 5 non-objects skipped");
        EnlChainRow r = e.Chain[0];
        Eq(r.Label, "Only label", "partial: label"); Eq(r.Need, 0L, "partial: need default"); Eq(r.Have, 0L, "partial: have default");
        Check(!r.Ok && !r.Craft, "partial: flags default false");
        r = e.Chain[1];
        Eq(r.Label, "", "empty row: label"); Eq(r.NeedText, "0", "empty row: need text");
        r = e.Chain[2];
        Eq(r.Need, 12L, "string number: need"); Eq(r.Have, 1000L, "string number with a separator: have"); Check(r.Ok, "string number row: ok");
        r = e.Chain[3];
        Eq(r.Label, "Bad nums", "wrong types: label"); Eq(r.Need, 0L, "wrong types: need"); Eq(r.Have, 0L, "wrong types: have");
        Check(!r.Ok && !r.Craft, "wrong types: flags default");
        r = e.Chain[4];
        Eq(r.Label, "", "nulls: label"); Eq(r.Need, 0L, "nulls: need"); Eq(r.Have, 1000L, "float have"); Check(r.Craft, "nulls: craft still read");
        r = e.Chain[5];
        Eq(r.Label, "Huge", "range: label"); Eq(r.Need, long.MaxValue, "range: need clamps"); Eq(r.Have, long.MinValue, "range: have clamps");
        r = e.Chain[6];
        Eq(r.Label, "Last", "last row: label"); Eq(r.Need, 5L, "last row: need"); Eq(r.Have, 2L, "last row: have");
        Check(!r.Ok && r.Craft, "last row: flags");
    }

    private static void PartialAndMalformedLevelAndMaterialRows()
    {
        EnlPlanner e = Enl("\"levels\":[{\"t\":300},{\"t\":\"301\",\"item\":5,\"count\":2.0,\"lum\":null,\"ok\":\"true\"},"
            + "{\"t\":302,\"item\":\"Sigils\",\"count\":99999999999,\"lum\":\"1.00B\",\"ok\":true},{\"item\":{\"deep\":[1]},\"lum\":[]},3,null],"
            + "\"materials\":[{\"label\":\"Tokens\"},{\"label\":\"Crests\",\"need\":7,\"have\":null,\"ok\":true},\"x\",{\"need\":{}}],\"enl\":1");
        Eq(e.Level, 1, "rows: enl after the arrays");
        Eq(e.Levels.Length, 4, "levels: 4 object rows");
        Eq(e.Levels[0].Level, 300, "level partial: t"); Eq(e.Levels[0].Item, "-", "level partial: item default"); Eq(e.Levels[0].Lum, "-", "level partial: lum default");
        Eq(e.Levels[0].CountText, "-", "level partial: no count shows '-'"); Check(!e.Levels[0].Ok, "level partial: not ok");
        Eq(e.Levels[1].Level, 301, "level strings: t"); Eq(e.Levels[1].Item, "5", "level number item reads as text"); Eq(e.Levels[1].Count, 2, "level float count");
        Eq(e.Levels[1].Lum, "-", "level null lum keeps '-'"); Check(!e.Levels[1].Ok, "level string 'true' is not a bool");
        Eq(e.Levels[2].Count, int.MaxValue, "level huge count clamps"); Check(e.Levels[2].Ok, "level ok");
        Eq(e.Levels[3].Level, 0, "level without t"); Eq(e.Levels[3].Item, "-", "level with object item"); Eq(e.Levels[3].Lum, "-", "level with array lum");
        Eq(e.Materials.Length, 3, "materials: 3 object rows");
        Eq(e.Materials[0].Label, "Tokens", "material partial: label"); Eq(e.Materials[0].Need, "0", "material partial: need"); Eq(e.Materials[0].Have, "0", "material partial: have");
        Eq(e.Materials[1].Need, "7", "material numeric need reads as text"); Eq(e.Materials[1].Have, "0", "material null have"); Check(e.Materials[1].Ok, "material ok");
        Eq(e.Materials[2].Label, "", "material object need: label empty"); Eq(e.Materials[2].Need, "0", "material object need: default");
        Check(e.HasCalculator, "partial rows still count as calculator data");
    }

    private static void UnknownFieldsAreIgnored()
    {
        ProgressionInfo info = ProgressionSource.ParseJson("""
        {"future":{"a":[1,{"b":2}]},"available":true,"world":"W","aug":{"off":false,"status":"s","newAugField":[1,2,3]},
         "enl":{"header":"H","newTop":{"x":1},"levels":[{"extra":[{"deep":true}],"t":9,"item":"Crests","count":1,"lum":"1.32T","ok":true,"more":{"k":"v"}}],
          "materials":[{"label":"Crests","tier":3,"need":"4","have":"5","ok":true,"hint":null}],
          "chain":[{"label":"Plants","note":"x","need":3,"have":1,"ok":false,"craft":false,"icon":{"wcid":1}}],
          "chainNote":"n","ecoins":"e","ecoinsLum":"l","zzz":[[]],"enl":300,"target":310},"tail":"yes"}
        """);
        Check(info.Available, "unknown: available"); Eq(info.Aug.Status, "s", "unknown: aug");
        EnlPlanner e = info.Enl;
        Eq(e.Header, "H", "unknown: header"); Eq(e.Level, 300, "unknown: enl after unknown fields"); Eq(e.Target, 310, "unknown: target");
        Eq(e.Levels.Length, 1, "unknown: levels"); Eq(e.Levels[0].Level, 9, "unknown: level t after an unknown array"); Eq(e.Levels[0].Item, "Crests", "unknown: level item");
        Eq(e.Levels[0].Lum, "1.32T", "unknown: level lum"); Check(e.Levels[0].Ok, "unknown: level ok (after an unknown object)");
        Eq(e.Materials.Length, 1, "unknown: materials"); Eq(e.Materials[0].Need, "4", "unknown: material need"); Eq(e.Materials[0].Have, "5", "unknown: material have");
        Eq(e.Chain.Length, 1, "unknown: chain"); Eq(e.Chain[0].Label, "Plants", "unknown: chain label"); Eq(e.Chain[0].Need, 3L, "unknown: chain need"); Eq(e.Chain[0].Have, 1L, "unknown: chain have");
        Eq(e.ChainNote, "n", "unknown: chainNote"); Eq(e.Ecoins, "e", "unknown: ecoins"); Eq(e.EcoinsLum, "l", "unknown: ecoinsLum");

        // Case matters (the producer's names are exact): a differently cased name is an unknown field.
        EnlPlanner cased = Enl("\"levels\":[{\"T\":5,\"Item\":\"X\",\"t\":6}]");
        Eq(cased.Levels[0].Level, 6, "case: only the exact name counts"); Eq(cased.Levels[0].Item, "-", "case: Item is unknown");
    }

    private static void RowCap()
    {
        var sb = new StringBuilder("\"levels\":[");
        for (int i = 0; i < 250; i++) sb.Append(i > 0 ? "," : "").Append("{\"t\":").Append(i + 1).Append(",\"item\":\"Tokens\",\"count\":1,\"lum\":\"-\",\"ok\":true}");
        sb.Append("],\"enl\":4");
        EnlPlanner e = Enl(sb.ToString());
        Eq(e.Levels.Length, 200, "250 rows: the parse keeps the first 200");
        Eq(e.Levels[0].Level, 1, "cap: first row"); Eq(e.Levels[199].Level, 200, "cap: last kept row"); Eq(e.Level, 4, "cap: field after the array");

        // The producer's own range, 100 level rows, comes through whole.
        var hundred = new StringBuilder("\"levels\":[");
        for (int i = 0; i < 100; i++) hundred.Append(i > 0 ? "," : "").Append("{\"t\":").Append(i + 299).Append('}');
        hundred.Append(']');
        Eq(Enl(hundred.ToString()).Levels.Length, 100, "100 rows (the producer's cap) all kept");
    }

    private static void ViewLimits()
    {
        Eq(EnlCalculatorView.VisibleLevels(0, false), 0, "view: none"); Eq(EnlCalculatorView.VisibleLevels(-3, true), 0, "view: negative");
        Eq(EnlCalculatorView.VisibleLevels(5, false), 5, "view: few rows all shown"); Eq(EnlCalculatorView.VisibleLevels(100, false), EnlCalculatorView.PreviewLevels, "view: preview limit");
        Eq(EnlCalculatorView.VisibleLevels(100, true), 100, "view: show all"); Eq(EnlCalculatorView.VisibleLevels(500, true), EnlCalculatorView.MaxLevels, "view: hard cap");
        Check(!EnlCalculatorView.CanExpand(EnlCalculatorView.PreviewLevels), "view: no toggle at the preview size");
        Check(EnlCalculatorView.CanExpand(EnlCalculatorView.PreviewLevels + 1), "view: toggle above the preview size");
    }
}
