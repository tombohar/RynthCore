using System;
using System.Collections.Generic;
using System.IO;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using Newtonsoft.Json;
using NexSuite.Plugins.RynthAi;
using System.Numerics;
using System.Linq;


namespace RynthAi.Meta
{
    public class MetaManager
    {
        private CoreManager _core;
        private UISettings _settings;
        private PluginHost _host;

        private string _lastChatText = "";
        private DateTime _lastChatTime = DateTime.MinValue;
        private System.Text.RegularExpressions.Match _lastChatMatch = null;

        // Timer tracking for "Seconds in State"
        private DateTime _lastStateChange = DateTime.Now;
        private string _previousState = "";

        // Tracks if the meta was turned off so we can clean the slate when turned back on
        private bool _wasMetaEnabled = false;
        public Action OnRouteDynamicallyLoaded;
        private Stack<string> _stateStack = new Stack<string>();
        private bool _watchdogActive = false;
        private string _watchdogState = "";
        private float _watchdogMetersRequired = 0f;
        private double _watchdogSecondsConfig = 0;
        private DateTime _watchdogExpiration;
        private Vector3 _lastWatchdogPos; // Requires 'using System.Numerics;'
        private string _lastState = "";
        private DateTime _stateStartTime = DateTime.Now;
      

        public MetaManager(CoreManager core, UISettings settings, PluginHost host)
        {
            _core = core;
            _settings = settings;
            _host = host;
        }

        public void OnHeartbeat()
        {
            // 1. If Meta is disabled, reset our tracker and do absolutely nothing
            if (!_settings.IsMacroRunning || !_settings.EnableMeta || _settings.MetaRules == null)
            {
                _wasMetaEnabled = false;
                return;
            }

            // 2. If Meta was just turned on, we need a clean slate so old timers don't instantly trigger
            if (!_wasMetaEnabled)
            {
                _wasMetaEnabled = true;
                _lastStateChange = DateTime.Now;
                _previousState = _settings.CurrentState;

                // Reset all rules so they can fire again
                foreach (var r in _settings.MetaRules) r.HasFired = false;
            }

            // 3. Normal State Change OR Forced Reset from UI
            if (!string.Equals(_settings.CurrentState, _previousState, StringComparison.OrdinalIgnoreCase) || _settings.ForceStateReset)
            {
                _settings.ForceStateReset = false; // Consume the flag so it only resets once
                _lastStateChange = DateTime.Now;
                _previousState = _settings.CurrentState;

                // Reset all rules so they can fire again in the new/reset state
                foreach (var r in _settings.MetaRules)
                {
                    r.HasFired = false;
                }
            }

            // Calculate the time ONCE per heartbeat to pass into evaluations, 
            // saving CPU cycles on deep 'Any'/'All' recursive checks.
            double currentSecondsInState = (DateTime.Now - _lastStateChange).TotalSeconds;

            // 4. Evaluate Rules
            foreach (var rule in _settings.MetaRules)
            {
                // Case-insensitive check so "Default" and "default" don't break the meta
                if (string.Equals(rule.State, _settings.CurrentState, StringComparison.OrdinalIgnoreCase))
                {
                    // Check our boolean to prevent chat/action spam
                    if (!rule.HasFired)
                    {
                        if (EvaluateCondition(rule, currentSecondsInState))
                        {
                            ExecuteAction(rule);

                            // Mark this specific rule as fired so it glows red in UI and stops spamming
                            rule.HasFired = true;

                            break; // Process one successful rule per heartbeat
                        }
                    }
                }
            }
        }

