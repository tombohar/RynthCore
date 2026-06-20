# RynthCore Status Agent

Surface live RynthCore client status (running / in-world / actively botting,
plus fps, plugin-pump rate, memory, vitals) to an external app — e.g. a phone
dashboard — so you can confirm a multi-box session is healthy without remoting
into the PC.

It is built in two deliberately separated halves:

1. **Engine status export** — opt-in, **default OFF**. When enabled, the engine
   writes a small **local** JSON file per client. It never opens a network
   connection.
2. **`RynthCore.StatusAgent.exe`** — a separate, optional program that runs **on
   your own PC**, reads those local files, and forwards a rolled-up payload to
   **an endpoint you configure**. It is not injected into the game, not in the
   installer, and sends nothing until you give it a URL.

---

## Privacy / "this isn't spyware"

This feature is designed so that distributing RynthCore to other people changes
nothing for them unless they explicitly turn it on, and even then nothing leaves
their machine:

- **Default off.** With `EnableStatusExport` absent or `false`, the engine writes
  no status file and behaves exactly as before.
- **No telemetry from the game.** `acclient.exe` / the engine never opens a
  socket for this feature. The most it ever does is write a local file under
  `C:\Games\RynthCore\Logs\status\`, right next to the logs it already writes.
- **The network half runs on the operator's PC, by the operator.** Only
  `RynthCore.StatusAgent.exe` makes outbound requests, only to the URL **you**
  put in **your** config, and only while you choose to run it. With no URL set it
  runs in local print-only mode and sends nothing.
- **Nothing is bundled.** The installer does not ship or auto-start the agent.

If you hand RynthCore to someone else, they get the default-off engine and no
agent — there is no path by which your build reaches into their PC.

---

## Turning it on (operator setup)

### 1. Enable the engine status export

Add to **your** `%APPDATA%\RynthCore\engine.json`:

```json
{
  "EnableStatusExport": true
}
```

(Or set the env var `RYNTHCORE_STATUS_EXPORT=1` before launching AC for a
one-session test without editing the file.) Picked up on next client launch.

The engine then writes, once per second, per running client:

```
C:\Games\RynthCore\Logs\status\RynthCore.<pid>.status.json
```

### 2. Build and run the agent

```powershell
dotnet publish .\src\RynthCore.StatusAgent\RynthCore.StatusAgent.csproj -c Release
.\src\RynthCore.StatusAgent\bin\Release\net10.0-windows\RynthCore.StatusAgent.exe
```

On first run it writes a config template to
`%APPDATA%\RynthCore\statusagent.json` and runs in **local print-only mode**
(reads + prints status, sends nothing) until you set an endpoint.

Useful flags: `--once` (one cycle then exit), `--dry-run` (read + print, never
POST), `--verbose` (print the full payload), `--interval N`, `--config PATH`,
`--help`.

To keep it running unattended, launch it at login (Task Scheduler "At log on",
or a Startup-folder shortcut).

### 3. Point it at your backend

Edit `%APPDATA%\RynthCore\statusagent.json`:

```json
{
  "Endpoint": "https://your-backend.example.com/api/rynthcore/status",
  "AuthHeaderName": "Authorization",
  "AuthHeaderValue": "Bearer YOUR_TOKEN",
  "IntervalSeconds": 5,
  "TimeoutSeconds": 15,
  "StatusDirectory": "C:\\Games\\RynthCore\\Logs\\status",
  "LogDirectory": "C:\\Games\\RynthCore\\Logs",
  "UseHeartbeatLogFallback": true,
  "StaleAfterSeconds": 12,
  "DropDeadAfterSeconds": 300,
  "Host": ""
}
```

- **Endpoint** — required to send anything. Empty = print-only.
- **AuthHeaderName/Value** — optional; sent as a request header if both set.
- **UseHeartbeatLogFallback** — when a running client has no status file (e.g.
  `EnableStatusExport` is off), derive basic status from its heartbeat log line.
  You get run/idle/loading/hung detection but no macro/profile/vitals.
- **Host** — label for this machine; empty uses the machine name.

The "micro manager" app then reads whatever your backend exposes from these
posts.

---

## Backend payload contract

The agent sends an HTTP **POST** to `Endpoint` with
`Content-Type: application/json` and (if configured) your auth header. Body:

```jsonc
{
  "schema": "rynthcore.status-agent/1",
  "host": "DESKTOP-XYZ",
  "agentVersion": "1.0.0",
  "generatedAtUtc": "2026-06-20T12:34:56.789+00:00",
  "clientCount": 2,
  "clients": [
    {
      "pid": 1234,
      "host": "DESKTOP-XYZ",
      "account": "myacct",
      "character": "Gandalf",
      "server": "Coldeve",
      "state": "botting",          // see state table below
      "healthy": true,             // false for wedged/hung/dead
      "ageSec": 0.4,               // seconds since this snapshot refreshed
      "source": "status-file",     // or "heartbeat-log" (basic fallback)
      "uptimeSec": 3600,
      "fps": 60,                   // 0 while in-world = render thread dead
      "pluginTicksPerSec": 30,     // 0 while macro on = bot pump wedged
      "workingSetMB": 540,
      "inWorld": true,
      "macroRunning": true,
      "currentState": "Killing",
      "botAction": "Cast War",
      "profile": "Default",
      "navProfile": "DiforsaPatrol",
      "lootProfile": "Salvage",
      "metaProfile": "",
      "target": "Drudge Skulker",
      "player": { "hp": 100, "maxHp": 120, "st": 80, "maxSt": 110, "mn": 60, "maxMn": 90 },
      "queueDropped": 0,
      "reconciles": 12,
      "forceClears": 0
    }
  ]
}
```

`bot`-derived fields (`macroRunning`, `profile`, `target`, `player`, …) are only
populated for `source: "status-file"` clients. For `heartbeat-log` clients they
are empty/false.

### Client `state` values

| state     | meaning                                              | healthy |
|-----------|------------------------------------------------------|---------|
| `botting` | in-world, macro running, plugin pump alive           | ✅      |
| `idle`    | in-world, macro stopped                              | ✅      |
| `running` | in-world, rendering (macro state unknown — log path) | ✅      |
| `loading` | process up, at login / char-select (not in-world)    | ✅      |
| `wedged`  | macro on but plugin pump at 0/s — bot stalled        | ❌      |
| `hung`    | in-world but fps 0, or snapshot frozen — client stuck| ❌      |
| `dead`    | process gone (crash/exit)                            | ❌      |

A simple phone view: green if every client is `healthy`, red if any is
`wedged`/`hung`/`dead`, and show each character's `state` + `character` name.

---

## Removing the feature entirely

Everything is tagged `[status-export]` in the engine — `grep -r "[status-export]"
src` finds every touch point.

1. Delete `src/RynthCore.StatusAgent/` and remove its `Project(...)` line and the
   `{A1B2C3D4-000A-...}` config block from `RynthCore.sln`.
2. Delete `src/RynthCore.Engine/Compatibility/StatusSnapshotWriter.cs`.
3. Remove the `StatusSnapshotWriter.Write(...)` call in
   `Compatibility/HeartbeatLogger.cs`.
4. Remove the `[status-export]` region in `LogPaths.cs` (status dir helpers + the
   status-prune block in `PruneOldLogs`).
5. Remove `EnableStatusExport` from `Plugins/EngineSettings.cs` (field, property,
   load, save).
6. Remove the `LastAccountName/LastCharacterName/LastServerName` members and their
   assignment in `Compatibility/SessionStateRegistry.cs`.

The engine builds and behaves exactly as before.
