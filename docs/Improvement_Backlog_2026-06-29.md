# RynthCore / RynthAi — Reliability, Diagnostics & Feature Backlog

> Authored 2026-06-29. A clean-room engineering backlog derived from our own field
> experience (the documented wedge/AV/hardlock investigations, the meta-engine
> deficiency notes, and the deep-audit findings) and our forward design goals.
>
> **Ground rules for everything below** (per `docs/LEGAL_COMPATIBILITY.md`):
> - Original work only. No code is copied, ported, or transliterated from any
>   closed third-party plugin. Each item is described as a design target, in our
>   own terms, to be implemented from scratch against our own architecture.
> - **Every numeric tuning constant** (thresholds, cooldowns, durations, ranges,
>   magnitudes, fellowship sizes, resist tables) is a placeholder to be **derived
>   empirically on our ACE test target**, never assumed from retail behaviour.
> - AC access discipline still rules: anything that reads or mutates live AC state
>   marshals through `AcMainThreadQueue` / is gated by `MainThreadGuard`. Several
>   items below are *specifically* about not regressing that (see the off-thread AV
>   class in `docs/DeepAudit_2026-06-18.md`).

---

## Executive summary

Our combat *decision-making* is already competitive or ahead of where we need it:
predictive war-magic selection, empirical per-wcid weapon learning, real-`m_cBusy`
busy-wedge handling, a disk-backed creature profile store, and an external
telemetry/control stack (StatusAgent + RynthRemote) with no equivalent elsewhere.

The gap is **observability and structure**. Our hardest, longest-running bugs —
the combat-cast orphan ("too busy forever"), the 20h respawn-blind wedge
("scanned=0 vs live mobs"), the item-action hardlock, "salvage starves combat" —
stayed *contested* for days mainly because the bot could not tell us **which stage
failed and why**. The highest-leverage work is therefore a cluster of cheap
diagnostic and structural surfaces that turn those investigations from
forensic-bisect into one-glance reads. Secondary work is a set of genuinely
absent subsystems (threshold-driven vital recovery, in-game multi-box
coordination, tell-driven control, a concurrent maintenance lane) and combat-depth
refinements (cold-start weapon prior, debuff refresh).

**Do Phase 1 first.** It is mostly low-effort, de-risks every later phase by making
regressions visible, and directly attacks bugs already on our board.

---

## Implemented (running log)

Landed in-session (compile-verified, **pending live test**), newest first:

- **2026-07-01** — **D4** (record-only cast-skip reasons): `LastBuffSkipReason` (BuffManager, 11 skip sites) + `LastCombatSkipReason` (CombatManager, 10 skip sites + a `"cast"` success marker), both surfaced through `GetStateSnapshot()` and shown in `/ra why`. Purely additive — no control-flow change; verified `safe-additive` by an adversarial reviewer against verbatim anchors. Files: `Combat/BuffManager.cs`, `Combat/CombatManager.cs`, `RynthAiCommands.cs`. **⚠ Smoke-test note:** managed-only string fields (lowest-risk class), but per `[[rynthcore_engine_addition_destabilizes_buffs]]` even byte-adjacent additions to this stack have twice destabilized buffing via NativeAOT codegen shifts — do a quick buff+combat smoke test after the first deploy. **Deferred (need the test loop):** weapon short-circuit + D5 tri-state cast return (both `needs-live-test`), and the armor-enchant equipped guard (decision flow tangled).
- **2026-06-30** — **M1** (`!=` operator + case-insensitive string `==`), **M3** (unknown-function rate-limited log), **D3** (`/ra why` with an interpretation line off the D2 counts), **D7+D8** (single `CombatManager.OnObjectDeleted` off the engine delete event: clears the cast/debuff wait if the despawned id was our target, and frees the blacklist/failure/kill-suppression maps so a respawn reusing the GUID starts clean). Files: `Meta/ExpressionEngine.cs`, `Combat/CombatManager.cs`, `Raycasting/BlacklistManager.cs`, `RynthAiPlugin.cs`, `RynthAiCommands.cs`.
- **2026-06-29** — **D2** (three-tier target telemetry), **D6** (casts-per-kill / attack-cast counters). Files: `Combat/CombatManager.cs`, `LegacyUi/LegacyDashboardRenderer.cs`, `RynthAiPlugin.cs`, StatusAgent `Models.cs`/`StatusReader.cs`/`RunArchive.cs`.

