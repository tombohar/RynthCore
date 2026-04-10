using System;
using System.Runtime.InteropServices;
using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace NexSuite.Plugins.RynthAi
{
    /// <summary>
    /// Native RynthAi jump system — no UB dependency.
    /// Tier 1: Uses CM_Movement::Event_Jump_NonAutonomous for direct jump
    /// commands when MovementActionHelper is initialized (no keyboard input).
    /// Fallback: PostMessage keyboard simulation for legacy mode.
    /// Direction keys (W/X/Z/C) still use PostMessage in Tier 1 since
    /// they control local physics direction. Tier 2 will replace these.
    /// Flow: Face heading → hold direction keys → jump → release → resume macro
    /// </summary>
    public class RynthJumper
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, uint wParam, uint lParam);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;

        // AC key codes
        private const uint VK_SPACE = 0x20;   // Jump
        private const uint VK_SHIFT = 0x10;   // Run modifier
        private const uint VK_W = 0x57;       // Forward
        private const uint VK_X = 0x58;       // Backward
        private const uint VK_Z = 0x5A;       // Slide left
        private const uint VK_C = 0x43;       // Slide right

        private enum JumpState { Idle, Turning, HoldingKeys, Done }
        private JumpState _state = JumpState.Idle;

        private PluginHost _host;
        private UISettings _settings;

        // Jump parameters
        private double _targetHeading;
        private int _holdTimeMs;
        private bool _addShift, _addW, _addX, _addZ, _addC;
        private DateTime _stateStart;
        private int _turnRetries;
        private const int MAX_TURN_RETRIES = 50; // 50 * 100ms = 5 seconds

        public bool IsJumping => _state != JumpState.Idle;

        public RynthJumper(PluginHost host, UISettings settings)
        {
            _host = host;
            _settings = settings;
        }

        /// <summary>
        /// Start a jump. Mirrors /ub jump[swzxc] [heading] [holdtime]
        /// </summary>
        /// <param name="heading">Heading to face (0-359), or -1 for current heading</param>
        /// <param name="holdTimeMs">How long to hold jump key (0-1000ms)</param>
        /// <param name="shift">S modifier — run</param>
        /// <param name="forward">W — forward</param>
        /// <param name="backward">X — backward</param>
        /// <param name="slideLeft">Z — slide left</param>
        /// <param name="slideRight">C — slide right</param>
        public void StartJump(double heading, int holdTimeMs, bool shift, bool forward, bool backward, bool slideLeft, bool slideRight)
        {
            if (_state != JumpState.Idle) return;

            _holdTimeMs = Math.Max(0, Math.Min(1000, holdTimeMs));
            _addShift = shift;
            _addW = forward;
            _addX = backward;
            _addZ = slideLeft;
            _addC = slideRight;
            _turnRetries = 0;

            // Pause macro during jump
            _settings.IsMacroRunning = false;

            if (heading >= 0)
            {
                _targetHeading = heading;
                _state = JumpState.Turning;
                CoreManager.Current.Actions.Heading = (float)_targetHeading;
            }
            else
            {
                _targetHeading = CoreManager.Current.Actions.Heading;
                // No turn needed — go straight to keys
                _state = JumpState.HoldingKeys;
                PressKeys();
            }
            _stateStart = DateTime.Now;
        }

        /// <summary>
        /// Tap jump — no heading change, no hold time.
        /// Uses CM_Movement::Event_Jump_NonAutonomous for a minimal jump
        /// when available, falls back to PostMessage keyboard simulation.
        /// </summary>
        public void TapJump()
        {
            if (_state != JumpState.Idle) return;

            // Tier 1: direct acclient.exe call — no keyboard input needed
            if (MovementActionHelper.IsInitialized)
            {
                MovementActionHelper.JumpNonAutonomous(0.01f); // minimal extent = tap
                return;
            }

            // Legacy fallback: keyboard simulation
            IntPtr hwnd = _host?.Decal?.Hwnd ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;

            PostMessage(hwnd, WM_KEYDOWN, VK_SPACE, 0x00390001);
            PostMessage(hwnd, WM_KEYUP, VK_SPACE, 0xC0390001);
        }

        /// <summary>
        /// Called every frame by PluginCore. Drives the jump state machine.
        /// </summary>
        public void Think()
        {
            if (_state == JumpState.Idle) return;

            switch (_state)
            {
                case JumpState.Turning:
                    // Check if heading is close enough
                    double currentHeading = CoreManager.Current.Actions.Heading;
                    if (Math.Abs(_targetHeading - currentHeading) < 2.0 ||
                        Math.Abs(_targetHeading - currentHeading) > 358.0) // Handle 0/360 wrap
                    {
                        // Heading matched — start the jump
                        _state = JumpState.HoldingKeys;
                        _stateStart = DateTime.Now;
                        PressKeys();
                    }
                    else
                    {
                        _turnRetries++;
                        // Keep setting heading every 100ms
                        if (_turnRetries % 5 == 0)
                            CoreManager.Current.Actions.Heading = (float)_targetHeading;

                        // Timeout after 5 seconds
                        if (_turnRetries > MAX_TURN_RETRIES)
                        {
                            try { _host?.Actions?.AddChatText("[RynthAi] Jump: turn timed out", 2); } catch { }
                            Finish();
                        }
                    }
                    break;

                case JumpState.HoldingKeys:
                    // Tier 1: jump already fired with correct extent in PressKeys().
                    // Direction keys only need ~100ms for physics to register direction.
                    // Legacy: space bar held to build power bar over holdTimeMs.
                    int effectiveHold = (MovementActionHelper.IsInitialized && _holdTimeMs > 100)
                        ? 100  // Tier 1: brief hold for direction registration
                        : _holdTimeMs;

                    if ((DateTime.Now - _stateStart).TotalMilliseconds >= effectiveHold)
                    {
                        ReleaseKeys();
                        _state = JumpState.Done;
                        _stateStart = DateTime.Now;
                    }
                    break;

                case JumpState.Done:
                    // Brief delay after releasing keys before resuming macro
                    if ((DateTime.Now - _stateStart).TotalMilliseconds >= 200)
                    {
                        Finish();
                    }
                    break;
            }
        }

        private void PressKeys()
        {
            IntPtr hwnd = _host?.Decal?.Hwnd ?? IntPtr.Zero;

            // Tier 1: Use direct jump call for the jump itself.
            // Direction keys (W/X/Z/C) still need PostMessage since they control
            // local movement direction during the jump, which DoMovementCommand
            // doesn't handle (it's server-side only). Tier 2 will replace these
            // with CPhysicsObj movement calls.
            if (MovementActionHelper.IsInitialized)
            {
                // Press direction modifiers via PostMessage (needed for local physics)
                if (hwnd != IntPtr.Zero)
                {
                    if (_addShift) PostMessage(hwnd, WM_KEYDOWN, VK_SHIFT, 0x002A0001);
                    if (_addW) PostMessage(hwnd, WM_KEYDOWN, VK_W, 0x00110001);
                    if (_addX) PostMessage(hwnd, WM_KEYDOWN, VK_X, 0x002D0001);
                    if (_addZ) PostMessage(hwnd, WM_KEYDOWN, VK_Z, 0x002C0001);
                    if (_addC) PostMessage(hwnd, WM_KEYDOWN, VK_C, 0x002E0001);
                }

                // Map holdTimeMs to jump extent (0.0–1.0)
                // AC power bar fills over ~1000ms, so holdTime/1000 approximates extent
                float extent = Math.Max(0.01f, Math.Min(1.0f, _holdTimeMs / 1000.0f));
                MovementActionHelper.JumpNonAutonomous(extent);
                return;
            }

            // Legacy fallback: all keyboard simulation
            if (hwnd == IntPtr.Zero) return;
            PostMessage(hwnd, WM_KEYDOWN, VK_SPACE, 0x00390001);
            if (_addShift) PostMessage(hwnd, WM_KEYDOWN, VK_SHIFT, 0x002A0001);
            if (_addW) PostMessage(hwnd, WM_KEYDOWN, VK_W, 0x00110001);
            if (_addX) PostMessage(hwnd, WM_KEYDOWN, VK_X, 0x002D0001);
            if (_addZ) PostMessage(hwnd, WM_KEYDOWN, VK_Z, 0x002C0001);
            if (_addC) PostMessage(hwnd, WM_KEYDOWN, VK_C, 0x002E0001);
        }

        private void ReleaseKeys()
        {
            IntPtr hwnd = _host?.Decal?.Hwnd ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;

            // Legacy mode: release space bar (Tier 1 never pressed it)
            if (!MovementActionHelper.IsInitialized)
                PostMessage(hwnd, WM_KEYUP, VK_SPACE, 0xC0390001);

            // Release direction keys (used by both Tier 1 and Legacy)
            if (_addShift) PostMessage(hwnd, WM_KEYUP, VK_SHIFT, 0xC02A0001);
            if (_addW) PostMessage(hwnd, WM_KEYUP, VK_W, 0xC0110001);
            if (_addX) PostMessage(hwnd, WM_KEYUP, VK_X, 0xC02D0001);
            if (_addZ) PostMessage(hwnd, WM_KEYUP, VK_Z, 0xC02C0001);
            if (_addC) PostMessage(hwnd, WM_KEYUP, VK_C, 0xC02E0001);
        }

        private void Finish()
        {
            _state = JumpState.Idle;
            _addShift = _addW = _addX = _addZ = _addC = false;
            _turnRetries = 0;
            // Resume macro
            _settings.IsMacroRunning = true;
            try { _host?.Actions?.AddChatText("[RynthAi] Jump complete", 5); } catch { }
        }

        public void Cancel()
        {
            if (_state != JumpState.Idle)
            {
                ReleaseKeys();
                Finish();
            }
        }
    }
}
