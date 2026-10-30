# Tracker panel: Lum/5m and pass-up rows

Pairs with RynthTracker 0.4.0 in RynthSuite.

## What changed

RynthTracker and RynthAi's Mini Remote now share one session-stats engine (RynthSuite
`Plugins/Shared/SessionStats.cs`), so the Tracker panel and the Mini Remote count XP, luminance and
kills the same way. The Tracker panel shows the new figures:

- **Lum/5m:** luminance earned in the last five minutes. It shows when RynthTracker sends it
  (0.4.0 and later), so an older RynthTracker still gets the old rows.
- **Pass/hr and Pass-up:** allegiance pass-up luminance per hour and for the session. These rows
  appear only where RynthTracker can tell pass-up apart, which is on ACECustom, where the bank
  luminance is read. Fellowship and Zone Share splits arrive the same silent way and are included;
  the label tooltip says so.
- **Row order:**
  1. Session
  2. XP/hr
  3. Lum/hr
  4. Lum/5m
  5. [Pass/hr]
  6. [Rad/hr]
  7. Kls/hr
  8. XP
  9. Lum
  10. [Pass-up]
  11. [Rad]
  12. Kills
  13. XP/kl
  14. Deaths

  Rows in brackets appear only on some servers.

## Files

- `src/RynthCore.Engine/UI/Data/PanelSources.cs`: `TrackerSource` reads `l5`, `pk`, `ph` and `pt`;
  `TrackerSnapshot.DefaultLabels` includes Lum/5m.
- `src/RynthCore.Engine/ImGui/Panels/P1Faces.cs`: tooltips on the Lum/5m and pass-up labels.

## Testing

- The engine builds in Release, and ItemHudTests pass (63 assertions).
- Not yet checked in game.