**Found already-implemented while working the backlog (no action needed):** **M2** (backtick-aware tokenizer — `ParseAtom`/`SplitArgs` already treat backtick args as opaque) and **M5** (stopwatch verbs + `setvar`/`getvar`/`testvar`/`touchvar`/`clearvar` scratch-vars, undefined reads as `0`). The meta engine is more complete than this backlog first assumed.

**Deferred:** **M6** (`statushud[k,v]` / `chatboxpaste[str]`) — needs a HUD sink + a no-send paste primitive, more than purely additive.

> Nothing below is committed or deployed. Build = `dotnet publish` the plugin + StatusAgent, then `Deploy-RynthCore.ps1`. Each combat-path item should get its own test session.

---

## Conventions

- Each item has an ID (`D#`, `M#`, `S#`, `C#`, `L#`, `I#`), an effort/impact tag
  `[E:L/M/H · I:L/M/H]`, and a target (`ACE`, `retail`, or `both`).
- `- [ ]` = not started. Check off as landed; add the commit + "live-verified" note.
- "Done-when" is the acceptance bar, written so it's testable on a live client.

Status legend: ☐ todo · ◐ in progress · ☑ landed (unverified) · ★ live-verified

---

# Phase 1 — Diagnostics & reliability (do first)

Cheap surfaces that make our known wedges self-diagnosing.

### D1 — Per-spell `RequiresTurnTo` flag on the cast path `[E:M · I:H]` (ACE)
- [ ] **Problem.** The combat-cast-orphan root is the bot's *own* turn/stop motion
  orphaning a targeted spell's deferred windup, pinning the server's busy state
  ("too busy forever", 0 damage). Our current mitigation is a blanket facing-settle
  gate applied to *all* targeted casts — slow, and it still issues the motion.
- [ ] **Approach.** Add a `RequiresTurnTo` property to our spell model. Default it
  conservatively (targeted = needs facing), then mark the families that provably
  don't need to face the target (self/area, and the no-facing offensive/debuff
  lines, confirmed on ACE) as `false`. In `CombatManager`, when `RequiresTurnTo`
  is false, skip turn-to-face **and** the facing-settle wait entirely.
- [ ] **ACE note.** The no-turn family list must be validated empirically on ACE —
  ACE windup/turn semantics are what produced the orphan in the first place.
- **Depends on:** D4 (so a skipped turn that still fails surfaces a reason).
- **Done-when:** a no-turn debuff/war cast issues with zero turn motion and the
  busy state clears within the normal cadence on a target already in front.

### D2 — Three-tier target-count telemetry (Total / Ring / Possible) `[E:L · I:H]` (ACE)
> **Status 2026-06-29:** ☑ landed (compiles, pending live verify). Producer in `CombatManager.ScanNearbyTargets` (`LastScanTotalMonsters/InRing/Possible/LosBlocked`); pushed to the snapshot + status feed and a change-gated `[ScanTele]` per-client log line.
- [ ] **Problem.** The respawn-blind wedge was *contested* only because nothing
  showed **which filter stage** zeroed the candidate set. "Cache has objects but
  zero are attackable" looks identical to "cache is genuinely empty" from outside.
- [ ] **Approach.** Instrument the combat target scan to emit three counts every
  tick: **Total** (all creatures in the object cache), **Ring** (within engage
  distance), **Possible** (survive blacklist + IsAttackable + LOS). Push to the
  heartbeat line and the StatusAgent feed; surface in RynthRemote.
- [ ] **ACE note.** Pure counters — server-agnostic. Directly distinguishes
  over-filtering / LOS-blindness from real respawn-blindness next time.
- **Done-when:** the three numbers are visible per-tick in the heartbeat and on the
  phone, and a deliberately blacklisted mob shows `Total>0, Possible=0`.

### D3 — `/ra why` live "why is the bot idle" snapshot `[E:L · I:H]` (both)
- [ ] **Problem.** Stall diagnosis is hampered by `BotAction` being a distributed
  free-text string with ~20 writers. We can't see the *winning rule* + *what's
  holding the body*.
