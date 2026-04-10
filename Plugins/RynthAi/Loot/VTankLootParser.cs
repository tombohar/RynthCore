using System;
using System.IO;
using System.Collections.Generic;
using Decal.Adapter.Wrappers;
using System.Text.RegularExpressions;

namespace NexSuite.Plugins.RynthAi
{
    public enum LootAction
    {
        Keep = 0,
        Salvage = 1,
        Sell = 2,
        Read = 3,
        User1 = 4
    }

    public class LootRule
    {
        public string Name { get; set; }
        public LootAction Action { get; set; }
        public int KeepCount { get; set; }
        public string RawInfoLine { get; set; }
        public List<string> RawDataLines { get; set; } = new List<string>();

        public bool IsMatch(WorldObject item)
        {
            if (item == null || string.IsNullOrEmpty(RawInfoLine)) return false;

            try
            {
                var parts = RawInfoLine.Split(';');
                if (parts.Length < 2) return false;

                // A rule with no requirement nodes (only Action;KeepCount) is unconditional.
                // "Loot Everything" style profiles depend on this behavior.
                if (parts.Length == 2) return true;

                var data = new Queue<string>(RawDataLines);
                var stack = new Stack<bool>();

                // VTank stores requirement nodes in POSTFIX notation (Reverse Polish Notation)
                // We start at index 2 to skip the Action and KeepCount numbers
                for (int i = 2; i < parts.Length; i++)
                {
                    if (!int.TryParse(parts[i], out int type)) continue;

                    if (type == 9998) // OR Operator
                    {
                        if (stack.Count < 2) return false;
                        bool right = stack.Pop();
                        bool left = stack.Pop();
                        stack.Push(left || right);
                    }
                    else if (type == 9999) // AND Operator
                    {
                        if (stack.Count < 2) return false;
                        bool right = stack.Pop();
                        bool left = stack.Pop();
                        stack.Push(left && right);
                    }
                    else
                    {
                        // Leaf Node: Dequeue its specific data, evaluate, and push to stack
                        bool result = false;

                        switch (type)
                        {
                            case 1: // StringValueMatch (Consumes 2 lines)
                                int sKey = int.Parse(data.Dequeue());
                                string sMatch = data.Dequeue();
                                string sVal = item.Values((StringValueKey)sKey, "");
                                result = Regex.IsMatch(sVal, sMatch, RegexOptions.IgnoreCase);
                                break;

                            case 2: // LongValKeyLE (Consumes 2 lines)
                                int lKeyLE = int.Parse(data.Dequeue());
                                int lValLE = int.Parse(data.Dequeue());
                                result = item.Values((LongValueKey)lKeyLE, 0) <= lValLE;
                                break;

                            case 3: // LongValKeyGE (Consumes 2 lines)
                                int lKeyGE = int.Parse(data.Dequeue());
                                int lValGE = int.Parse(data.Dequeue());
                                result = item.Values((LongValueKey)lKeyGE, 0) >= lValGE;
                                break;

                            case 4: // DoubleValKeyLE (Consumes 2 lines)
                                int dKeyLE = int.Parse(data.Dequeue());
                                double dValLE = double.Parse(data.Dequeue());
                                result = item.Values((DoubleValueKey)dKeyLE, 0.0) <= dValLE;
                                break;

                            case 5: // DoubleValKeyGE (Consumes 2 lines)
                                int dKeyGE = int.Parse(data.Dequeue());
                                double dValGE = double.Parse(data.Dequeue());
                                result = item.Values((DoubleValueKey)dKeyGE, 0.0) >= dValGE;
                                break;

                            case 7: // ObjectClass (Consumes 1 line)
                                int objClass = int.Parse(data.Dequeue());
                                result = (int)item.ObjectClass == objClass;
                                break;

                            case 12: // LongValKeyE (Consumes 2 lines)
                                int lKeyE = int.Parse(data.Dequeue());
                                int lValE = int.Parse(data.Dequeue());
                                result = item.Values((LongValueKey)lKeyE, 0) == lValE;
                                break;

                            case 13: // LongValKeyNE (Consumes 2 lines)
                                int lKeyNE = int.Parse(data.Dequeue());
                                int lValNE = int.Parse(data.Dequeue());
                                result = item.Values((LongValueKey)lKeyNE, 0) != lValNE;
                                break;

                            default:
                                // If we encounter a VTank node type we haven't mapped yet, we MUST abort the rule.
                                // If we don't know how many lines of data it consumes, we can't safely 
                                // process the rest of the queue, so we fail safely and skip to the next rule.
                                return false;
                        }

                        stack.Push(result);
                    }
                }

                // The final result of the tree will be the only item left sitting in the stack.
                // If nodes existed but did not produce a valid expression, fail safely.
                return stack.Count == 1 && stack.Pop();
            }
            catch
            {
                return false; // Failsafe for missing data or parsing exceptions
            }
        }
    }

