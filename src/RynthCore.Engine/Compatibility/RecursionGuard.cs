// ============================================================================
//  RynthCore.Engine - Compatibility/RecursionGuard.cs
//
//  Deep-audit finding #27 (2026-06-18): PERMANENTLY DISABLED, not
//  temporarily. This used to sample Environment.StackTrace on a fraction of
//  calls to detect runaway same-thread recursion across the ~24 detour call
//  sites that still call Tick(name) on entry. It was switched off while
//  ruling out whether the StackTrace allocation itself was contributing to a
//  second recursive AV (full budget + D3D9-off + ImGui-off) — that
//  investigation never re-enabled it, and the "TEMPORARILY DISABLED /
//  re-enable by removing this early-return" comment it carried was
//  misleading: nobody had, and the dead sampling body below it was never
//  going to run again without someone reading this exact comment.
//
//  Deliberately NOT re-enabled here: re-adding Environment.StackTrace
//  sampling on a hot per-detour path reintroduces the exact allocation this
//  was disabled to rule out, and a proper allocation-free depth counter
//  needs a paired enter/exit at all ~24 call sites (Tick() today is
//  entry-only) — too invasive for a diagnostic that only ever logged, never
//  prevented anything (this project already triages it as P3/cosmetic).
//  The sibling ThreadStackSampler.SampleAll() (cross-thread, EBP-walk based,
//  no per-call allocation) is a safer design for the same goal, but it
//  SuspendThreads every thread in the process on each call — wiring it to
//  run unattended on a timer is a real behavioral risk (a live gaming
//  client hitching, or worse, one AC thread suspended mid critical-section)
//  that deserves its own deliberate decision, not a drive-by from this
//  finding. Tick() stays a real, honest no-op — call sites keep compiling
//  unchanged, and no allocation or new risk is introduced anywhere.
// ============================================================================

namespace RynthCore.Engine.Compatibility;

internal static class RecursionGuard
{
    public static void Tick(string detourName)
    {
        // No-op by design — see the file-header comment above.
    }
}
