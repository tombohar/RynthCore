using System;
using System.Collections.Generic;
using System.IO;
using NexSuite.Plugins.RynthAi;

namespace RynthAi.Meta
{
    public static class VTankMetaParser
    {
        public static List<MetaRule> IntelligentlyLoad(string path)
        {
            if (!File.Exists(path)) return new List<MetaRule>();

            try
            {
                using (StreamReader reader = new StreamReader(path))
                {
                    string firstLine = reader.ReadLine();
                    if (firstLine != "1") return null; // Not a valid VTank file format

                    // Skip the 11-line header (CondAct schema metadata)
                    for (int i = 0; i < 11; i++) reader.ReadLine();

                    string countLine = reader.ReadLine();
                    int ruleCount = int.Parse(countLine ?? "0");
                    List<MetaRule> rules = new List<MetaRule>();

                    for (int i = 0; i < ruleCount; i++)
                    {
                        rules.Add(ParseCondAct(reader));
                    }
                    return rules;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RynthAi] Meta Load Error: {ex.Message}");
                return new List<MetaRule>();
            }
        }

        private static MetaRule ParseCondAct(StreamReader reader)
        {
            MetaRule rule = new MetaRule();

            // 1. CType (Condition Type - Integer)
            reader.ReadLine(); // Consume 'i' type marker
            rule.Condition = (MetaConditionType)int.Parse(reader.ReadLine());

            // 2. AType (Action Type - Integer)
            reader.ReadLine(); // Consume 'i' type marker
            int vTankActionType = int.Parse(reader.ReadLine());

            // Map VTank Action IDs to RynthAi Action Enums
            if (vTankActionType == 1) rule.Action = MetaActionType.SetMetaState;
            else if (vTankActionType == 2) rule.Action = MetaActionType.ChatCommand;
            else if (vTankActionType == 3) rule.Action = MetaActionType.EmbeddedNavRoute;

            // 3. CData (Condition Data - Variant)
            object cData = ReadVariant(reader);

            // Handle VTank Data Tables (Type 2) for complex conditions
            if (cData is Dictionary<string, object> cDict)
            {
                // --- MONSTER COUNT TABLE ---
                if (rule.Condition == MetaConditionType.MonsterNameCountWithinDistance)
                {
                    string name = cDict.ContainsKey("n") ? cDict["n"].ToString() : "";
                    string dist = cDict.ContainsKey("d") ? cDict["d"].ToString() : "0";
                    string count = cDict.ContainsKey("c") ? cDict["c"].ToString() : "0";
                    rule.ConditionData = $"{name},{dist},{count}";
                }
                // --- INVENTORY COUNT TABLE ---
                else if (rule.Condition == MetaConditionType.InventoryItemCount_LE ||
                         rule.Condition == MetaConditionType.InventoryItemCount_GE)
                {
                    string name = cDict.ContainsKey("n") ? cDict["n"].ToString() : "";
                    string count = cDict.ContainsKey("c") ? cDict["c"].ToString() : "0";
                    rule.ConditionData = $"{name},{count}";
                }
                // --- SPELL TIME TABLE (NEW) ---
                else if (rule.Condition == MetaConditionType.TimeLeftOnSpell_GE)
                {
                    string id = cDict.ContainsKey("s") ? cDict["s"].ToString() : "0";
                    string time = cDict.ContainsKey("t") ? cDict["t"].ToString() : "0";
                    rule.ConditionData = $"{id},{time}";
                }
                else
                {
                    rule.ConditionData = "TableData"; // Fallback for unsupported tables
                }
            }
            else
            {
                rule.ConditionData = cData?.ToString() ?? "";
            }

            // 4. AData (Action Data - Variant)
            object aData = ReadVariant(reader);
            rule.ActionData = aData?.ToString() ?? "";

            // 5. State (The State this rule belongs to - String)
            reader.ReadLine(); // Consume 's' type marker
            rule.State = reader.ReadLine();

            // 6. Check for recursion (Children for Group conditions like All/Any/Not)
            if (rule.Condition == MetaConditionType.All || rule.Condition == MetaConditionType.Any || rule.Condition == MetaConditionType.Not)
            {
                string nextType = reader.ReadLine();
                if (nextType == "2") // Marker for the children table
                {
                    // Skip table metadata (k, v, n, n)
                    for (int j = 0; j < 4; j++) reader.ReadLine();

                    int childCount = int.Parse(reader.ReadLine() ?? "0");
                    for (int j = 0; j < childCount; j++)
                    {
                        // Consume the table index key, then parse the child rule
                        ReadVariant(reader);
                        rule.Children.Add(ParseCondAct(reader));
                    }
                }
            }

            return rule;
        }

        /// <summary>
        /// Reads a VTank "Variant" which includes a type marker (s, i, d, 2, etc.) 
        /// followed by the data. This allows the parser to stay synchronized.
        /// </summary>
        private static object ReadVariant(StreamReader reader)
        {
            string typeMarker = reader.ReadLine();
            if (typeMarker == null) return null;

            // Handle standard scalar types
            switch (typeMarker)
            {
                case "s": // String
                case "i": // Integer
                case "d": // Double
                case "b": // Boolean
                case "u": // Unsigned Int
                case "f": // Float
                    return reader.ReadLine();

                case "2": // Table / Dictionary
                    // Skip table metadata headers
                    for (int i = 0; i < 4; i++) reader.ReadLine();

                    int pairCount = int.Parse(reader.ReadLine() ?? "0");
                    Dictionary<string, object> table = new Dictionary<string, object>();

                    for (int j = 0; j < pairCount; j++)
                    {
                        object key = ReadVariant(reader);
                        object val = ReadVariant(reader);

                        if (key != null)
                        {
                            table[key.ToString()] = val;
                        }
                    }
                    return table;

                default:
                    return null;
            }
        }
    }
}