        private bool EvaluateCondition(MetaRule rule, double secondsInState)
        {
            try
            {
                // Explicitly using Decal.Adapter for Vitals
                dynamic actions = _host.Actions;

                switch (rule.Condition)
                {
                    case MetaConditionType.Always: return true;
                    case MetaConditionType.Never: return false;

                    case MetaConditionType.All:
                        {
                            if (rule.Children == null || rule.Children.Count == 0) return false;
                            foreach (var child in rule.Children)
                                if (!EvaluateCondition(child, secondsInState)) return false;
                            return true;
                        }

                    case MetaConditionType.Any:
                        {
                            if (rule.Children == null || rule.Children.Count == 0) return false;
                            foreach (var child in rule.Children)
                                if (EvaluateCondition(child, secondsInState)) return true;
                            return false;
                        }

                    case MetaConditionType.Not:
                        {
                            if (rule.Children == null || rule.Children.Count == 0) return false;
                            return !EvaluateCondition(rule.Children[0], secondsInState);
                        }

                    // --- VITAL CHECKS (Value) ---
                    case MetaConditionType.MainHealthLE:
                        {
                            if (int.TryParse(rule.ConditionData, out int hVal))
                                return _core.CharacterFilter.Health <= hVal;
                            return false;
                        }

                    case MetaConditionType.MainManaLE:
                        {
                            if (int.TryParse(rule.ConditionData, out int mVal))
                                return _core.CharacterFilter.Mana <= mVal;
                            return false;
                        }

                    case MetaConditionType.MainStamLE:
                        {
                            if (int.TryParse(rule.ConditionData, out int sVal))
                                return _core.CharacterFilter.Stamina <= sVal;
                            return false;
                        }

                    // --- VITAL CHECKS (Percentage) ---
                    case MetaConditionType.MainHealthPHE:
                        {
                            if (int.TryParse(rule.ConditionData, out int hPct))
                            {
                                double cur = _core.CharacterFilter.Health; // Live HP
                                double max = _core.CharacterFilter.Vitals[Decal.Adapter.Wrappers.CharFilterVitalType.Health].Current; // Max HP
                                return max > 0 && (cur / max) * 100 <= hPct;
                            }
                            return false;
                        }

                    case MetaConditionType.MainManaPHE:
                        {
                            if (int.TryParse(rule.ConditionData, out int mPct))
                            {
                                double cur = _core.CharacterFilter.Mana; // Live MP
                                double max = _core.CharacterFilter.Vitals[Decal.Adapter.Wrappers.CharFilterVitalType.Mana].Current; // Max MP
                                return max > 0 && (cur / max) * 100 <= mPct;
                            }
                            return false;
                        }

                    case MetaConditionType.CharacterDeath:
                        return _core.CharacterFilter.Health <= 0;

                    // --- TIME & CHAT ---
                    case MetaConditionType.SecondsInState_GE:
                    case MetaConditionType.SecondsInStateP_GE:
                        {
                            if (double.TryParse(rule.ConditionData, out double reqSecs))
                                return secondsInState >= reqSecs;
                            return false;
                        }

                    case MetaConditionType.ChatMessage:
                    case MetaConditionType.ChatMessageCapture:
                        {
                            if ((DateTime.Now - _lastChatTime).TotalSeconds > 1.0) return false;
                            try
                            {
                                var regex = new System.Text.RegularExpressions.Regex(rule.ConditionData);
                                var match = regex.Match(_lastChatText);
                                if (match.Success)
                                {
                                    if (rule.Condition == MetaConditionType.ChatMessageCapture) _lastChatMatch = match;
                                    return true;
                                }
                            }
                            catch { }
                            return false;
                        }

                    // --- WORLD & INVENTORY ---
                    case MetaConditionType.BurdenPercentage_GE:
                        {
                            if (int.TryParse(rule.ConditionData, out int targetB))
                                return _core.CharacterFilter.Burden >= targetB;
                            return false;
                        }

                    case MetaConditionType.PackSlots_LE:
                        {
                            if (int.TryParse(rule.ConditionData, out int targetSlots))
                            {
                                int myId = _core.CharacterFilter.Id;
                                int usedGridSquares = 0;
                                foreach (var item in _core.WorldFilter.GetInventory())
                                {
                                    if (item.Container == myId)
                                    {
                                        int slot = item.Values(Decal.Adapter.Wrappers.LongValueKey.Slot, -1);
                                        bool isEquipped = item.Values(Decal.Adapter.Wrappers.LongValueKey.EquippedSlots, 0) > 0;
                                        bool isBag = item.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Container;
                                        bool isFoci = item.Name != null && item.Name.Contains("Foci");

                                        if (slot >= 0 && slot <= 101 && !isEquipped && !isBag && !isFoci)
                                            usedGridSquares++;
                                    }
                                }
                                int freeSlots = 102 - usedGridSquares;
                                return freeSlots <= targetSlots;
                            }
                            return false;
                        }

                    case MetaConditionType.InventoryItemCount_LE:
                    case MetaConditionType.InventoryItemCount_GE:
                        {
                            if (string.IsNullOrEmpty(rule.ConditionData)) return false;
                            string[] invParts = rule.ConditionData.Split(',');
                            if (invParts.Length >= 2)
                            {
                                string itemName = invParts[0].Trim();
                                if (!int.TryParse(invParts[1], out int targetCount)) return false;

                                int currentCount = 0;
                                int myId = _core.CharacterFilter.Id;
                                foreach (var item in _core.WorldFilter.GetInventory())
                                {
                                    bool isMine = (item.Container == myId);
                                    if (!isMine)
                                    {
                                        var parentBag = _core.WorldFilter[item.Container];
                                        if (parentBag != null && parentBag.Container == myId) isMine = true;
                                    }

                                    if (isMine && item.Name != null && item.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                                        currentCount += item.Values(Decal.Adapter.Wrappers.LongValueKey.StackCount, 1);
                                }

                                if (rule.Condition == MetaConditionType.InventoryItemCount_LE)
                                    return currentCount <= targetCount;
                                else
                                    return currentCount >= targetCount;
                            }
                            return false;
                        }

                    case MetaConditionType.TimeLeftOnSpell_GE:
                    case MetaConditionType.TimeLeftOnSpell_LE:
                        {
                            if (string.IsNullOrEmpty(rule.ConditionData)) return false;
                            string[] spellParts = rule.ConditionData.Split(',');
                            if (spellParts.Length >= 2)
                            {
                                if (int.TryParse(spellParts[0], out int spellId) && double.TryParse(spellParts[1], out double reqSecs))
                                {
                                    double remaining = 0;
                                    bool found = false;
                                    foreach (var ench in _core.CharacterFilter.Enchantments)
                                    {
                                        if (ench.SpellId == spellId)
                                        {
                                            remaining = ench.TimeRemaining;
                                            found = true;
                                            break;
                                        }
                                    }
                                    if (rule.Condition == MetaConditionType.TimeLeftOnSpell_GE)
                                        return found && remaining >= reqSecs;
                                    else
                                        return !found || remaining <= reqSecs;
                                }
                            }
                            return false;
                        }

                    case MetaConditionType.MonsterNameCountWithinDistance:
                        {
                            if (string.IsNullOrEmpty(rule.ConditionData)) return false;
                            string[] mParts = rule.ConditionData.Split(',');
                            if (mParts.Length >= 3)
                            {
                                string pattern = mParts[0];
                                if (!double.TryParse(mParts[1], out double dist) || !int.TryParse(mParts[2], out int count)) return false;
                                int matchCount = 0;
                                var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                foreach (var obj in _core.WorldFilter.GetLandscape())
                                {
                                    if (obj.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Monster && _core.WorldFilter.Distance(obj.Id, _core.CharacterFilter.Id) <= dist)
                                        if (regex.IsMatch(obj.Name)) matchCount++;
                                }
                                return matchCount >= count;
                            }
                            return false;
                        }

                    case MetaConditionType.NoMonstersWithinDistance:
                        {
                            double maxD = 20.0;
                            double.TryParse(rule.ConditionData, out maxD);
                            foreach (var obj in _core.WorldFilter.GetLandscape())
                            {
                                if (obj.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Monster && _core.WorldFilter.Distance(obj.Id, _core.CharacterFilter.Id) <= maxD)
                                    return false;
                            }
                            return true;
                        }

                    case MetaConditionType.NavrouteEmpty:
                        if (_settings.CurrentRoute == null || _settings.CurrentRoute.Points.Count == 0) return true;
                        return _settings.ActiveNavIndex >= _settings.CurrentRoute.Points.Count;

                    case MetaConditionType.AnyVendorOpen:
                        return actions.VendorId != 0;

                    case MetaConditionType.PortalspaceEntered:
                        return (actions.Status & 0x1) != 0;

                    case MetaConditionType.PortalspaceExited:
                        return (actions.Status & 0x1) == 0;

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                _host.Actions.AddChatText($"[RynthAi] EvaluateCondition Error: {ex.Message}", 2);
            }
            return false;
        }

        private void ExecuteAction(MetaRule rule)
        {
            dynamic actions = _host.Actions;

            // --- HELPER: Regex Variable Replacement ---
            string ProcessData(string rawData)
            {
                if (string.IsNullOrEmpty(rawData)) return "";
                if (_lastChatMatch == null || !_lastChatMatch.Success) return rawData;

                string result = rawData;
                for (int i = 0; i < _lastChatMatch.Groups.Count; i++)
                {
                    result = result.Replace($"{{{i}}}", _lastChatMatch.Groups[i].Value);
                }
                return result;
            }

            switch (rule.Action)
            {
                case MetaActionType.All:
                    if (rule.Children != null && rule.Children.Count > 0)
                    {
                        foreach (var childAction in rule.Children) ExecuteAction(childAction);
                    }
                    break;

                case MetaActionType.ChatCommand:
                    string finalCommand = ProcessData(rule.ActionData);
                    if (!string.IsNullOrEmpty(finalCommand)) actions.InvokeChatParser(finalCommand);
                    break;

                case MetaActionType.SetMetaState:
                    string nextState = ProcessData(rule.ActionData);
                    if (!string.IsNullOrEmpty(nextState))
                    {
                        _settings.CurrentState = nextState;
                        _settings.ForceStateReset = true;
                        actions.AddChatText($"[RynthAi] Meta State -> {nextState}", 5);
                    }
                    break;

                case MetaActionType.CallMetaState:
                    string callState = ProcessData(rule.ActionData);
                    if (!string.IsNullOrEmpty(callState))
                    {
                        _stateStack.Push(_settings.CurrentState);
                        _settings.CurrentState = callState;
                        _settings.ForceStateReset = true;
                        actions.AddChatText($"[RynthAi] Calling State -> {callState}", 5);
                    }
                    break;

                case MetaActionType.ReturnFromCall:
                    if (_stateStack.Count > 0)
                    {
                        string returnTo = _stateStack.Pop();
                        _settings.CurrentState = returnTo;
                        _settings.ForceStateReset = true;
                        actions.AddChatText($"[RynthAi] Returning to -> {returnTo}", 5);
                    }
                    else { actions.AddChatText("[RynthAi] Error: Return From Call failed (Stack Empty)", 2); }
                    break;

                case MetaActionType.EmbeddedNavRoute:
                    if (string.IsNullOrWhiteSpace(rule.ActionData)) break;
                    string routeName = rule.ActionData.Split(';')[0];
                    string navFolder = @"C:\Games\DecalPlugins\NexSuite\RynthAi\NavProfiles";
                    string fullPath = System.IO.Path.Combine(navFolder, routeName + ".nav");

                    if (System.IO.File.Exists(fullPath))
                    {
                        try
                        {
                            _settings.CurrentNavPath = fullPath;
                            _settings.CurrentRoute = VTankNavParser.Load(fullPath);
                            _settings.ActiveNavIndex = 0;
                            actions.AddChatText($"[RynthAi Meta] Switched to route: {routeName}", 5);
                            OnRouteDynamicallyLoaded?.Invoke();
                        }
                        catch (Exception ex) { actions.AddChatText($"[RynthAi Meta] Load Error: {ex.Message}", 2); }
                    }
                    else { actions.AddChatText($"[RynthAi Meta] Route file missing: {fullPath}", 2); }
                    break;

                case MetaActionType.SetWatchdog:
                    // IGNITION SWITCH: If it's already ticking, do absolutely nothing. 
                    // This prevents the rule from resetting the timer every single frame!
                    if (_watchdogActive) break;

                    string[] wdParts = (rule.ActionData ?? "").Split(';');
                    if (wdParts.Length >= 3)
                    {
                        _watchdogState = wdParts[0].Trim();
                        if (!float.TryParse(wdParts[1].Trim(), out _watchdogMetersRequired)) _watchdogMetersRequired = 5f;
                        if (!double.TryParse(wdParts[2].Trim(), out _watchdogSecondsConfig)) _watchdogSecondsConfig = 5.0;

                        _watchdogExpiration = DateTime.Now.AddSeconds(_watchdogSecondsConfig);
                        _lastWatchdogPos = new Vector3((float)actions.LocationX, (float)actions.LocationY, (float)actions.LocationZ);

                        _watchdogActive = true;

                        actions.AddChatText($"[RynthAi] Watchdog Started: To {_watchdogState} if not moved {_watchdogMetersRequired}m in {_watchdogSecondsConfig}s.", 5);
                    }
                    break;

                case MetaActionType.ClearWatchdog:
                    _watchdogActive = false;
                    actions.AddChatText("[RynthAi] Watchdog Cleared", 5);
                    break;

                case MetaActionType.SetNTOption:
                    string[] ntParts = (rule.ActionData ?? "").Split(';');
                    if (ntParts.Length >= 2) actions.AddChatText($"[RynthAi] Option Set: {ntParts[0]} = {ntParts[1]}", 5);
                    break;

                case MetaActionType.ExpressionAction:
                case MetaActionType.ChatExpression:
                case MetaActionType.CreateView:
                case MetaActionType.DestroyView:
                case MetaActionType.DestroyAllViews:
                    break;
            }
        }



        public void Think()
        {
            try
            {
                dynamic actions = _host.Actions;

                // 1. THE KILL SWITCH (State Entry/Exit)
                if (_settings.CurrentState != _lastState || _settings.ForceStateReset)
                {
                    _lastState = _settings.CurrentState;
                    _stateStartTime = DateTime.Now;

                    _watchdogActive = false;
                    _settings.ForceStateReset = false;

                    // --- THE FIX: Wipe the slate clean for the new state ---
                    // This ensures rules can fire again if we leave the state and come back later
                    if (_settings.MetaRules != null)
                    {
                        foreach (var r in _settings.MetaRules)
                        {
                            r.HasFired = false;
                        }
                    }
                }

                double secondsInState = (DateTime.Now - _stateStartTime).TotalSeconds;

                // 2. THE WATCHDOG TIMER
                if (_watchdogActive)
                {
                    Vector3 currentPos = new Vector3(
                        (float)actions.LocationX,
                        (float)actions.LocationY,
                        (float)actions.LocationZ
                    );

                    float distMoved = Vector3.Distance(currentPos, _lastWatchdogPos);

                    if (distMoved < _watchdogMetersRequired)
                    {
                        if (DateTime.Now > _watchdogExpiration)
                        {
                            _watchdogActive = false;
                            _settings.CurrentState = _watchdogState;
                            _settings.ForceStateReset = true;

                            actions.AddChatText($"[RynthAi] WATCHDOG TRIGGERED! Jumping to: {_watchdogState}", 5);
                            return;
                        }
                    }
                    else
                    {
                        _lastWatchdogPos = currentPos;
                        _watchdogExpiration = DateTime.Now.AddSeconds(_watchdogSecondsConfig);
                    }
                }

                // 3. RULE EVALUATION
                if (!_settings.EnableMeta || _settings.MetaRules == null) return;

                var currentRules = _settings.MetaRules.Where(r => r.State == _settings.CurrentState).ToList();

                foreach (var rule in currentRules)
                {
                    // --- THE FIX: Skip this rule if it already successfully fired during this state visit ---
                    if (rule.HasFired) continue;

                    if (EvaluateCondition(rule, secondsInState))
                    {
                        rule.HasFired = true; // Lock it out from firing again
                        ExecuteAction(rule);

                        if (_settings.CurrentState != _lastState || _settings.ForceStateReset)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _host.Actions.AddChatText($"[RynthAi] ENGINE CRASH in Think(): {ex.Message}", 5);
            }
        }

        public void PrintWatchdogStatus()
        {
            if (!_watchdogActive)
            {
                _host.Actions.AddChatText("[RynthAi] Watchdog Status: INACTIVE", 5);
                return;
            }

            // Grab current coordinates
            Vector3 currentPos = new Vector3(
                (float)_host.Actions.LocationX,
                (float)_host.Actions.LocationY,
                (float)_host.Actions.LocationZ
            );

            // Calculate real-time stats
            float distMoved = Vector3.Distance(currentPos, _lastWatchdogPos);
            double secondsLeft = (_watchdogExpiration - DateTime.Now).TotalSeconds;

            // Print the diagnostic readout
            _host.Actions.AddChatText($"[RynthAi] Watchdog Status: ACTIVE", 5);
            _host.Actions.AddChatText($"   -> Target State: {_watchdogState}", 5);
            _host.Actions.AddChatText($"   -> Distance Moved: {distMoved:F2}m / {_watchdogMetersRequired}m", 5);
            _host.Actions.AddChatText($"   -> Time Left: {Math.Max(0, secondsLeft):F1}s", 5);
        }

        private Vector3 GetCurrentPosition()
        {
            // Accessing the host's actions coordinates directly
            return new Vector3(
                (float)_host.Actions.LocationX,
                (float)_host.Actions.LocationY,
                (float)_host.Actions.LocationZ
            );
        }



        // --- PROFILE PERSISTENCE ---

        public void LoadProfile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    _settings.MetaRules = new List<MetaRule>();
                    return;
                }

                // Try VTank Parser first for .met files
                if (path.EndsWith(".met", StringComparison.OrdinalIgnoreCase))
                {
                    var vtankRules = VTankMetaParser.IntelligentlyLoad(path);
                    if (vtankRules != null)
                    {
                        _settings.MetaRules = vtankRules;
                        _host.Actions.AddChatText($"[RynthAi] Imported VTank Profile: {Path.GetFileName(path)}", 5);
                        return;
                    }
                }

                // Fallback to our native JSON loader
                string json = File.ReadAllText(path);
                var rules = JsonConvert.DeserializeObject<List<MetaRule>>(json);
                if (rules != null) _settings.MetaRules = rules;
            }
            catch (Exception ex)
            {
                _host.Actions.AddChatText($"[RynthAi] Meta Load Error: {ex.Message}", 1);
            }
        }
    

        public void SaveCurrentProfile()
        {
            try
            {
                if (string.IsNullOrEmpty(_settings.CurrentMetaPath)) return;

                string directory = Path.GetDirectoryName(_settings.CurrentMetaPath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                string json = JsonConvert.SerializeObject(_settings.MetaRules, Formatting.Indented);
                File.WriteAllText(_settings.CurrentMetaPath, json);
            }
            catch (Exception ex)
            {
                _host.Actions.AddChatText($"[RynthAi] Meta Save Error: {ex.Message}", 1);
            }
        }

        public void HandleChat(string text)
        {
            // TEMPORARY DEBUG: Uncomment this to see if text is hitting the manager
            // _host.Actions.AddChatText($"[Meta Debug] Received: {text}", 5);

            _lastChatText = text;
            _lastChatTime = DateTime.Now;
        }
    }
}