- [ ] **Approach.** A `/ra why` command + a StatusAgent field reporting: the
  winning arbiter rule's friendly name and priority, its want-bits
  (buff/combat/loot/salvage/nav), any held action locks, and the live busy count.
  Pipe the same struct to RynthRemote.
- [ ] **ACE note.** Most valuable on ACE where the busy/orphan wedges live, but
  useful on both.
- **Depends on:** ActivityArbiter steps 3–5 (this is the natural consumer of the
  arbiter snapshot we're already moving toward).
- **Done-when:** running `/ra why` during a stall names the rule + lock + busy
  count that explains the idle.

### D4 — Unified `CastSkipReason` enum + single `TryEvaluateCast` entry `[E:M · I:H]` (both)
- [ ] **Problem.** "Why didn't it cast?" reasons exist only as scattered ad-hoc
  log strings across the combat and buff paths. No structured *first-failing*
  reason a caller, the UI, or StatusAgent can read.
- [ ] **Approach.** Define `enum CastSkipReason { Ok, NotKnown, SkillTooLow,
  OnFailCooldown, AlreadyActive, NoComponents, Unresolvable, WrongStance,
  NotInRange, TurnPending }`. Route every cast attempt through one
  `bool TryEvaluateCast(spellId, out reason)` that sets the **first** failing
  reason cheapest-check-first. Surface the last reason per spell family in the
  panel + StatusAgent.
- [ ] **ACE note.** Axes are server-agnostic. On ACE components are free so
  `NoComponents` rarely fires, but the slot stays valid for retail.
- **Done-when:** a buff/combat cast that's skipped reports exactly one structured
  reason that matches reality, queryable from the panel.

### D5 — Tri-state cast return for meta cast verbs (begun / retry / impossible) `[E:M · I:H]` (ACE)
- [ ] **Problem.** Our meta cast verbs collapse "busy / no-op this tick" and
  "genuinely impossible" into one failure value, so polled metas spin forever on
  exactly the orphan-cast and hardlock conditions instead of giving up or retrying
  appropriately.
- [ ] **Approach.** Make `actiontrycastbyid` / `...ontarget` (and the equip-wand
  verb) return three states: **1 = dispatched**, **0 = transient/retry-next-tick**
  (wire the facing-settle gate + busy poll into this path so "too busy" is
  explicitly retryable), **2 = impossible** (unknown spell / castability fail).
  Add an "equip-any-wand" verb that returns success only when a wand is already in
  hand.
- **Depends on:** D4 (impossible ⇒ a `CastSkipReason`), D1.
- **Done-when:** a meta loop that casts on a momentarily-busy target retries and
  eventually succeeds rather than wedging, and aborts cleanly on an unknown spell.

### D6 — Casts-per-kill / attack-cast counters in the feed `[E:L · I:H]` (both)
> **Status 2026-06-29:** ☑ landed (compiles, pending live verify). `CombatManager.SessionAttackCasts`/`CastsSinceLastKill` (reset on `OnKillNotification`); `castsPerKill` in the snapshot + status feed + `runs.json` per-run record.
- [ ] **Problem.** The "animates but does 0 damage" orphan is currently invisible
  until a human notices nothing dying.
- [ ] **Approach.** Track attack casts issued vs kills; export casts-per-kill to the
  StatusAgent feed and `runs.json`. The ratio diverging toward infinity is an
  early automatic signal of the orphan.
- **Done-when:** an induced orphan makes the ratio climb visibly on the phone
  within a few seconds.

### D7 — Snap cast-FSM to Idle on active-target despawn `[E:L · I:M]` (both)
- [ ] **Problem.** When the current target despawns mid-cast we can keep casting at
  a corpse / wait out the watchdog.
- [ ] **Approach.** On our delete-object hook, if the despawned id is the active
  cast target, immediately reset the cast state machine to Idle (main-thread,
  re-verify object class before any action).
- **Done-when:** killing the target mid-windup returns the bot to target-select on
  the next tick, not after a watchdog timeout.

### D8 — Periodic validity-prune of failure/blacklist/corpse maps `[E:L · I:M]` (both)
- [ ] **Problem.** A despawn → respawn-same-GUID mob can stay stuck behind a stale
  failure/blacklist entry until the long timeout.
- [ ] **Approach.** On a periodic tick, collect-then-remove invalid-object keys
  from the failure-count, blacklist-expiry, and completed-corpse maps (don't mutate
  while enumerating). Presence-prune on validity, not just age.
- **Done-when:** a respawned mob is re-engageable well before the static timeout.

---

# Phase 2 — Meta engine: correctness & authoring

Theme: **we currently fail quiet exactly where we should fail loud.** These are the
highest correctness-value, lowest-effort fixes in the whole backlog and they protect
both our hand-written metas and the large imported-meta corpus + transpiler.

### M1 — Operator parity: add `!=`, case-insensitive string `==`/`!=` `[E:L · I:H]` (both)
- [ ] **Problem.** No `!=` operator — a condition using it silently drops and
  **fails open**, which can quietly break combat/loot gating. String `==` is
  case-sensitive ordinal, so `$x==True` is false when the variable holds `'true'`.
- [ ] **Approach.** Add `!=` (and verify it never fails open). Make string equality
  `OrdinalIgnoreCase`. While here, write down and lock our operator precedence/
  semantics table (regex-match, sequence-returns-left, int-mod, xor) so imported
  and hand-authored metas evaluate identically.
- **Done-when:** a regression meta covering `!=`, mixed-case `==`, and each operator
  evaluates to the documented truth table.

### M2 — Backtick-aware atom tokenizer `[E:L · I:H]` (both)
- [ ] **Problem.** The expression atom scanner doesn't treat backtick-quoted
  arguments as opaque, so a `]` inside a quoted string truncates the call.
- [ ] **Approach.** Make the bracket scanner backtick-aware and honor a
  backslash-escape-next-char inside quotes.
- **Done-when:** an expression with a `]` inside a backtick arg parses and runs.

### M3 — Stop swallowing unknown functions/options `[E:M · I:H]` (both)
- [ ] **Problem.** An unknown expression function hits a silent empty-string
  fallback with **no log**; unmapped imported options are silently dropped; unknown
  item names silently vanish. This is our #1 "expressions just don't work" footgun.
- [ ] **Approach.** Two steps. (a) Immediate safety net: replace the silent fallback
  with a **rate-limited ERR** that names the missing function and the offending
  source. (b) A centralized argument validator that emits uniform self-describing
  errors (function name, param index, expected type, offending text) back to the
  meta editor/log.
- **Done-when:** a meta referencing a typo'd function logs a named error instead of
  silently no-opping.

### M4 — `getitemcountininventorybyname` stale-cache + name-drop fix `[E:M · I:H]` (both)
- [ ] **Problem.** The item-count lookup reads a stale cache and silently drops
  unknown names — the documented reason the "+1 when count reaches N" idiom never
  triggers.
- [ ] **Approach.** Back the lookup with a per-frame memo keyed on game-time XOR the
  targeting flag wrapping the family resolver + item-count reads, refreshed each
  tick; surface a not-found as a logged condition (ties into M3), not a zero.
- **Done-when:** the "+1 at N" idiom fires reliably as inventory crosses the
  threshold during live looting.

### M5 — Stopwatch + scratch-var verbs for metas `[E:L · I:M]` (both)
- [ ] **Problem.** Polled-FSM metas have no clean cooldown/timeout primitive and no
  idempotent init that survives a watchdog restart.
- [ ] **Approach.** Add a stopwatch token type (`create/start/elapsedseconds`) and
  `touchvar/testvar/clearvar/clearallvars` with **undefined-reads-as-0** semantics
  so init idioms are idempotent. Needed for import fidelity too.
- **Done-when:** a meta can implement a debounced action with a stopwatch and
  re-init cleanly after a restart.

### M6 — `statushud[k,v]` + `chatboxpaste[str]` output verbs `[E:L · I:M]` (both)
- [ ] **Problem.** Metas can't surface their own state to our HUD/phone, and there's
  no safe human-in-the-loop partial-command output.
- [ ] **Approach.** `statushud[key,val]` writes a labeled value to the D3D9 HUD /
  RynthRemote; `chatboxpaste[str]` types into the chat box without sending (safe
  partial command).
- **Done-when:** a meta can publish a custom status line visible on the phone.

---

# Phase 3 — Missing subsystems

Larger builds for capabilities we don't have at all.

### S1 — `VitalRechargeManager` (threshold-driven consumable recovery) `[E:H · I:H]` (both)
- [ ] **Problem.** We have no threshold-driven heal/food/potion/kit use. Low-mana
  and non-mage builds have no automated sustain beyond spells.
- [ ] **Approach.** A recharge subsystem built on a small handler interface
  (`FriendlyName`, the vitals it serves, `bool Activate(vital)`); register handlers
  for self-heal spells, vital-transfer spells, food/potions, and healing/stam/mana
  kits. Per vital, keep an **ordered list of `{handler, minPct, maxPct, stance}`**
  walked first-match-wins, with the last entry an unconditional fallback. Prefer the
  consumable stack with the **fewest remaining uses**. Add hysteresis so we don't
  oscillate around a threshold.
- [ ] **CRITICAL (our scar tissue).** Using an item from inventory must
  save-current-selection → select-self/item → use → **restore selection**, all
  marshaled through `AcMainThreadQueue` — the off-thread `SelectItem` UAF is
  documented and must not be reintroduced.
- [ ] **ACE note.** `UseItem` works on both; item ids and use-times need ACE tuning.
- **Depends on:** I1 (its idle path belongs in the concurrent lane).
- **Done-when:** HP/Stam/Mana below their thresholds trigger the correct consumable
  with selection correctly restored, no off-thread access, on a live client.

### S2 — In-game peer relay + `CoordinationManager` `[E:H · I:H]` (ACE)
- [ ] **Problem.** We have zero **in-game** coordination between our own boxes —
  StatusAgent is out-of-game/external. Shared-target focus-fire and follow-the-puller
  are our biggest untapped multi-box DPS/QoL wins. (The dead RynthCore2.Coord work is
  reference-only.)
- [ ] **Approach.** A relay abstraction in the PluginSdk —
  `SendMessage(charName, pluginId:byte, msgId:byte, payload:byte[])` +
  a received-callback — backed by a first-success-wins provider chain. Two providers:
  (1) an in-game chat-tunnel over our existing chat in/out hooks; (2) a StatusAgent-WS
  provider. On top, a `CoordinationManager`: leader broadcasts target GUID →
  followers select+attack; follow-leader from the pose tick; buff-request;
  corpse/loot claim by GUID — all **fail-open to solo**.
- [ ] **Security.** Stamp sender identity, allowlist our own roster, HMAC the
  chat-tunnel payload (multi-box chat-spoofing is a real risk in our environment).
- [ ] **Lifecycle.** Ship `Reset()` for hot-reload teardown from day one (see I2).
- [ ] **ACE/retail.** Our fleet is the use case; on retail prefer the WS provider to
  avoid contending on chat channels.
- **Done-when:** two of our clients focus-fire a leader-broadcast target and a
  follower trails the leader, degrading to solo when the peer is absent.

### S3 — Tell-driven command surface + fellowship governance `[E:H · I:H]` (both, ACE-first)
- [ ] **Problem.** We have **no inbound-tell command intake** (FellowshipTracker is
  read-only) and **no fellowship-write GameActions**. This blocks any chat-driven
  control and any fellowship auto-host.
- [ ] **Prerequisite.** Reverse the four privileged fellowship GameActions
  (recruit / dismiss / give-leader / set-open) from ACE source and wrap them
  mirroring our existing Trade/Inventory GameAction pattern; gate on `IsLeader`.
- [ ] **Approach.** A `TellCommandRouter` on the inbound chat hook: channel-gated,
  named-group regex `(name, body)` — derive the ACE inbound-tell envelope from a
  **live capture**, not an assumed format. Data-driven keyword vocabulary + help.
  A per-identity rate limiter before any verb executes. Then an optional fellowship
  queue/reservations layer. An edge-triggered leadership-transition watcher purges
  pending state when we lose authority; drive expiry/reconcile from a periodic tick
  so it survives a dropped event.
- [ ] **CRITICAL.** Route all FellowshipTracker reads through the main-thread queue
  — the off-thread raw-VA read is the top finding in `DeepAudit_2026-06-18.md` and
  must be fixed as part of this work, not layered on top of it.
- **Done-when:** a whitelisted tell triggers a rate-limited, leader-gated fellowship
  action on a live client, with fellowship reads marshaled on-thread.

---

# Phase 4 — Combat depth

### C1 — Concurrent "always lane" separate from the single-winner action lane `[E:M · I:H]` (both)
> Placed at the top of combat-depth because it's the **structural fix** for a class
> of starvation bugs (e.g. "salvage starves combat", the loot busy-leak) and the
> home for S1's idle path.
- [ ] **Problem.** Vital monitoring, mana-charge, pet-refill, and status-export are
  wedged into the same linear cascade as combat/nav/loot, so a maintenance action
  can starve the body-owning action.
- [ ] **Approach.** Split scheduling into an **arbitrated lane** (exactly one winner
  owns the body — move/cast/loot/nav/salvage-combine) and an **always-lane** whose
  rules just set "I want to run" each tick with no arbitration (vital monitor, status
  push, mana-charge-when-idle, pet refill). Add a macro-OFF maintenance lane so idle
  upkeep isn't dead code when the macro is paused.
- **Depends on:** ActivityArbiter steps 3–5.
- **Done-when:** a salvage/mana-charge cycle no longer blocks a combat tick; both
  observably progress in the same window.

### C2 — `WeaponProfileStore` cold-start prior + slayer/element awareness `[E:M · I:H]` (both)
- [ ] **Problem.** Weapon choice is purely empirical fewest-casts-to-kill, so an
  un-fought weapon scores 0 and is invisible until used, and **slayer-species — the
  single strongest signal — is never read**.
- [ ] **Approach.** A property profile per weapon
  (`{Imbued, CleaveType, SlayerSpecies, EquipSkill, DamageType, AssociatedSpell,
  TwoHand, Multistrike}`), lazily populated behind a `HasIdData` gate (mirror the
  qualities cold-start), invalidated on item change. A `GetEffectiveWeapon` ladder:
  manual override → slayer match (`SlayerSpecies == CreatureType`) →
  weapon-element-vs-target-vuln → learned-best (`MonsterDamageStore`) → default →
  any-usable, each tier logged. A static imbue/element weight table is **only** a
  tiebreaker when the empirical store has too few samples. Skip weapons whose equip
  skill is untrained.
- [ ] **ACE note.** Verify ACE actually populates slayer/imbue bits against ACE
  source before trusting them (retail flag values can differ on ACE).
- **Done-when:** a brand-new slayer weapon is chosen on first encounter with the
  matching species, and the choice + tier is logged.

### C3 — Per-target debuff expiry tracking + refresh `[E:M · I:H]` (ACE)
- [ ] **Problem.** Debuff tracking is effectively one-shot: once applied it isn't
  re-evaluated until the target changes, so on long ACE fights Imperil/Vuln lapse and
  never reapply, and toggling target re-debuffs from scratch.
- [ ] **Approach.** Track a per-`(target, debuffKey)` expiry timestamp — prefer a
  live ACE enchantment-registry remaining-time read; fall back to a duration table.
  Re-mark a debuff "needed" when remaining ≤ `DebuffPrecastSeconds`, with per-line
  overrides for DoT/curse lines that refresh only when fully expired. Add a pre-cast
  range gate (`distanceUnits/240.0 < spellRange`) so out-of-range debuffs skip
  instead of wasting a windup.
- [ ] **ACE note.** Duration source must be ACE-verified (prefer the registry read).
- **Done-when:** a debuff reapplies before expiry on a sustained fight and an
  out-of-range target is skipped without a wasted cast.

### C4 — Ghost/stale-object culler (dual signal) `[E:M · I:M]` (both)
- [ ] **Problem.** The client can keep rendering server objects that no longer exist,
  poisoning the target set (a contributing suspect in the respawn-blind wedge).
- [ ] **Approach.** Cull on a **dual signal** — repeated failed-cast count against an
  object AND HP-tracker staleness — routed through our already-hooked object-maint
  delete path. Re-verify `ObjectClass == Monster` on the main thread immediately
  before deletion.
- [ ] **Caution.** This deletes client-side objects; gate behind a setting,
  default conservative, and log every deletion. Validate it doesn't fight real
  respawns on ACE.
- **Done-when:** a provably-dead ghost is removed without removing live mobs over a
  long session.

---

# Phase 5 — Loot & infra hygiene

### L1 — Two-phase loot with ID-gating + rule attribution `[E:M · I:H]` (both)
- [ ] **Problem.** Appraisal is rate-limited and latency-bearing; appraising every
  item slows fast looting. And "why did it loot/skip this?" isn't answerable.
- [ ] **Approach.** Add a `DoesItemNeedID(item)` pre-check: run the loot profile
  against the un-appraised snapshot first; only enqueue an appraisal when the verdict
  is genuinely ambiguous, then re-run on ID return. Stamp the deciding rule name onto
  the verdict and log it ("Kept X by rule: Charges") at DBG in the per-client log.
- **Done-when:** items decidable from base properties are kept/skipped with no
  appraisal, and the log names the rule for each.

### I1 — `ViewRegistry.DestroyAll` + relay/registry `Reset()` on teardown `[E:M · I:H]` (both)
- [ ] **Problem.** The documented hot-reload leak caps reloads at ~7 before VA
  exhaustion → AC OOM; each reload leaks engine + plugin runtimes. Any **new**
  registry (relay providers, capability handlers, floating panels) that doesn't clear
  on teardown joins that leak class.
- [ ] **Approach.** A `ViewRegistry` that tracks every floating panel/overlay window
  and `DestroyAll()` on plugin teardown + hot-reload; a `MessageRelay.Reset()` that
  clears providers **and** inbound callbacks, wired into `EngineLifecycle` teardown.
  Add a soak assertion that handle/registry counts return to baseline after a reload.
- [ ] **Gate for new work:** S2's relay and any new panel MUST register here on day
  one.
- **Done-when:** N reloads in a row hold registry counts flat at baseline.

---

# Quick-wins checklist (small, independent, mostly low-effort)

- [ ] `ExtraBuffSpells` / `AntiExtraBuffSpells` force/deny lists + a buff-exclude
      family list — per-char buff trimming without code (helps when the auto-resolver
      mis-picks on ACE). `[E:L · I:M]`
- [ ] Element-letter-string protection/bane profiles — stop attempting all 7
      prots/banes on every char, cutting no-show cooldown churn. `[E:L · I:M]`
- [ ] Pre-check the armor piece is equipped before an item-enchant cast (+ a
      "-1 = wielded weapon" sentinel) — removes the post-death Impen cast loop.
      `[E:L · I:M]`
- [ ] A `SkillToSelfBuff` mapping table (incl. the Invuln/Impreg quirks) — let users
      say "keep War Magic buffed" instead of naming the exact spell. `[E:L · I:M]`
- [ ] Hoist meta `Think()` to a single guarded pre-pass at the top of `OnTick` —
      removes the scattered call sites and the "meta skipped this frame"
      nondeterminism. `[E:L · I:M]`
- [ ] A per-vital "no-target" recharge tier between the in-combat heal threshold and
      the idle top-off — distinct self-recovery aggressiveness while running with no
      target. `[E:L · I:M]` (folds into S1)
- [ ] Anchored full-line chat matches gated on chat-message-type — defeats multi-box
      chat-spoofing of kill/resist lines. `[E:L · I:M]` (prereq for any chat-driven
      logic, incl. S2/S3)
- [ ] Honor-current-weapon short-circuit to skip redundant swaps — reduces the
      busy-churn the orphan is sensitive to. `[E:L · I:M]`

---

# Secondary / also-consider (revisit after Phases 1–3)

- [ ] ACE-sourced 4-bucket spell-feedback chat router (retry / terminal-unattackable /
      landed / killed) returning a `CastOutcome` the combat + buff paths consume.
      Re-derive **all** wording from ACE server source; keep the authoritative kill
      event as the kill source; on ACE the damage line is the primary "landed"
      trigger since war magic is otherwise silent. `[E:H · I:H]` — high value but
      high effort; D6's counter is the cheap early-warning stand-in until then.
- [ ] Centralized `ItemSnapshot` with a `KeyExists()` that distinguishes "absent" vs
      "present-but-zero" — kills the qualities-gate-tail bad-default poisoning at the
      source. `[E:H · I:H]` (the `KeyExists` primitive alone is cheap and worth doing
      early).
- [ ] Species-default fallback layer under `CreatureProfileStore` (creature-type
      aggregate cold-start seed) when name|wcid misses.
- [ ] Embedded ACE-tuned seed creature DB so a fresh install isn't combat-blind on
      first login; optional bulk-appraisal sweep of known creatures on bot-enable
      (rate-limited for multi-box).
- [ ] Cooperative ref-counted busy increment/decrement + named time-boxed action
      locks (distinct from the AC `m_cBusy` watchdog) for clean action serialization.
- [ ] Capability-gated in-proc inter-plugin API surface (permission flags) — the
      stable seam other RynthCore-native plugins call into.
- [ ] Typed waypoint kinds (Portal / UseNPC / OpenVendor / Pause / Jump) + a
      follow-route mode — already on the nav radar; validate every target on ACE.
- [ ] Per-phase timing probes with budgets in the heartbeat — names the slow rule
      when the bot is laggy.
- [ ] Forced-rebuff reversible stomp (keep real expiry alongside the compare expiry)
      so an interrupted forcebuff reverts instead of clearing all timers.
- [ ] Boxed-object + first-class coordinate (240.0 frame) token types for metas —
      unlocks distance-gating in imported metas.

---

# Explicitly out of scope / deferred

- **Any retail-calibrated constant** (fellowship size caps, school lockout
  magnitudes, heal/resist magnitude tables) — re-derive on ACE, never assume.
- Self-damage spell awareness (HP-cost gate), missile ammo-availability gate, and
  "use N times" item specifiers — niche/build-dependent; only if such builds enter
  rotation.
- Mana-stone/mana-tank loot dispositions — already covered by our ManaStoneManager.
- A full attribute/source-gen expr-function registration refactor — the rate-limited
  unknown-fn log (M3) is the cheap 80%; defer the AOT-friendly registry rework.

---

# Invariants these changes must NOT regress

These are existing strengths; every item above must preserve them.

1. **Off-thread AC discipline** — all AC reads/mutations marshal via
   `AcMainThreadQueue` / gate on `MainThreadGuard`; re-fetch-and-verify before any
   destructive op; `IsWandObject()` not a raw `ObjectClass` switch. (S1/S3 are the
   highest-risk here.)
2. **Predictive war-magic selection** — known ∧ skill-tier-valid ∧ strict spell
   shape, motion-end cadence, no component gate. New combat work feeds this, doesn't
   replace it.
3. **Empirical per-wcid weapon learning** — C2 *seeds* it with a prior; it must not
   override a confident learned-best.
4. **Armor enchants as per-item timers**, gated `!IsArmorEnchantment`, never unified
   with the player-registry buff path.
5. **Real-`m_cBusy` busy-wedge watchdog + `/ra clearbusy`** — D1/D4/D5 layer on top;
   don't reintroduce a single shadow bool as the source of truth.
6. **External telemetry stays opt-in and local-only** at the engine
   (`EnableStatusExport`); only the StatusAgent networks. D2/D3/D6 add fields to the
   existing local export, not new engine networking.

---

# Suggested execution order

1. **Phase 1 in full** (D1–D8) — small, high-leverage, makes everything else
   debuggable. D2/D3/D6 first (pure diagnostics, zero behaviour risk), then D1/D4/D5
   (the orphan cluster), then D7/D8.
2. **M1–M4** — tiny diffs, large correctness win, protect the meta corpus.
3. **C1 (concurrent lane)** — unlocks S1 and removes a starvation class; pairs with
   finishing ActivityArbiter steps 3–5.
4. **S1 (recharge)** on top of C1; **C2/C3** combat depth in parallel.
5. **I1** before, not after, **S2** — so the new relay can't leak.
6. **S2 / S3** — the big multi-box + control features, once the diagnostics from
   Phase 1 make their failure modes visible.

> One item per test session where it touches the live combat/cast path — our history
> says bundling combat-path changes hides which one regressed.
