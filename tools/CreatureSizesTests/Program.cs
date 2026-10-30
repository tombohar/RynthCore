// Checks CreatureSizes (the lookup over the generated table) against creatures with known heights.
using RynthCore.Engine.ImGuiBackend.Hud;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) pass++;
    else { fail++; Console.WriteLine($"FAIL {what} {detail}"); }
}

void Top(uint wcid, string name, float expected)
{
    bool found = CreatureSizes.TryGetTop(wcid, out float m);
    Check($"{name} ({wcid}) is in the table", found);
    Check($"{name} ({wcid}) top", found && MathF.Abs(m - expected) < 0.006f, $"got {m}, want {expected}");
}

Check("the table is not empty", CreatureSizes.Count > 8000, $"count {CreatureSizes.Count}");

Top(1, "human", 1.84f);
Top(5, "lugianlaigus", 2.08f);
Top(185, "bandit", 1.84f);
Top(1621, "olthoiqueen", 2.59f);
Top(1756, "shadowchild", 0.92f);   // a default scale of 0.5 halves its model

// Unknown ids fall through to the HUD's estimate.
Check("wcid 0 is unknown", !CreatureSizes.TryGetTop(0, out _));
Check("a huge id is unknown", !CreatureSizes.TryGetTop(uint.MaxValue, out _));

Console.WriteLine($"{(fail == 0 ? "PASS" : "FAIL")} {pass} checks, {fail} failed");
return fail == 0 ? 0 : 1;