    public class VTankLootProfile
    {
        public List<LootRule> Rules { get; set; } = new List<LootRule>();
    }

    public static class VTankLootParser
    {
        public static VTankLootProfile Load(string filePath)
        {
            var profile = new VTankLootProfile();
            if (!File.Exists(filePath)) return profile;

            string[] lines = File.ReadAllLines(filePath);
            if (lines.Length < 3 || lines[0].Trim() != "UTL")
                throw new Exception("Invalid UTL file format.");

            int version = int.Parse(lines[1].Trim());
            int ruleCount = int.Parse(lines[2].Trim());

            int lineIndex = 3;

            while (lineIndex < lines.Length && profile.Rules.Count < ruleCount)
            {
                LootRule rule = new LootRule();

                // 1. Read Name (VTank sometimes leaves these blank, so we just trim the end)
                rule.Name = lines[lineIndex++].TrimEnd();

                // 2. Read the mandatory Empty Line VTank puts after names
                if (lineIndex < lines.Length && string.IsNullOrWhiteSpace(lines[lineIndex]))
                {
                    lineIndex++;
                }

                // 3. Read the InfoLine (e.g., "0;1;1;12")
                if (lineIndex < lines.Length)
                {
                    rule.RawInfoLine = lines[lineIndex++].Trim();
                    string[] parts = rule.RawInfoLine.Split(';');
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int act) && int.TryParse(parts[1], out int count))
                    {
                        rule.Action = (LootAction)act;
                        rule.KeepCount = count;
                    }
                }

                // 4. Read Requirement Data using node-token arity when possible.
                // This is more reliable than heuristic rule boundary detection for complex profiles.
                int requiredDataLines = TryGetRequiredDataLineCount(rule.RawInfoLine);
                if (requiredDataLines >= 0)
                {
                    for (int i = 0; i < requiredDataLines && lineIndex < lines.Length; i++)
                    {
                        rule.RawDataLines.Add(lines[lineIndex++].TrimEnd());
                    }
                }
                else
                {
                    // Fallback for unknown token types: heuristic boundary detection.
                    while (lineIndex < lines.Length)
                    {
                        if (IsStartOfNextRule(lines, lineIndex))
                        {
                            break;
                        }

                        rule.RawDataLines.Add(lines[lineIndex++].TrimEnd());
                    }
                }

                profile.Rules.Add(rule);
            }

            return profile;
        }

        private static bool IsStartOfNextRule(string[] lines, int index)
        {
            // We need at least 3 lines to form a valid rule start: [Name], [Empty Line], [InfoLine]
            if (index + 2 >= lines.Length) return false;

            // lines[index] is the Name. It can be literally anything, so we skip checking it.

            // lines[index+1] MUST be an empty line in the VTank format
            if (!string.IsNullOrWhiteSpace(lines[index + 1])) return false;

            // lines[index+2] MUST be an InfoLine containing semicolons
            string infoLine = lines[index + 2].Trim();
            if (!infoLine.Contains(";")) return false;

            string[] parts = infoLine.Split(';');
            if (parts.Length >= 2)
            {
                // An InfoLine always starts with two integers (Action and KeepCount)
                if (int.TryParse(parts[0], out int action) && int.TryParse(parts[1], out int _))
                {
                    // Valid VTank Actions are 0 to 4 (Allowing up to 10 just for forward compatibility)
                    if (action >= 0 && action <= 10) return true;
                }
            }

            return false;
        }

        private static int TryGetRequiredDataLineCount(string rawInfoLine)
        {
            if (string.IsNullOrWhiteSpace(rawInfoLine)) return -1;
            var parts = rawInfoLine.Split(';');
            if (parts.Length < 2) return -1;
            if (parts.Length == 2) return 0; // unconditional rule

            int required = 0;
            for (int i = 2; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out int type)) continue;

                int lines = GetDataLinesForNodeType(type);
                if (lines < 0) return -1;
                required += lines;
            }

            return required;
        }

        private static int GetDataLinesForNodeType(int type)
        {
            switch (type)
            {
                case 9998: // OR
                case 9999: // AND
                    return 0;
                case 1:  // StringValueMatch
                case 2:  // LongValKeyLE
                case 3:  // LongValKeyGE
                case 4:  // DoubleValKeyLE
                case 5:  // DoubleValKeyGE
                case 12: // LongValKeyE
                case 13: // LongValKeyNE
                    return 2;
                case 7:  // ObjectClass
                    return 1;
                default:
                    return -1;
            }
        }
    }
}