# Settings > Display: Borderless Windowed, remembered windows, Save Layout Now

Branch `feature/display-chrome`, from `status-export`. Not deployed. From SilentKelpie's
RynthCore PR 6 (the Display chrome piece only), with the changes below.

## What changed

- `UI/AcWindowChrome.cs` (Win32) and `UI/AcWindowChromePolicy.cs` (the decisions, pure and tested).
- Settings > Display:
  - **Borderless Windowed** (`BorderlessWindowed`, default off): hides the Asheron's Call title bar and
    borders while staying windowed.
  - **Remember Open Windows and Positions** (`RememberWindowLayout`, default on, as panels have always
    been): off stops `panel_state.txt` being overwritten and stops panels reopening from the last session.
  - **Remember Game Window Position** (`RememberAcWindowPosition`, default off, new): saves the game
    window's position and size and puts it back after login.
  - **Save Layout Now**: writes open panels, the window position (if remembered), the Vision HUD and
    `imgui.ini` now.

## How it differs from the PR

- Chrome is only given back to a window this setting stripped. That is recorded on the window itself
  (an HWND property holding the style from before), so it survives an engine hot reload and ends with
  the process. With the setting off and no marker nothing is touched: exclusive fullscreen and external
  borderless tools are left alone.
- The window rect is kept per client (`ac_window.<server-character>.txt`), written only when it
  changed (checked every 10 s, written by the background writer). Until a character is known nothing is
  restored or saved; after 90 s without one the shared `ac_window.txt` is used.
- Nothing changes the window inside the render callback. EndScene only reads; style changes and the
  move/resize are posted to the game window's own WndProc (`Win32Backend.PostToGameThread`, as pop-out
  windows are created). Settings toggles go the same way. Without the ImGui backend (no WndProc
  subclass) the feature stays inactive.
- An off-screen saved rect, a rect under 160x120 or a minimised/maximised/fullscreen-looking window is
  ignored. `PanelStateStore.Flush` no longer claims to be a no-op when remembering is off (it is an
  explicit save).
- Not taken: Vaulted gate, chat 0x1C, hot-reload hold, bar declutter.

## Tests

`tools/EngineSettingsTests`: defaults and engine.json round trip for the three settings, the
borderless decisions (setting off with no marker changes nothing; our marker restores; on strips once),
off-screen / size limits, per-client file names, the rect file.

Not yet tested in game (windowed vs fullscreen, hot reload).
