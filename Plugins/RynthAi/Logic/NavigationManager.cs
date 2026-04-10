using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using AcClient;

namespace NexSuite.Plugins.RynthAi
{
    /// <summary>
    /// VTank-faithful navigation engine.
    ///
    /// ARCHITECTURE:
    ///   Forward motion: SetAutorun(true/false) via Decal API.
    ///     - Player can toggle autorun off manually for immediate control.
    ///     - Re-asserted every 500ms as heartbeat.
    ///
    ///   Steering while running: SetClientMotion(TurnRight/TurnLeft) via
    ///     AcClient command interpreter. These combine with autorun naturally
    ///     (like pressing W+A or W+D) without interrupting forward motion.
    ///
    ///   Large turns (>60°): Stop autorun, snap Actions.Heading while
    ///     stopped, resume autorun once error is below 20°.
    ///
    ///   Closest-approach detection prevents circling waypoints.
    /// </summary>
    public class NavigationManager
    {
        private readonly CoreManager _core;
        private readonly UISettings  _settings;
        private readonly PluginHost  _host;

        // ── Motion command constants (turns only — forward uses SetAutorun) ────
        private const uint MOTION_TURNRIGHT = 0x6500000D;
        private const uint MOTION_TURNLEFT  = 0x6500000E;

        // ── Nav tick ────────────────────────────────────────────────────────────
        private DateTime _lastNavTick = DateTime.MinValue;
        private const double NAV_TICK_MS = 33.0;  // ~30 Hz — smoother turn toggling

        // ── Stop debounce ───────────────────────────────────────────────────────
        private DateTime _stopRequestedAt = DateTime.MaxValue;
        private const double STOP_DEBOUNCE_MS = 300.0;

        // ── Movement state ──────────────────────────────────────────────────────
        private bool _isMovingForward = false;
        private bool _hasStopped = true;
        private bool _turnsStopped = true;
        public bool DebugNav = false;
        private DateTime _lastDebugPrint = DateTime.MinValue;
        private double _lastGoodHeading = 0;
        private bool _hasGoodHeading = false;  // true = we've already stopped, don't repeat

        // ── W tap heartbeat — periodic safety tap ───────────────────────────────
        private DateTime _lastWTap = DateTime.MinValue;
        private const double W_HEARTBEAT_MS = 500.0;

        // ── Turning state ───────────────────────────────────────────────────────
        private bool _isTurning = false;

        // ── Heading thresholds ──────────────────────────────────────────────────
        private const double DEAD_ZONE         = 4.0;   // wider for indoor stability — frequent small corrections = smooth
        private const double BIG_TURN_ENTER    = 20.0;  // stop and turn above this
        private const double BIG_TURN_EXIT     = 10.0;  // resume running below this

        // ── Arrival / lookahead ─────────────────────────────────────────────────
        private double ArrivalYards   => Math.Max(1.5, _settings.FollowNavMin);
        private double LookaheadYards => ArrivalYards * 0.1;

        // ── Closest approach tracking ───────────────────────────────────────────
        // Detects when we've passed a waypoint even if we never entered the
        // arrival radius (turn radius at speed is too wide). If distance was
        // decreasing and starts increasing, we've swept past — advance.
        private double _prevDist = double.MaxValue;

        // ── Route bookkeeping ───────────────────────────────────────────────────
        private int      _linearDir    = 1;
        private bool     _inPause      = false;
        private DateTime _pauseUntil   = DateTime.MinValue;
        private bool     _actionFired  = false;
        private DateTime _actionExpiry = DateTime.MinValue;
        private const double ACTION_TIMEOUT_MS = 10000.0;

        // ── Portal/Recall state machine ─────────────────────────────────────────
        private enum PortalState
        {
            None,
            Settling,
            EquippingWand,
            EnteringMagicMode,
            CastingSpell,
            WaitingForPortalOrTeleport,
            WaitingForPortalExit,
            PostTeleportSettle    // Brief pause after teleporting to cancel UseItem effects
        }
        private PortalState _portalState = PortalState.None;
        private DateTime _portalStateStart = DateTime.MinValue;
        private int _preCastLandblock = 0;
        private int _prePortalLandblock = 0;
        private double _prePortalNS = double.NaN;
        private double _prePortalEW = double.NaN;
        private const double PORTAL_STATE_TIMEOUT_MS = 15000.0;
        private const double SETTLE_DELAY_MS = 600.0;
        private const double WAND_EQUIP_DELAY_MS = 1000.0;
        private const double MAGIC_MODE_DELAY_MS = 800.0;
        private const double CAST_DELAY_MS = 500.0;
        private const double PORTAL_WAIT_MS = 3000.0;

        // ── Stuck watchdog ──────────────────────────────────────────────────────
        private double   _watchdogNS    = double.NaN;
        private double   _watchdogEW    = double.NaN;
        private DateTime _watchdogNext  = DateTime.MinValue;
        private int      _stuckCount    = 0;
        private bool     _inRecovery    = false;
        private DateTime _recoveryUntil = DateTime.MinValue;
        private const double WATCHDOG_MS  = 5000.0;
        private const double STUCK_YD     = 2.0;
        private const double RECOVERY_MS  = 1200.0;

