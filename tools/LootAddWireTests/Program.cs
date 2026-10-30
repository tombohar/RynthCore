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

            Console.WriteLine(_fails == 0 ? $"All {_asserts} checks passed." : $"{_fails} of {_asserts} checks FAILED.");
            return _fails == 0 ? 0 : 1;
        }
    }
}
