using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LootAddWireTests
{
    // The engine half of the "Add to loot profile" attribute checklist, against what RynthAi's
    // LootEditWire (RynthSuite Shared/RynthCore.LootSdk/Editing) writes and reads. The JSON literals
    // below are LootEditJsonContext's output / input (PascalCase, case-insensitive on read).
    internal static class Program
    {
        private static int _fails, _asserts;
        private static void Check(bool cond, string msg)
        {
            _asserts++;
            if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
        }

        private const string Ns = "RynthCore.Engine.UI.Data.";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static int Main()
        {
            Assembly engine = Assembly.Load("RynthCore.Engine");
            Type ctxType = engine.GetType(Ns + "LootEditJsonContext", throwOnError: true)!;
            object ctx = ctxType.GetProperty("Default", All)!.GetValue(null)!;
            var stateInfo = (JsonTypeInfo)ctxType.GetProperty("LootEditStateDto", All)!.GetValue(ctx)!;
            var cmdInfo = (JsonTypeInfo)ctxType.GetProperty("LootEditCmd", All)!.GetValue(ctx)!;

            Type stateType = engine.GetType(Ns + "LootEditStateDto", throwOnError: true)!;
            Type draftType = engine.GetType(Ns + "LootEditItemDraftDto", throwOnError: true)!;
            Type rowType = engine.GetType(Ns + "LootEditItemAttributeDto", throwOnError: true)!;
            Type cmdType = engine.GetType(Ns + "LootEditCmd", throwOnError: true)!;
            Type reqType = engine.GetType(Ns + "LootEditItemRequestDto", throwOnError: true)!;
            Type choiceType = engine.GetType(Ns + "LootEditItemChoiceDto", throwOnError: true)!;
            Type opsType = engine.GetType(Ns + "LootAttrOps", throwOnError: true)!;

            // ── RynthAi -> engine: a draft with the checklist ───────────────────────────
            const string draftJson =
                "{\"ItemDraft\":{\"Seq\":7,\"ItemId\":2147483648,\"ItemName\":\"Gold Sword\",\"AttributesSupported\":true," +
                "\"Attributes\":[" +
                "{\"Id\":\"L:131\",\"Group\":\"Item\",\"Label\":\"Material\",\"ItemValue\":\"Gold\",\"Value\":\"Gold\",\"Op\":1,\"Ops\":[1,3],\"Editable\":false,\"On\":true,\"Tip\":\"t\"}," +
                "{\"Id\":\"spell:Major Strength\",\"Group\":\"Spells\",\"Label\":\"Major Strength\",\"ItemValue\":\"Major\",\"Value\":\"Major\",\"Op\":11,\"Ops\":[10,11,12],\"Editable\":false,\"On\":false,\"Tip\":\"\"}," +
                "{\"Id\":\"L:5\",\"Group\":\"Item\",\"Label\":\"Burden\",\"ItemValue\":\"8\",\"Value\":\"9\",\"Op\":2,\"Ops\":[0,1,2],\"Editable\":true,\"On\":true,\"Tip\":\"\"}]}}";
            object state = JsonSerializer.Deserialize(draftJson, stateInfo)!;
            object draft = stateType.GetProperty("ItemDraft", All)!.GetValue(state)!;
            Check((bool)draftType.GetProperty("AttributesSupported", All)!.GetValue(draft)!, "AttributesSupported is read");
            var rows = ((System.Collections.IEnumerable)draftType.GetProperty("Attributes", All)!.GetValue(draft)!).Cast<object>().ToList();
            Check(rows.Count == 3, "three rows read");
            object spell = rows.First(r => (string)rowType.GetProperty("Id")!.GetValue(r)! == "spell:Major Strength");
            Check((int)rowType.GetProperty("Op")!.GetValue(spell)! == 11 && ((List<int>)rowType.GetProperty("Ops")!.GetValue(spell)!).SequenceEqual(new[] { 10, 11, 12 }), "a spell row: op and the ops it offers");
            Check(!(bool)rowType.GetProperty("On")!.GetValue(spell)!, "a spell row is unticked");
            object burden = rows.First(r => (string)rowType.GetProperty("Id")!.GetValue(r)! == "L:5");
            Check((bool)rowType.GetProperty("Editable")!.GetValue(burden)! && (string)rowType.GetProperty("Value")!.GetValue(burden)! == "9" && (string)rowType.GetProperty("ItemValue")!.GetValue(burden)! == "8", "a numeric row: editable, the rule's value and the item's");

            // The op codes the popup labels are the SDK's LootItemAttributeOp.
            MethodInfo label = opsType.GetMethod("Label", All)!;
            string L(int op) => (string)label.Invoke(null, new object[] { op })!;
            Check(L(0) == ">=" && L(1) == "=" && L(2) == "<=" && L(3) == "not", "numeric / named ops label as >= = <= not");
            Check(L(10) == "exact" && L(11) == "or better" && L(12) == "any tier", "spell ops label as exact / or better / any tier");

            // ── An older RynthAi: no checklist -> the popup keeps its T11 box ───────────
            object older = JsonSerializer.Deserialize("{\"ItemDraft\":{\"Seq\":3,\"ItemName\":\"Gold Sword\",\"IncludeT11\":true,\"IsT11\":true}}", stateInfo)!;
            object olderDraft = stateType.GetProperty("ItemDraft", All)!.GetValue(older)!;
            Check(!(bool)draftType.GetProperty("AttributesSupported", All)!.GetValue(olderDraft)!, "an older RynthAi: AttributesSupported is false (the T11 box shows)");
            Check(((System.Collections.ICollection)draftType.GetProperty("Attributes", All)!.GetValue(olderDraft)!).Count == 0, "and there are no rows");
            Check((bool)draftType.GetProperty("IncludeT11", All)!.GetValue(olderDraft)!, "its T11 answer is still read");

            // ── Engine -> RynthAi: the ask ───────────────────────────────────────────────
            object choice = Activator.CreateInstance(choiceType, nonPublic: true)!;
            choiceType.GetProperty("Id")!.SetValue(choice, "L:5");
            choiceType.GetProperty("On")!.SetValue(choice, true);
            choiceType.GetProperty("Op")!.SetValue(choice, 2);
            choiceType.GetProperty("Value")!.SetValue(choice, "9");
            object req = Activator.CreateInstance(reqType, nonPublic: true)!;
            reqType.GetProperty("ItemId")!.SetValue(req, 0x80000001u);
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(choiceType))!;
            list.Add(choice);
            reqType.GetProperty("Choices")!.SetValue(req, list);
            object cmd = Activator.CreateInstance(cmdType, nonPublic: true)!;
            cmdType.GetProperty("Op")!.SetValue(cmd, "item_preview");
            cmdType.GetProperty("Item")!.SetValue(cmd, req);
            string cj = JsonSerializer.Serialize(cmd, cmdInfo);
            Check(cj.Contains("\"Choices\":[{\"Id\":\"L:5\",\"On\":true,\"Op\":2,\"Value\":\"9\"}]"), "choices are written as RynthAi's LootEditItemChoice reads them: " + cj);

            // Until the player changes a row the ask carries none: RynthAi's own defaults (and notes) apply.
            object bare = Activator.CreateInstance(reqType, nonPublic: true)!;
            Check(reqType.GetProperty("Choices")!.GetValue(bare) == null, "a fresh ask has no choices");
            cmdType.GetProperty("Item")!.SetValue(cmd, bare);
            Check(JsonSerializer.Serialize(cmd, cmdInfo).Contains("\"Choices\":null"), "which is written as null (an older RynthAi ignores the field)");

            PerDayChecks(engine, ctxType, ctx, stateInfo, cmdInfo, cmdType);

            Console.WriteLine(_fails == 0 ? $"All {_asserts} checks passed." : $"{_fails} of {_asserts} checks FAILED.");
            return _fails == 0 ? 0 : 1;
        }

        // Keep # per day (2026-11-22): the rule view's option and count, both ways, and with an older RynthAi.
        private static void PerDayChecks(Assembly engine, Type ctxType, object ctx, JsonTypeInfo stateInfo, JsonTypeInfo cmdInfo, Type cmdType)
        {
            var ruleInfo = (JsonTypeInfo)ctxType.GetProperty("LootEditRuleDto", All)!.GetValue(ctx)!;
            Type ruleType = engine.GetType(Ns + "LootEditRuleDto", throwOnError: true)!;
            Type stateType = engine.GetType(Ns + "LootEditStateDto", throwOnError: true)!;
            Type rowType = engine.GetType(Ns + "LootEditRowDto", throwOnError: true)!;
            Type vocab = engine.GetType(Ns + "LootEditVocabulary", throwOnError: true)!;

            // RynthAi -> engine: the rule (LootEditRule) and the list row (LootEditRow).
            object rule = JsonSerializer.Deserialize(
                "{\"Index\":3,\"Name\":\"Treasure Maps\",\"Action\":10,\"KeepCount\":2,\"PerDayHours\":24,\"PerDayStatus\":\"1 of 2 picked up in the last 24 h, next frees in 5 h\",\"Conditions\":[]}",
                ruleInfo)!;
            Check((int?)ruleType.GetProperty("PerDayHours")!.GetValue(rule) == 24, "the rule's per-day window is read");
            Check((string)ruleType.GetProperty("PerDayStatus")!.GetValue(rule)! == "1 of 2 picked up in the last 24 h, next frees in 5 h", "and its count line");
            object older = JsonSerializer.Deserialize("{\"Index\":3,\"Name\":\"Maps\",\"Action\":10,\"KeepCount\":2,\"Conditions\":[]}", ruleInfo)!;
            Check(ruleType.GetProperty("PerDayHours")!.GetValue(older) == null, "an older RynthAi: PerDayHours is null (the option is hidden)");

            object state = JsonSerializer.Deserialize(
                "{\"Rules\":[{\"Name\":\"Treasure Maps\",\"Action\":10,\"KeepCount\":2,\"PerDayHours\":24,\"PerDayStatus\":\"2 of 2\"},{\"Name\":\"Gems\",\"Action\":10,\"KeepCount\":5}]}",
                stateInfo)!;
            var rows = ((System.Collections.IEnumerable)stateType.GetProperty("Rules", All)!.GetValue(state)!).Cast<object>().ToList();
            Check((int)rowType.GetProperty("PerDayHours")!.GetValue(rows[0])! == 24 && (int)rowType.GetProperty("PerDayHours")!.GetValue(rows[1])! == 0,
                "rows: the per-day one has its window, a plain Keep # has 0");

            // The badge.
            MethodInfo badge = vocab.GetMethod("Badge", All)!;
            string B(int a, int k, int h) => (string)badge.Invoke(null, new object[] { a, k, h })!;
            Check(B(10, 2, 24) == "KEEP 2/DAY" && B(10, 2, 48) == "KEEP 2/48H" && B(10, 5, 0) == "KEEP 5" && B(1, 0, 24) == "KEEP",
                $"badges: {B(10, 2, 24)} / {B(10, 2, 48)} / {B(10, 5, 0)} / {B(1, 0, 24)}");

            // Engine -> RynthAi: update_rule carries the window; an edit of it makes the view "changed".
            MethodInfo clone = ruleType.GetMethod("Clone", All)!, same = ruleType.GetMethod("SameAs", All)!;
            object edit = clone.Invoke(rule, null)!;
            Check((bool)same.Invoke(edit, new[] { rule })!, "a clone is the same rule");
            ruleType.GetProperty("PerDayHours")!.SetValue(edit, 0);
            Check(!(bool)same.Invoke(edit, new[] { rule })!, "turning Per day off is an edit");
            ruleType.GetProperty("PerDayHours")!.SetValue(edit, 24);
            ruleType.GetProperty("PerDayStatus")!.SetValue(edit, "something else");
            Check((bool)same.Invoke(edit, new[] { rule })!, "the count line is not an edit");
            ruleType.GetProperty("PerDayHours")!.SetValue(edit, 0);
            object cmd = Activator.CreateInstance(cmdType, nonPublic: true)!;
            cmdType.GetProperty("Op")!.SetValue(cmd, "update_rule");
            cmdType.GetProperty("Rule")!.SetValue(cmd, edit);
            string cj = JsonSerializer.Serialize(cmd, cmdInfo);
            Check(cj.Contains("\"PerDayHours\":0"), "update_rule writes the window RynthAi's LootEditRule reads: " + cj);
            object fromOlder = clone.Invoke(older, null)!;
            cmdType.GetProperty("Rule")!.SetValue(cmd, fromOlder);
            Check(JsonSerializer.Serialize(cmd, cmdInfo).Contains("\"PerDayHours\":null"), "with an older RynthAi's rule it writes null (RynthAi keeps its own)");
        }
    }
}
