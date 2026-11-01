using System.Text.RegularExpressions;
using RynthCore.Engine.UI;

namespace LootPatternTextTests;

// Offline tests for the Loot Editor's text-condition modes. See the csproj.
internal static class Program
{
    private static int _fails, _asserts;
    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    // RynthAi's evaluator: Regex.IsMatch(value, pattern, IgnoreCase).
    private static bool Matches(string pattern, string text) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);

    private static int Main()
    {
        try
        {
            Writes();
            WhatTheyMatch();
            ReadBack();
            OtherPatternsAreRegex();
        }
        catch (Exception ex)
        {
            _fails++;
            Console.WriteLine($"  [FAIL] threw: {ex}");
        }
        Console.WriteLine(_fails == 0 ? $"PASS ({_asserts} assertions)" : $"FAIL: {_fails} of {_asserts} assertions failed");
        return _fails == 0 ? 0 : 1;
    }

    private static void Writes()
    {
        Check(LootPatternText.Build(LootTextMode.Contains, "Sanguine") == "Sanguine", "contains: the word");
        Check(LootPatternText.Build(LootTextMode.NotContains, "Sanguine") == "^(?!.*Sanguine)", "doesn't contain: a lookahead");
        Check(LootPatternText.Build(LootTextMode.NotContains, "Sanguine, Tattered") == "^(?!.*(Sanguine|Tattered))", "doesn't contain: several words");
        Check(LootPatternText.Build(LootTextMode.Contains, "Sanguine, Tattered") == "Sanguine|Tattered", "contains: several words");
        Check(LootPatternText.Build(LootTextMode.StartsWith, "Ancient") == "^Ancient", "starts with");
        Check(LootPatternText.Build(LootTextMode.EndsWith, "Robe") == "Robe$", "ends with");
        Check(LootPatternText.Build(LootTextMode.Exactly, "Trade Note (250,000)") == "^Trade Note \\(250,000\\)$", "is exactly: brackets escaped, spaces kept");
        Check(LootPatternText.Build(LootTextMode.Regex, "a.*b") == "a.*b", "regex: as typed");
        Check(LootPatternText.Build(LootTextMode.NotContains, "  ") == "", "no words: empty");
        Check(LootPatternText.Build(LootTextMode.StartsWith, "") == "", "no words: empty (not a bare ^)");
        Check(LootPatternText.Escape("a.b*c?") == "a\\.b\\*c\\?", "escape: metacharacters");
    }

    private static void WhatTheyMatch()
    {
        string not = LootPatternText.Build(LootTextMode.NotContains, "Sanguine");
        Check(!Matches(not, "Sanguine Robe") && !Matches(not, "Robe of the sanguine"), "doesn't contain: any case, anywhere");
        Check(Matches(not, "Frost Robe") && Matches(not, "") , "doesn't contain: other names (and an empty one) pass");
        string notTwo = LootPatternText.Build(LootTextMode.NotContains, "Sanguine, Tattered");
        Check(!Matches(notTwo, "Tattered Cloak") && !Matches(notTwo, "Sanguine Robe") && Matches(notTwo, "Fine Cloak"), "doesn't contain: either word");
        string has = LootPatternText.Build(LootTextMode.Contains, "Sanguine, Tattered");
        Check(Matches(has, "a TATTERED cloak") && !Matches(has, "Fine Cloak"), "contains: either word, any case");
        Check(Matches(LootPatternText.Build(LootTextMode.StartsWith, "Ancient"), "Ancient Sword") && !Matches(LootPatternText.Build(LootTextMode.StartsWith, "Ancient"), "An Ancient Sword"), "starts with");
        Check(Matches(LootPatternText.Build(LootTextMode.EndsWith, "Robe"), "Frost Robe") && !Matches(LootPatternText.Build(LootTextMode.EndsWith, "Robe"), "Robe of Frost"), "ends with");
        string exact = LootPatternText.Build(LootTextMode.Exactly, "Trade Note (250,000)");
        Check(Matches(exact, "Trade Note (250,000)") && !Matches(exact, "Trade Note (250,000) x2") && !Matches(exact, "Trade Note 250,000"), "is exactly: the whole name, brackets literal");
        Check(Matches(LootPatternText.Build(LootTextMode.Contains, "a.b"), "a.b") && !Matches(LootPatternText.Build(LootTextMode.Contains, "a.b"), "axb"), "a dot typed is a dot");
    }

    private static void ReadBack()
    {
        foreach (var (mode, words) in new[]
        {
            (LootTextMode.Contains, "Sanguine"), (LootTextMode.NotContains, "Sanguine"), (LootTextMode.NotContains, "Sanguine, Tattered"),
            (LootTextMode.Contains, "Sanguine, Tattered"), (LootTextMode.StartsWith, "Ancient"), (LootTextMode.EndsWith, "Robe"),
            (LootTextMode.Exactly, "Trade Note (250,000)"), (LootTextMode.Contains, "a.b"), (LootTextMode.Exactly, "Mana Stone"),
        })
        {
            var (m, w) = LootPatternText.Parse(LootPatternText.Build(mode, words));
            Check(m == mode && w == words, $"round trip {mode} '{words}' -> {m} '{w}'");
        }
        Check(LootPatternText.Parse("") == (LootTextMode.Contains, ""), "an empty pattern: contains, no words");
        // LootSdk's ExactPattern (the "Add to loot profile" rules): "^" + EscapeText(name) + "$".
        Check(LootPatternText.Parse("^Pyreal$") == (LootTextMode.Exactly, "Pyreal"), "the add-item rules' exact names read as is exactly");
    }

    private static void OtherPatternsAreRegex()
    {
        foreach (string p in new[] { "a.*b", "^(Robe|Cloak)$", "Sword|", "\\d+", "^$", "^(?!.*Sanguine)Robe" })
            Check(LootPatternText.Parse(p).Mode == LootTextMode.Regex, $"'{p}' is regex");
        Check(LootPatternText.Parse("Robe\\$") == (LootTextMode.Contains, "Robe$"), "an escaped $ is a literal: contains 'Robe$'");
        Check(LootPatternText.Parse("a.*b").Words == "a.*b", "regex: its words are the pattern");
    }
}
