# DComp parallel overlay system (TEST HARNESS)

Opt-in alternative to the production AvaloniaOverlay. Lives entirely in this folder; activated by env var:

```powershell
$env:RYNTHCORE_DCOMP_OVERLAY = "1"
```

When set, [EntryPoint.cs](../../EntryPoint.cs) skips `AvaloniaOverlay.Start()` and calls `DcompOverlayBootstrap.Start()` instead. **Production code paths are unaffected when the env var is unset.**

## Why this exists

Phase A of the WGL_NV_DX_interop2 spike (see `spikes/WglDxInterop/findings.md`) ruled out GPU-into-AC's-D3D9 rendering on NVIDIA. The remaining viable GPU path is "render the Avalonia overlay into its own ANGLE swap chain on a separate top-level layered window, composited by DWM over AC's window."

This folder is the side-by-side test bed for that approach. **Goal: prove the rendering, positioning, Z-order, transparency, and DWM compositing work correctly before committing to a full migration.**

## Architecture (per AC client)

```
acclient.exe (one process per client)
 └─ AC's main HWND  (D3D9 backbuffer, AC renders here)
     └─ DcompOverlayWindow  (Avalonia Window, owned-Z-order child of AC's HWND)
           └─ DComp / ANGLE swap chain composited by DWM
           └─ Avalonia content tree (test panel for now)
```

- `DcompOverlayBootstrap.cs` — entry point, spawns the STA thread, runs Avalonia.
- `DcompOverlayApp.cs` — Avalonia App configured with `Win32CompositionMode.LowLatencyDComp` and default rendering (no custom platform graphics, no software fallback). Avalonia uses its own ANGLE.
- `DcompOverlayWindow.cs` — borderless, transparent, no chrome. Sized and positioned to AC's client area. Set as owned by AC HWND via SetWindowLong(GWL_HWNDPARENT). Initially hosts a simple visual indicator.
- `AcWindowTracker.cs` — subclasses AC's WndProc to catch WM_MOVE/WM_SIZE/WM_SHOWWINDOW and propagate to the overlay window.

## Phases

**Phase A (this commit):** scaffold the bootstrap, get an Avalonia window appearing over AC with a test panel. Verify GPU rendering, transparency, owner-Z-order. Don't worry about input forwarding or content yet.

**Phase B:** AC HWND tracking — overlay follows AC's moves, resizes, hide/show on minimize. Verify against multi-client scenarios (cascaded windows, foreground vs background).

**Phase C (deferred):** port the actual bar + docked panels into this system, replacing the EndScene composite path.
