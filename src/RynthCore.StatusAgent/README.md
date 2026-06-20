# RynthCore.StatusAgent

Optional, operator-side console app. Reads the **local** per-client status files
RynthCore writes (when `EnableStatusExport` is on) and forwards a rolled-up
status payload to **a URL you configure**, so an external app (e.g. a phone
dashboard) can show whether your clients are running and actively botting.

- Runs on **your** PC only. Not injected into the game. Not in the installer.
- Sends **nothing** until you set `Endpoint` in
  `%APPDATA%\RynthCore\statusagent.json`.
- The engine half never networks — it only writes local files.

Full setup, the backend payload contract, the client `state` table, and removal
steps live in [`docs/STATUS_AGENT.md`](../../docs/STATUS_AGENT.md).

```powershell
dotnet publish .\RynthCore.StatusAgent.csproj -c Release
.\bin\Release\net10.0-windows\RynthCore.StatusAgent.exe --once --dry-run   # smoke test
```
