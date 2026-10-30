# Engine review fixes (crash log size, dump safety, transition gate valve, AutoId)

RynthCore.SehTrampoline.dll native version 8 (previous: 7). Branch `fix/engine-review`, from
`integration/pr-fixes-2026-10-18`. Not deployed.

## native-crash.log no longer grows without bound

- Decal's handled null-ref AV (about once a minute, one address) was written on every hit: the 30 s
  "relog" applied to noise too. About 1,440 entries a day, 5-6 KB each. Now a noise address repeats only
  at powers of two (about 11 entries a day for that address). The 30 s relog and the always-write for
  a non-AV code stay for genuine (AC-class) addresses, capped at `CLF_MAX_RELOGS` (16) per address.
  What a cap skips is still in the flush ring, so a fatal hit is recovered at exit.
- A total byte budget per process (`CLF_MAX_LOG_BYTES`, 3 MB, counted from the file's size at install):
  past it ordinary entries stop after one marker line. Fatal entries (last chance, crash-exit flush)
  get 256 KB more.
- `native-crash.log` is one file for every client, so `LogPaths.RotateAtStartup` (per-PID) never
  covered it. `LogPaths.RotateNativeCrashLogAtStartup` moves one over 1 MB to `native-crash.log.old`
  on the first engine generation, before the native side opens it.
- Tests: `native/SehTrampoline/tests/Run-CrashLogFilterTests.ps1`.

## Full-memory OUT OF MEMORY dump: disk check and a cap across clients

`DumpBudget` (pure, tested): the volume must keep 2 GB free after the dump, and the full dumps already
in the folder (any client) are capped at 4 files / 14 GB. The check and an empty placeholder file are
made under one machine-wide mutex, so clients that hit the box together cannot all pass first. A refusal
prunes older dumps once and asks again, else writes the small dump. The small dump (also the hang dump)
needs 200 MB free.

## Crash dump on TerminateProcess / NtTerminateProcess is back (bounded)

7cbf69e removed the managed dump on a crash exit and relied on CoreCLR's `createdump`. Checked offline
with a small CoreCLR host: `createdump` does run for a native AV that unwinds through a managed frame
and for one that reaches the CLR's unhandled-exception filter. It does not run when AC's own handler
catches a native AV and answers with TerminateProcess (no CLR frame, no CLR filter), and nothing else
wrote a dump there. `ProcessExitHooks.DumpCrashOnce` writes one small dump on that path: after the
native log flush, on its own thread with a 20 s wait, once per process, not when `clr_<pid>_*.dmp`
exists, newest 5 kept.

## World-transition gate: safety valve and a setting

A gate held continuously for 120 s (`WorldTransitionState.MaxHoldMs`) opens by itself: logged once
(`[Lifecycle] world-transition gate OPENED by its safety valve`), counted in the heartbeat as
`gateov=N`, closed again when the raw transition ends. engine.json `TransitionGate` (default true;
only a hand-set false is written) turns the gate off; it is read at client start.

## AutoId

- `AutoIdDrain`: when the 512-per-tick evaluation budget runs out with items left, the evaluated ones
  move to the back, so the next tick starts where this one stopped.
- Identify data kept since a client release is dropped on re-create when the release was
  `ReleasedObjectRetention.StaleAfterMs` (2 min) or more ago (the client sends no updates for a released
  object). A short absence still keeps its data. The no-re-appraisal window after a release is
  therefore effectively 2 min rather than 10 for data freshness; tune the constant if that is too eager.
