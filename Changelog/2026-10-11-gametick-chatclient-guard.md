# Game-tick crash guard (chatclient.dll null read)

RynthCore.SehTrampoline.dll native version 4 (previous: 3).

## The crash

On 2026-10-11 two idle clients died in the same second. Both CoreCLR dumps show the same fault on
AC's main thread:

- AC's `Client::UseTime` called into `chatclient.dll` (Turbine's chat client in the AC folder).
  Three calls in, a `rep movsd` copy of about 2 MB read from address 0
  (`chatclient.dll+0x1050`, read of 0x00000000).
- Both clients at once, both idle: most likely a bad or oversized message from the chat server.
- The fault unwound into the engine's managed `UseTime` detour. CoreCLR treats a native access
  violation there as an unhandled exception and ends the process, so AC's own handlers never ran.
  Windows reported the follow-on fault in `coreclr.dll+0x2c27ae`, which is the runtime's fail-fast
  path, not the cause.
- `native-crash.log` had no record: a null read is held back until the process exits normally,
  and the fail-fast skipped that.

## The fix

- `native/SehTrampoline/SehTrampoline.c`: new export `SEH_GameTickThiscall` calls
  `int __thiscall fn(this)` under `__try`. On an access violation it returns 0 with the faulting
  address (module-relative), the data address and the module name, and writes a full record
  (registers and stack sweep) to `native-crash.log`, at most 16 per process.
- `Compatibility/GameTickHooks.cs`: AC's original `UseTime` goes through that wrapper when the
  loaded trampoline is version 4 or later. A caught fault drops that one tick, logs an `[ERR]`
  line, and returns 1 so AC's game loop keeps running (0 would make AC shut down). After 5 faults
  within 30 seconds the guard turns itself off for the session, so a client whose state is
  really broken still ends instead of spinning.
- `Compatibility/SehTrampoline.cs`: the P/Invoke, and `NativeVersion` (cached). A running client
  keeps the trampoline DLL it started with, so a hot-reloaded engine meeting a v3 DLL logs
  "native AV guard OFF" and calls `UseTime` directly, as before.
- `native/SehTrampoline/Build-SehTrampoline.ps1`: `-NoDeploy` builds into `bin\` without
  copying to the runtime folder.
- `scripts/Publish-EngineCoreClr.ps1`: a trampoline DLL held by a running client is now moved
  aside (`.old-<time>`) like the loader and shim, so new launches get the new one. Before, it
  was skipped with a warning and every launch stayed on the old version until all clients closed.

## Tests

- The engine builds in Release (CoreCLR host).
- An x86 harness against the new DLL: a clean call returns its value, a null read is caught and
  attributed to the right module, and a call after the caught fault works. Version reports 4.
- Not yet seen in game: the chat-server fault can't be reproduced on demand.
