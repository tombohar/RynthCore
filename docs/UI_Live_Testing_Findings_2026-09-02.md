# Live-testing findings — 2026-09-02

Found by Tom during a live client soak test while reviewing today's UI
deep-dive fixes (docs/UI_DeepDive_2026-07-02.md batches 1-4).

1. **FIXED — Resize grip too large.** The "resize" corner (bottom-right of
   docked and popped-out windows) had a much-too-large hit/visual area (22px,
   FontSize 14). Shrunk to 16px / FontSize 10-11 (matching Radar's existing
   smaller grip) across all three copies (AvaloniaOverlay.cs) and
   LayeredWindow.cs's native hit-test zone.
2. **FIXED — Chat needs a real "always interactive" toggle.** Chat had no
   click-through option at all (every docked panel except Radar is
   unconditionally interactive). Added a "Click-through (hold Ctrl to
   interact)" checkbox in chat's gear menu, mirroring Radar's existing
   Ctrl-gated click-through — off by default, opt-in.
3. **NOT a bug — chat already has an opacity control.** Gear icon → the
   existing "Background" slider is functionally the same as Radar's Opacity
   slider (background fill alpha only, same as Radar). Left as-is; flag again
   if it's still not discoverable/working after testing.
4. **FIXED — RynthAi main panel opacity buttons did nothing.** The +/- chips
   correctly reached the plugin (RynthPluginAdjustOpacity → persisted
   correctly), but the Avalonia panel never read the resulting bgOpacity
   value back and applied it anywhere. Now applied to the combat section's
   background alpha every snapshot tick.
5. **FIXED (docked only) — RynthAi "reduce" (minimize) doesn't resize the
   window.** Added real preset heights (260 expanded / 110 minimized) applied
   on toggle, for the DOCKED panel. Popped-out (floating) resize on minimize
   toggle was deliberately NOT touched — that resize path has a documented
   crash history (modal-loop + DIB churn) and needs live verification before
   changing; toggling minimize while floating still only hides/shows content
   for now.
6. **FIXED — RynthAi minimum size too large in minimized mode.** Both the
   wrapping panel's MinHeight (was a flat 260 regardless of mode) and the
   inner combat-section's MinHeight (was a flat 142) now drop to a smaller
   floor while minimized.