        // ── Tier 2 state (MoveToPosition via MovementManager) ─────────────────
        private int  _tier2TargetIdx = -1;         // waypoint index we last issued MoveToPosition for
        private bool _tier2Active    = false;       // true while a Tier2 MoveToPosition is in flight
        private DateTime _tier2IssuedAt = DateTime.MinValue;
        private const double TIER2_REISSUE_MS = 2000.0;  // re-issue if no progress for this long
        private const double TIER2_ARRIVAL_YD = 1.5;     // arrival radius (yards)

        // ── Win32 (focus detection only) ────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        public bool IsReversed { get; set; } = false;
        public void SetReverse(bool reverse) { IsReversed = reverse; }

        private IntPtr _gameHwnd = IntPtr.Zero;
        private IntPtr GameHwnd
        {
            get
            {
                if (_gameHwnd == IntPtr.Zero)
                    _gameHwnd = Process.GetCurrentProcess().MainWindowHandle;
                return _gameHwnd;
            }
        }

        private bool IsGameFocused() => GetForegroundWindow() == GameHwnd;

        public NavigationManager(CoreManager core, UISettings settings, PluginHost host)
        {
            _core     = core;
            _settings = settings;
            _host     = host;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  CLIENT MOTION + W TAP
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Direct client memory motion — same as UB's setmotion.
        /// Used ONCE to establish forward motion state.
        /// </summary>
        private unsafe void SetClientMotion(uint motion, bool on)
        {
            try
            {
                ((ACCmdInterp*)(*SmartBox.smartbox)->cmdinterp)->SetMotion(motion, on);
            }
            catch { }
        }

        private void StartForward()
        {
            if (!_isMovingForward)
            {
                try { _core.Actions.SetAutorun(true); } catch { }
                _isMovingForward = true;
            }
        }

        private void StopForward()
        {
            try { _core.Actions.SetAutorun(false); } catch { }
            _isMovingForward = false;
        }

        private void StopMovement()
        {
            _isTurning = false;
            StopForward();
            ClearTurnMotions();
            CancelTier2();
        }

        private void CancelTier2()
        {
            if (_tier2Active)
            {
                try { Tier2MovementHelper.CancelMoveTo(); } catch { }
                _tier2Active = false;
            }
            _tier2TargetIdx = -1;
        }

        private void ClearTurnMotions()
        {
            SetClientMotion(MOTION_TURNRIGHT, false);
            SetClientMotion(MOTION_TURNLEFT, false);
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  PUBLIC ENTRY POINT
        // ══════════════════════════════════════════════════════════════════════════

        public void ProcessNavigation(bool isFighting)
        {
            // The priority dispatcher in PluginCore already controls call order.
            // Nav only needs to check: macro running, nav enabled, and state is available.
            // "Idle" means no higher-priority system claimed this frame.
            // "Navigating" means WE claimed it last frame and are still going.
            bool shouldNav = _settings.IsMacroRunning
                        && _settings.EnableNavigation
                        && (_settings.CurrentState == "Idle" || _settings.CurrentState == "Navigating");

            if (!shouldNav)
            {
                if (!_hasStopped && !_turnsStopped)
                { try { ClearTurnMotions(); } catch { } _turnsStopped = true; }
                if (_stopRequestedAt == DateTime.MaxValue)
                    _stopRequestedAt = DateTime.Now;
                if ((DateTime.Now - _stopRequestedAt).TotalMilliseconds >= STOP_DEBOUNCE_MS)
                {
                    if (!_hasStopped)
                    {
                        CancelTier2();
                        try { _core.Actions.SetAutorun(false); } catch { }
                        _isMovingForward = false;
                        _isTurning = false;
                        _hasStopped = true;
                    }
                }
                return;
            }
            _stopRequestedAt = DateTime.MaxValue;
            _hasStopped = false;
            _turnsStopped = false;

            var route = _settings.CurrentRoute;
            if (route == null || route.Points.Count == 0) { StopMovement(); return; }

            // Claim the traffic light — we are actively navigating
            _settings.CurrentState = "Navigating";

            // Rate limit to ~15 Hz
            if ((DateTime.Now - _lastNavTick).TotalMilliseconds < NAV_TICK_MS) return;
            _lastNavTick = DateTime.Now;

            int idx = _settings.ActiveNavIndex;
            if (!IndexValid(idx, route)) { HandleRouteEnd(route); return; }

            UpdateWatchdog();
            if (_inRecovery)
            {
                if (DateTime.Now >= _recoveryUntil) _inRecovery = false;
                return;
            }

            var pt = route.Points[idx];

            // ── Pause ───────────────────────────────────────────────────────────
            if (pt.Type == NavPointType.Pause)
            {
                StopMovement();
                if (!_inPause) { _inPause = true; _pauseUntil = DateTime.Now.AddMilliseconds(pt.PauseTimeMs); }
                if (DateTime.Now >= _pauseUntil) { _inPause = false; Advance(route); }
                return;
            }

            // ── Chat ────────────────────────────────────────────────────────────
            if (pt.Type == NavPointType.Chat)
            {
                StopMovement();
                if (!_actionFired)
                {
                    _actionFired = true;
                    try { _core.Actions.InvokeChatParser(pt.ChatCommand); } catch { }
                    Advance(route);
                }
                return;
            }

            // ── Recall / PortalNPC ──────────────────────────────────────────────
            if (pt.Type == NavPointType.Recall || pt.Type == NavPointType.PortalNPC)
            {
                if (_portalState == PortalState.None) StopMovement();
                if (DebugNav && (DateTime.Now - _lastDebugPrint).TotalMilliseconds > 1000)
                {
                    _lastDebugPrint = DateTime.Now;
                    try { _host.Actions.AddChatText($"[NavDbg] PORTAL state={_portalState} elapsed={(DateTime.Now - _portalStateStart).TotalMilliseconds:F0}ms", 1); } catch { }
                }
                ProcessPortalAction(pt, route);
                return;
            }

            // ── Standard coordinate waypoint ────────────────────────────────────
            _actionFired = false;
            _inPause     = false;

            var pos = GetPos();
            if (pos == null) return;

            double dNS  = pt.NS - pos.NorthSouth;
            double dEW  = pt.EW - pos.EastWest;
            double dist = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;

            if (dist < ArrivalYards)
            {
                _prevDist = double.MaxValue;
                Advance(route);
                return;
            }

            // ── Tier 2: delegate to client physics engine ────────────────────
            // The client's MovementManager handles interpolation, heading, and
            // speed internally — no frame-by-frame PD-controller needed.
            // Falls through to Tier 0/1 steering if not mode 2, not initialized,
            // or player is indoors (coordinate conversion is outdoor-only).
            if (_settings.MovementMode == 2 && Tier2MovementHelper.IsInitialized
                && (Tier2MovementHelper.GetPlayerCellId() & 0xFFFF) < 0x100)
            {
                ProcessTier2Waypoint(idx, pt, route, pos, dist);
                return;
            }

            // ── Closest approach detection (Tier 0/1 only) ───────────────────
            // If we were getting closer (within a reasonable range) and now
            // we're moving away, we've swept past the waypoint. Advance.
            // This handles the case where the turn radius at speed prevents
            // the character from entering the arrival radius exactly.
            if (_prevDist < ArrivalYards * 2.5 && dist > _prevDist + 0.3)
            {
                _prevDist = double.MaxValue;
                Advance(route);
                return;
            }
            _prevDist = dist;

            // ── Lookahead blend ─────────────────────────────────────────────────
            double tNS = pt.NS, tEW = pt.EW;
            if (dist < LookaheadYards)
            {
                int ni = PeekNext(idx, route);
                if (ni >= 0)
                {
                    var np = route.Points[ni];
                    double t = 1.0 - (dist / LookaheadYards);
                    tNS = Lerp(pt.NS, np.NS, t);
                    tEW = Lerp(pt.EW, np.EW, t);
                }
            }

            // ── Compute heading error ───────────────────────────────────────────
            double desiredDeg = Math.Atan2(tEW - pos.EastWest, tNS - pos.NorthSouth) * (180.0 / Math.PI);
            if (desiredDeg < 0) desiredDeg += 360.0;

            double rawHeading = 0;
            try { rawHeading = _core.Actions.Heading; } catch { }
            double currentDeg;
            if (rawHeading != 0.0)
            { currentDeg = rawHeading; _lastGoodHeading = rawHeading; _hasGoodHeading = true; }
            else if (_hasGoodHeading)
            { currentDeg = _lastGoodHeading; }
            else
            { currentDeg = desiredDeg; }
            double error = NormalizeAngle(desiredDeg - currentDeg);
            double absError = Math.Abs(error);
            if (DebugNav && (DateTime.Now - _lastDebugPrint).TotalMilliseconds > 1000)
            {
                _lastDebugPrint = DateTime.Now;
                string turnDir = absError < DEAD_ZONE ? "NONE" : (error > 0 ? "RIGHT" : "LEFT");
                double ox = 0, oy = 0;
                try { var me = _core.WorldFilter[_core.CharacterFilter.Id]; if (me != null) { var o = me.Offset(); ox = o.X; oy = o.Y; } } catch { }
                try { _host.Actions.AddChatText($"[NavDbg] raw={rawHeading:F1} cur={currentDeg:F1} des={desiredDeg:F1} err={error:F1} turn={turnDir} dist={dist:F1}yd oX={ox:F2} oY={oy:F2}", 1); } catch { }
            }

            // ═════════════════════════════════════════════════════════════════════
            //  STEERING
            // ═════════════════════════════════════════════════════════════════════

            // ── STATE: Turning in place (big turn while stopped) ─────────────────
            if (_isTurning)
            {
                if (absError > BIG_TURN_EXIT)
                {
                    try { _core.Actions.Heading = (float)desiredDeg; } catch { }
                    return;
                }

                // Close enough — clear turn keys, start running
                _isTurning = false;
                ClearTurnMotions();
                StartForward();
                return;
            }

            // ── BIG TURN (>60°): stop and face ──────────────────────────────────
            if (absError > BIG_TURN_ENTER)
            {
                StopForward();
                ClearTurnMotions();
                try { _core.Actions.Heading = (float)desiredDeg; } catch { }
                _isTurning = true;
                return;
            }

            // ── Ensure we're moving forward ─────────────────────────────────────
            StartForward();

            // ── Near waypoint: stop turning, just run straight ───────────────────
            bool closeToWaypoint = dist < ArrivalYards * 3.0;

            // ── SMALL CORRECTION: use TurnRight/TurnLeft while running ──────────
            // These combine with Forward at the command interpreter level — 
            // the character turns while running naturally, like pressing W+A or W+D.
            // No Actions.Heading, no motion interrupt, no W tap needed.
            if (absError > DEAD_ZONE && !closeToWaypoint)
            {
                if (error > 0)
                {
                    // Need to turn right
                    SetClientMotion(MOTION_TURNRIGHT, true);
                    SetClientMotion(MOTION_TURNLEFT, false);
                }
                else
                {
                    // Need to turn left
                    SetClientMotion(MOTION_TURNLEFT, true);
                    SetClientMotion(MOTION_TURNRIGHT, false);
                }
            }
            else
            {
                // On course or close to waypoint — stop turning
                ClearTurnMotions();
            }

            // ── HEARTBEAT: re-assert autorun periodically as safety net ─────────
            if (_isMovingForward && (DateTime.Now - _lastWTap).TotalMilliseconds > W_HEARTBEAT_MS)
            {
                try { _core.Actions.SetAutorun(true); } catch { }
                _lastWTap = DateTime.Now;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  ROUTE ADVANCEMENT
        // ══════════════════════════════════════════════════════════════════════════

        private void Advance(VTankNavParser route)
        {
            _actionFired = false;
            _prevDist = double.MaxValue;
            switch (route.RouteType)
            {
                case NavRouteType.Circular:
                    _settings.ActiveNavIndex = (_settings.ActiveNavIndex + 1) % route.Points.Count;
                    break;
                case NavRouteType.Linear:
                    int n = _settings.ActiveNavIndex + _linearDir;
                    if (n < 0 || n >= route.Points.Count) { _linearDir = -_linearDir; n = _settings.ActiveNavIndex + _linearDir; }
                    _settings.ActiveNavIndex = n;
                    break;
                case NavRouteType.Once:
                    _settings.ActiveNavIndex++;
                    if (_settings.ActiveNavIndex >= route.Points.Count) { _settings.EnableNavigation = false; StopMovement(); }
                    break;
                case NavRouteType.Follow:
                    _settings.ActiveNavIndex = 0;
                    break;
            }
        }

        private void HandleRouteEnd(VTankNavParser route)
        {
            if (route.RouteType == NavRouteType.Circular) _settings.ActiveNavIndex = 0;
            else StopMovement();
        }

        private int PeekNext(int cur, VTankNavParser route)
        {
            switch (route.RouteType)
            {
                case NavRouteType.Circular: return (cur + 1) % route.Points.Count;
                case NavRouteType.Linear:   int n = cur + _linearDir; return (n >= 0 && n < route.Points.Count) ? n : -1;
                case NavRouteType.Once:     return (cur + 1 < route.Points.Count) ? cur + 1 : -1;
                default:                    return -1;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  TIER 2 MOVEMENT (CPhysicsObj::MoveToPosition via MovementManager)
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tier 2 waypoint processor — issues a single MoveToPosition call to the
        /// client's physics engine, then lets it handle all interpolation and heading.
        /// Re-issues the call on waypoint change or if no progress after timeout.
        /// </summary>
        private void ProcessTier2Waypoint(
            int idx, NavPoint pt, VTankNavParser route,
            CoordsObject pos, double distYards)
        {
            // ── Closest approach detection (shared with Tier 0/1) ────────────
            if (_prevDist < ArrivalYards * 2.5 && distYards > _prevDist + 0.3)
            {
                _prevDist = double.MaxValue;
                CancelTier2();
                Advance(route);
                return;
            }
            _prevDist = distYards;

            // ── Decide whether to issue / re-issue the MoveToPosition call ───
            bool needsIssue = false;

            if (idx != _tier2TargetIdx)
            {
                // Waypoint changed — cancel old movement, issue new
                CancelTier2();
                needsIssue = true;
            }
            else if (_tier2Active &&
                     (DateTime.Now - _tier2IssuedAt).TotalMilliseconds > TIER2_REISSUE_MS)
            {
                // Timeout — re-issue in case the client gave up
                CancelTier2();
                needsIssue = true;
            }
            else if (!_tier2Active)
            {
                needsIssue = true;
            }

            if (needsIssue)
            {
                IssueTier2Move(pt, pos);
                _tier2TargetIdx = idx;
            }

            if (DebugNav && (DateTime.Now - _lastDebugPrint).TotalMilliseconds > 1000)
            {
                _lastDebugPrint = DateTime.Now;
                try
                {
                    _host.Actions.AddChatText(
                        $"[NavDbg] T2 idx={idx} dist={distYards:F1}yd active={_tier2Active} " +
                        $"age={(DateTime.Now - _tier2IssuedAt).TotalMilliseconds:F0}ms", 1);
                }
                catch { }
            }
        }

        /// <summary>
        /// Converts Decal NS/EW to internal AC coordinates and calls MoveToPosition.
        /// Works by adding the yard-delta to the player's known-good internal position,
        /// then normalizing cell/landblock boundaries with Math.Floor (critical for
        /// negative deltas — (int) rounds toward zero, not down, producing garbage cells).
        /// </summary>
        private void IssueTier2Move(NavPoint pt, CoordsObject decalPos)
        {
            try
            {
                // Read player's internal position for coordinate reference
                uint playerCell;
                float playerX, playerY, playerZ;
                if (!Tier2MovementHelper.GetPlayerPosition(
                        out playerCell, out playerX, out playerY, out playerZ))
                    return;

                // Indoor cells (objcell_id & 0xFFFF >= 0x100) — fall back to Tier 0/1
                if ((playerCell & 0xFFFF) >= 0x100)
                    return;

                // ── AC cell ID format: 0xEENNcccc ────────────────────────────
                //   EE (bits 24-31) = EW landblock index (increases east)
                //   NN (bits 16-23) = NS landblock index (increases north)
                //   cccc = outdoor cell 1-64: (rowNS * 8 + colEW + 1)
                //   Internal Position.Frame: x = EW, y = NS
                //   Each landblock = 8×8 cells, each cell = 24×24 yards

                // ── Delta in yards from Decal coordinates ────────────────────
                // Decal: 1 unit = 240 yards.  EW→x, NS→y.
                double deltaEW = (pt.EW - decalPos.EastWest) * 240.0;
                double deltaNS = (pt.NS - decalPos.NorthSouth) * 240.0;

                // ── Safety: clamp to 500 yards max ───────────────────────────
                // Beyond ~2 landblocks the target cell is likely unloaded.
                double deltaDist = Math.Sqrt(deltaEW * deltaEW + deltaNS * deltaNS);
                if (deltaDist > 500.0)
                {
                    // Scale delta to 500 yards — we'll re-issue when closer
                    double scale = 500.0 / deltaDist;
                    deltaEW *= scale;
                    deltaNS *= scale;
                }

                // ── Compute target global position from player's known-good pos ─
                int pLbEW = (int)((playerCell >> 24) & 0xFF);
                int pLbNS = (int)((playerCell >> 16) & 0xFF);
                int pCellIdx = (int)(playerCell & 0xFFFF) - 1;
                if (pCellIdx < 0 || pCellIdx >= 64) return;  // invalid outdoor cell
                int pColEW = pCellIdx % 8;
                int pRowNS = pCellIdx / 8;

                double globalEW = pLbEW * 192.0 + pColEW * 24.0 + playerX + deltaEW;
                double globalNS = pLbNS * 192.0 + pRowNS * 24.0 + playerY + deltaNS;

                // ── Decompose to landblock + cell + local ────────────────────
                // Math.Floor is critical — (int) truncates toward zero, which
                // produces wrong results for negative coordinates.
                int tLbEW = (int)Math.Floor(globalEW / 192.0);
                int tLbNS = (int)Math.Floor(globalNS / 192.0);
                tLbEW = Math.Max(0, Math.Min(254, tLbEW));
                tLbNS = Math.Max(0, Math.Min(254, tLbNS));

                double inLbEW = globalEW - tLbEW * 192.0;
                double inLbNS = globalNS - tLbNS * 192.0;

                int tColEW = (int)Math.Floor(inLbEW / 24.0);
                int tRowNS = (int)Math.Floor(inLbNS / 24.0);
                tColEW = Math.Max(0, Math.Min(7, tColEW));
                tRowNS = Math.Max(0, Math.Min(7, tRowNS));

                float localX = (float)(inLbEW - tColEW * 24.0);
                float localY = (float)(inLbNS - tRowNS * 24.0);
                float localZ = playerZ;

                // Clamp to valid range within cell (guard against float drift)
                localX = Math.Max(0.5f, Math.Min(23.5f, localX));
                localY = Math.Max(0.5f, Math.Min(23.5f, localY));

                int cellIdx = tRowNS * 8 + tColEW + 1;
                uint targetCell = (uint)((tLbEW << 24) | (tLbNS << 16) | cellIdx);

                // ── Sanity: target landblock within 3 of player ──────────────
                // If further, the cell is almost certainly not loaded → skip.
                if (Math.Abs(tLbEW - pLbEW) > 3 || Math.Abs(tLbNS - pLbNS) > 3)
                {
                    if (DebugNav)
                        try { _host.Actions.AddChatText($"[NavDbg] T2 SKIP: target lb ({tLbEW},{tLbNS}) too far from player ({pLbEW},{pLbNS})", 1); } catch { }
                    return;
                }

                uint result = Tier2MovementHelper.MoveToPosition(
                    targetCell, localX, localY, localZ,
                    1.0f,
                    (float)Math.Max(0.5, ArrivalYards * 0.5));

                _tier2Active = (result == 0);
                _tier2IssuedAt = DateTime.Now;

                if (DebugNav)
                {
                    try
                    {
                        _host.Actions.AddChatText(
                            $"[NavDbg] T2 issued: cell=0x{targetCell:X8} lx={localX:F1} ly={localY:F1} " +
                            $"lz={localZ:F1} lb=({tLbEW},{tLbNS}) delta=({deltaEW:F0},{deltaNS:F0})yd result={result}", 1);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                if (DebugNav)
                    try { _host.Actions.AddChatText($"[NavDbg] T2 issue FAILED: {ex.Message}", 1); } catch { }
            }
        }

        private void ProcessPortalAction(NavPoint pt, VTankNavParser route)
        {
            // Timeout check - if we've been in this portal action too long, reset and advance
            if (_portalState != PortalState.None)
            {
                if ((DateTime.Now - _portalStateStart).TotalMilliseconds > PORTAL_STATE_TIMEOUT_MS)
                {
                    ResetPortalState();
                    Advance(route);
                    return;
                }
            }

            // Initialize state machine — go to Settling first to let turns stop
            if (_portalState == PortalState.None)
            {
                _portalState = PortalState.Settling;
                _portalStateStart = DateTime.Now;
                _preCastLandblock = GetCurrentLandblock();
                // Store position for teleport detection
                try { var p = GetPos(); if (p != null) { _prePortalNS = p.NorthSouth; _prePortalEW = p.EastWest; } } catch { }
                // Aggressively clear ALL motion
                try { _core.Actions.SetAutorun(false); } catch { }
                try { ClearTurnMotions(); } catch { }
                try { _core.Actions.SetCombatMode(CombatState.Peace); } catch { }
                _isMovingForward = false;
                _isTurning = false;
                return;
            }

            switch (_portalState)
            {
                case PortalState.Settling:
                    // Keep clearing turns every frame until settled
                    try { ClearTurnMotions(); } catch { }
                    if ((DateTime.Now - _portalStateStart).TotalMilliseconds > SETTLE_DELAY_MS)
                    {
                        _portalState = PortalState.EquippingWand;
                        _portalStateStart = DateTime.Now;
                    }
                    break;

                case PortalState.EquippingWand:
                    ProcessEquippingWand(pt);
                    break;

                case PortalState.EnteringMagicMode:
                    ProcessEnteringMagicMode();
                    break;

                case PortalState.CastingSpell:
                    ProcessCastingSpell(pt);
                    break;

                case PortalState.WaitingForPortalOrTeleport:
                    ProcessWaitingForPortalOrTeleport(pt, route);
                    break;

                case PortalState.WaitingForPortalExit:
                    ProcessWaitingForPortalExit(route);
                    break;

                case PortalState.PostTeleportSettle:
                    // Hammer stop commands to cancel any lingering UseItem walk action
                    // UseItem walk persists through teleportation in the AC client — need
                    // to keep hammering for ~2s until it expires on its own
                    try { _core.Actions.SetAutorun(false); } catch { }
                    try { ClearTurnMotions(); } catch { }
                    try { _core.Actions.SetCombatMode(CombatState.Peace); } catch { }
                    _isMovingForward = false;
                    _isTurning = false;
                    if ((DateTime.Now - _portalStateStart).TotalMilliseconds > 4000)
                    {
                        ResetPortalState();
                        Advance(route);
                    }
                    break;
            }
        }

        private void ProcessEquippingWand(NavPoint pt)
        {
            // Only equip wand for recall spells, not portal NPCs
            if (pt.Type == NavPointType.PortalNPC)
            {
                _portalState = PortalState.WaitingForPortalOrTeleport;
                _portalStateStart = DateTime.Now;
                _prePortalLandblock = GetCurrentLandblock();
                try { var p = GetPos(); if (p != null) { _prePortalNS = p.NorthSouth; _prePortalEW = p.EastWest; } } catch { }
                
                // Use the portal NPC — partial name matching
                try
                {
                    WorldObject bestMatch = null;
                    foreach (WorldObject wo in _core.WorldFilter.GetLandscape())
                    {
                        if (wo == null) continue;
                        if (wo.Name == pt.TargetName) { bestMatch = wo; break; }
                        if (wo.Name != null && pt.TargetName != null &&
                            (wo.Name.Contains(pt.TargetName) || pt.TargetName.Contains(wo.Name)))
                        { bestMatch = wo; }
                    }
                    if (bestMatch != null)
                        _core.Actions.UseItem(bestMatch.Id, 0);
                }
                catch { }
                return;
            }

            // For recall spells, equip a wand if needed
            double elapsed = (DateTime.Now - _portalStateStart).TotalMilliseconds;
            
            if (elapsed < 50)
            {
                try
                {
                    // Check if wand already equipped — UseItem on equipped wand UNEQUIPS it
                    bool wandEquipped = false;
                    int wandToEquip = 0;

                    // Scan all known objects for wands
                    foreach (WorldObject wo in _core.WorldFilter.GetInventory())
                    {
                        if (wo.ObjectClass == ObjectClass.WandStaffOrb)
                        {
                            int slots = wo.Values(LongValueKey.EquippedSlots, 0);
                            if (slots != 0)
                            {
                                wandEquipped = true;
                                break;
                            }
                            else if (wandToEquip == 0)
                            {
                                wandToEquip = wo.Id;  // remember first unequipped wand
                            }
                        }
                    }

                    if (!wandEquipped && wandToEquip != 0)
                    {
                        _core.Actions.UseItem(wandToEquip, 0);
                        if (DebugNav) try { _host.Actions.AddChatText($"[NavDbg] WAND equipping id={wandToEquip}", 1); } catch { }
                    }
                    else if (wandEquipped)
                    {
                        if (DebugNav) try { _host.Actions.AddChatText("[NavDbg] WAND already equipped, skipping", 1); } catch { }
                    }
                    else
                    {
                        if (DebugNav) try { _host.Actions.AddChatText("[NavDbg] WAND none found in inventory!", 1); } catch { }
                    }
                }
                catch { }
            }

            // Wait for wand equip to complete, then proceed to magic mode
            if (elapsed > WAND_EQUIP_DELAY_MS)
            {
                _portalState = PortalState.EnteringMagicMode;
                _portalStateStart = DateTime.Now;
            }
        }

        private void ProcessEnteringMagicMode()
        {
            // Enter magic casting mode
            try
            {
                _core.Actions.SetCombatMode(CombatState.Magic);
            }
            catch { }

            // Wait a moment for mode change, then proceed
            if ((DateTime.Now - _portalStateStart).TotalMilliseconds > MAGIC_MODE_DELAY_MS)
            {
                _portalState = PortalState.CastingSpell;
                _portalStateStart = DateTime.Now;
            }
        }

        private void ProcessCastingSpell(NavPoint pt)
        {
            // Cast the recall spell
            if ((DateTime.Now - _portalStateStart).TotalMilliseconds < 100) // Only cast once
            {
                try
                {
                    _core.Actions.CastSpell(pt.SpellId, _core.CharacterFilter.Id);
                }
                catch { }
            }

            // Wait a moment for cast, then proceed to waiting
            if ((DateTime.Now - _portalStateStart).TotalMilliseconds > CAST_DELAY_MS)
            {
                _portalState = PortalState.WaitingForPortalOrTeleport;
                _portalStateStart = DateTime.Now;
                _prePortalLandblock = GetCurrentLandblock();
            }
        }

        private void ProcessWaitingForPortalOrTeleport(NavPoint pt, VTankNavParser route)
        {
            // Detect teleport by checking if we moved far from pre-portal position
            try
            {
                var pos = GetPos();
                if (pos != null && !double.IsNaN(_prePortalNS))
                {
                    double dNS = pos.NorthSouth - _prePortalNS;
                    double dEW = pos.EastWest - _prePortalEW;
                    double distFromStart = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;

                    if (distFromStart > 50.0)  // moved > 50 yards = definitely teleported
                    {
                        _portalState = PortalState.PostTeleportSettle;
                        _portalStateStart = DateTime.Now;
                        return;
                    }
                }
            }
            catch { }

            // Also check landblock as fallback
            int currentLandblock = GetCurrentLandblock();
            if (currentLandblock != _prePortalLandblock && currentLandblock != 0 && _prePortalLandblock != 0)
            {
                _portalState = PortalState.PostTeleportSettle;
                _portalStateStart = DateTime.Now;
                return;
            }

            // For PortalNPC: no short timeout — let UseItem walk complete.
            // The global PORTAL_STATE_TIMEOUT_MS (15s) handles overall timeout.
            // For Recall: use shorter timeout since teleport should be instant.
            if (pt.Type == NavPointType.Recall)
            {
                if ((DateTime.Now - _portalStateStart).TotalMilliseconds > PORTAL_WAIT_MS)
                {
                    ResetPortalState();
                    Advance(route);
                }
            }
        }

        private void ProcessWaitingForPortalExit(VTankNavParser route)
        {
            // Distance-based teleport detection
            try
            {
                var pos = GetPos();
                if (pos != null && !double.IsNaN(_prePortalNS))
                {
                    double dNS = pos.NorthSouth - _prePortalNS;
                    double dEW = pos.EastWest - _prePortalEW;
                    double distFromStart = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;
                    if (distFromStart > 50.0)
                    {
                        _portalState = PortalState.PostTeleportSettle;
                        _portalStateStart = DateTime.Now;
                        return;
                    }
                }
            }
            catch { }

            // Landblock fallback
            int currentLandblock = GetCurrentLandblock();
            if (currentLandblock != _prePortalLandblock && currentLandblock != 0 && _prePortalLandblock != 0)
            {
                _portalState = PortalState.PostTeleportSettle;
                _portalStateStart = DateTime.Now;
            }
        }

        private void ResetPortalState()
        {
            _portalState = PortalState.None;
            _portalStateStart = DateTime.MinValue;
            _preCastLandblock = 0;
            _prePortalLandblock = 0;
            _prePortalNS = double.NaN;
            _prePortalEW = double.NaN;
            
            // Return to peace mode after portal/recall
            try
            {
                _core.Actions.SetCombatMode(CombatState.Peace);
            }
            catch { }
        }

        private int GetCurrentLandblock()
        {
            try
            {
                var pos = GetPos();
                if (pos != null)
                {
                    // Landblock is the high word of the location
                    // AC coordinates encode landblock in a specific way
                    // For simplicity, we'll use a combination of NS/EW rounded to detect changes
                    int nsBlock = (int)(pos.NorthSouth / 0.8);
                    int ewBlock = (int)(pos.EastWest / 0.8);
                    return (nsBlock << 16) | (ewBlock & 0xFFFF);
                }
            }
            catch { }
            return 0;
        }

        private void FireSpecialAction(NavPoint pt)
        {
            // Legacy method - now mostly unused, kept for compatibility
            try
            {
                if (pt.Type == NavPointType.Recall)
                    _core.Actions.CastSpell(pt.SpellId, _core.CharacterFilter.Id);
                else if (pt.Type == NavPointType.PortalNPC)
                {
                    foreach (WorldObject wo in _core.WorldFilter.GetLandscape())
                    {
                        if (wo != null && wo.Name == pt.TargetName) { _core.Actions.UseItem(wo.Id, 0); break; }
                    }
                }
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  STUCK WATCHDOG
        // ══════════════════════════════════════════════════════════════════════════

        private void UpdateWatchdog()
        {
            if (DateTime.Now < _watchdogNext) return;
            _watchdogNext = DateTime.Now.AddMilliseconds(WATCHDOG_MS);

            var pos = GetPos();
            if (pos == null) return;

            double curNS = pos.NorthSouth, curEW = pos.EastWest;

            if (!double.IsNaN(_watchdogNS) && _isMovingForward)
            {
                bool dataStale = Math.Abs(curNS - _watchdogNS) < 0.000001
                              && Math.Abs(curEW - _watchdogEW) < 0.000001
                              && !IsGameFocused();

                if (!dataStale)
                {
                    double dN = curNS - _watchdogNS, dE = curEW - _watchdogEW;
                    double moved = Math.Sqrt(dN * dN + dE * dE) * 240.0;
                    if (moved < STUCK_YD) { _stuckCount++; BeginRecovery(); }
                    else _stuckCount = 0;
                }
            }
            _watchdogNS = curNS; _watchdogEW = curEW;
        }

        private void BeginRecovery()
        {
            _inRecovery    = true;
            _recoveryUntil = DateTime.Now.AddMilliseconds(RECOVERY_MS);
            StopMovement();

            // Jump to try to unstick
            try
            {
                if (MovementActionHelper.IsInitialized)
                {
                    // Tier 1: direct jump call — no keyboard/motion interpreter dependency
                    MovementActionHelper.JumpNonAutonomous(0.5f);
                }
                else
                {
                    // Legacy: local motion command for jump
                    SetClientMotion(0x2500003b, true);
                    SetClientMotion(0x2500003b, false);
                }
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  PUBLIC LIFECYCLE
        // ══════════════════════════════════════════════════════════════════════════

        public void Stop()
        {
            _inPause = false; _actionFired = false; _inRecovery = false;
            _linearDir = 1; _stopRequestedAt = DateTime.MaxValue;
            ResetPortalState();
            CancelTier2();
            try { _core.Actions.SetAutorun(false); } catch { }
            try { ClearTurnMotions(); } catch { }
            _isMovingForward = false;
            _isTurning = false;
            _hasStopped = true;
            _turnsStopped = true;
        }

        public void ResetRouteState()
        {
            Stop();
            _stuckCount = 0;
            _prevDist = double.MaxValue;
            _watchdogNS = double.NaN; _watchdogEW = double.NaN;
            _hasGoodHeading = false;
        }

        public int FindNearestWaypoint(VTankNavParser route)
        {
            if (route == null || route.Points == null || route.Points.Count == 0)
                return 0;

            try
            {
                var pos = GetPos();
                if (pos == null)
                    return 0;

                double myNS = pos.NorthSouth;
                double myEW = pos.EastWest;

                int nearestIdx = 0;
                double nearestDist = double.MaxValue;

                for (int i = 0; i < route.Points.Count; i++)
                {
                    var pt = route.Points[i];
                    // Only consider regular waypoints, not special actions
                    if (pt.Type == NavPointType.Point)
                    {
                        double dNS = pt.NS - myNS;
                        double dEW = pt.EW - myEW;
                        double dist = Math.Sqrt(dNS * dNS + dEW * dEW);

                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearestIdx = i;
                        }
                    }
                }

                return nearestIdx;
            }
            catch
            {
                return 0;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  UTILITIES
        // ══════════════════════════════════════════════════════════════════════════

        private bool IndexValid(int i, VTankNavParser r) => r?.Points != null && i >= 0 && i < r.Points.Count;

        private CoordsObject GetPos()
        {
            try { return _core.WorldFilter[_core.CharacterFilter.Id].Coordinates(); }
            catch { return null; }
        }

        private static double NormalizeAngle(double a)
        {
            while (a >  180.0) a -= 360.0;
            while (a < -180.0) a += 360.0;
            return a;
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}
