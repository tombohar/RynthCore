using Decal.Adapter.Wrappers;
using Decal.Adapter;
using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;

namespace NexSuite.Plugins.RynthAi
{
    public class LootManager : IDisposable
    {
        private CoreManager _core;
        private UISettings _settings;
        private PluginHost _host;
        private FellowshipTracker _fellowshipTracker;

        private VTClassic.LootCore _vtLootCore;
        private VTankLootProfile _parsedLootProfile;
        private string _loadedProfilePath = "";
        private DateTime _loadedProfileWriteTimeUtc = DateTime.MinValue;
        private int _lastSelectedId = 0;

        // --- Auto Cram & Stack ---
        private DateTime _nextInventoryActionTime = DateTime.MinValue;
        private HashSet<string> _failedStackPairs = new HashSet<string>();
        private HashSet<int> _cramBlacklist = new HashSet<int>();
        private DateTime _lastBlacklistClear = DateTime.MinValue;
        private const double BLACKLIST_CLEAR_INTERVAL_MS = 30000.0; // Clear every 30 seconds
        private bool _inventoryDirty = true;  // Start dirty so first scan happens
        private int _lastInventoryCount = 0;  // Track inventory count changes

        // --- Looting State Machine ---
        private enum LootState { Idle, Approaching, WaitingForContents, Identifying, Looting, Closing }
        private LootState _lootState = LootState.Idle;
        private int _targetCorpseId = 0;
        private DateTime _lootTimer = DateTime.MinValue;
        private HashSet<int> _lootedCorpses = new HashSet<int>();

        // --- Sequential Scanner Variables ---
        private int _currentItemIdx = 0;
        private int _itemsLootedFromCorpse = 0;
        private List<int> _corpseItemsToCheck = new List<int>();
        private int _lootRetryCount = 0;
        private int _assessingItemId = 0;
        private DateTime _assessItemStart = DateTime.MinValue;
        private DateTime _lastAssessRequest = DateTime.MinValue;
        private DateTime _stateEnteredAt = DateTime.MinValue;
        private DateTime _lastOpenAttemptAt = DateTime.MinValue;
        private DateTime _openedContainerAt = DateTime.MinValue;
        private DateTime _lastContentsCheckAt = DateTime.MinValue;
        private DateTime _lastLootPickupTime = DateTime.MinValue; // Inter-item delay timer
        // Loot timing now driven by _settings — see UISettings for defaults
        private readonly string _debugLogPath = @"C:\Projects\NexSuite\Plugins\RynthAi\Loot\loot-debug.log";

        // --- VTank-style Deferred Salvage System ---
        // Items are picked up during looting, then salvaged during idle phase
        // --- Single-Item Salvage System ---
        // Items are picked up during looting, then salvaged one at a time during idle phase
        // First item has longer delays (panel opening), subsequent items are faster
        private bool _isSalvagingCurrentItem = false;
        private Queue<int> _salvageQueue = new Queue<int>();
        private DateTime _nextSalvageAction = DateTime.MinValue;
        private enum SalvagePhase { Idle, OpeningPanel, AddingItem, Salvaging, WaitingForResult, CombiningSalvage }
        private SalvagePhase _salvagePhase = SalvagePhase.Idle;
        private int _currentSalvageItemId = 0;
        private bool _salvagePanelOpened = false; // True after first item — faster delays
        private DateTime _salvagePhaseStart = DateTime.MinValue;

        public LootManager(CoreManager core, UISettings settings, PluginHost host)
        {
            _core = core;
            _settings = settings;
            _host = host;
            try
            {
                _vtLootCore = new VTClassic.LootCore();
            }
            catch (Exception ex)
            {
                _host.Actions.AddChatText($"[RynthAi] VTClassic Load Error: {ex.Message}", 1);
            }
        }

        public void SetFellowshipTracker(FellowshipTracker tracker)
        {
            _fellowshipTracker = tracker;
        }

        // Restored for PluginCore/CommandProcessor compatibility
        public void ForceReload()
        {
            _loadedProfilePath = "";
            _loadedProfileWriteTimeUtc = DateTime.MinValue;
        }

        // Restored for CommandProcessor compatibility
        public void SaveProfile(string path)
        {
            // Placeholder: VTank profiles are typically managed within the VTank UI
        }

        public void LoadProfile(string path)
        {
            _loadedProfilePath = path;
            _loadedProfileWriteTimeUtc = DateTime.MinValue;
            _parsedLootProfile = null;

            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                DebugLog($"LoadProfile skipped path='{path}' exists={System.IO.File.Exists(path)}");
                return;
            }

            try
            {
                // DESTROY the old engine to clear the internal cache before reloading
                if (_vtLootCore != null)
                {
                    _vtLootCore.UnloadProfile();
                    _vtLootCore = null;
                }

                _vtLootCore = new VTClassic.LootCore();
                _vtLootCore.LoadProfile(path, false);

                _parsedLootProfile = VTankLootParser.Load(path);
                _loadedProfileWriteTimeUtc = File.GetLastWriteTimeUtc(path);

                DebugLog($"LoadProfile ok path='{path}' parsedRules={(_parsedLootProfile?.Rules?.Count ?? 0)}");
                if (_parsedLootProfile?.Rules != null)
                {
                    for (int i = 0; i < _parsedLootProfile.Rules.Count && i < 5; i++)
                    {
                        var r = _parsedLootProfile.Rules[i];
                        if (r == null) continue;
                        DebugLog($"Rule[{i}] name='{r.Name}' action={r.Action} info='{r.RawInfoLine}'");
                    }
                }
                _host.Actions.AddChatText($"[RynthAi] Loaded Loot Profile: {System.IO.Path.GetFileName(path)}", 1);
            }
            catch (Exception ex)
            {
                DebugLog($"LoadProfile error path='{path}' msg='{ex.Message}'");
                _host.Actions.AddChatText($"[RynthAi] Error loading {path}", 1);
            }
        }

        /// <summary>
        /// Returns true if salvage has items queued or is actively processing.
        /// </summary>
        public bool IsSalvageActive => _salvageQueue.Count > 0 || _salvagePhase != SalvagePhase.Idle;

        /// <summary>
        /// Returns true if salvage should block combat/nav.
        /// True when: queue has items ready to salvage (not looting) OR panel is actively open.
        /// </summary>
        public bool IsSalvageProcessing
        {
            get
            {
                // Panel is actively open — always block
                if (_salvagePhase != SalvagePhase.Idle) return true;
                // Queue has items and we're not looting a corpse — about to start salvaging
                if (_salvageQueue.Count > 0 && _lootState == LootState.Idle) return true;
                return false;
            }
        }

        /// <summary>
        /// Returns true if actively looting a corpse (don't block looting with salvage priority).
        /// </summary>
        public bool IsLootingCorpse => _lootState != LootState.Idle;

        /// <summary>
        /// Process salvage queue — called every frame, highest priority.
        /// Runs independently of loot state (items are already in inventory).
        /// </summary>
        public void ProcessSalvage()
        {
            if (!_settings.IsMacroRunning) return;
            if (_salvageQueue.Count == 0 && _salvagePhase == SalvagePhase.Idle) return;
            if (_lootState != LootState.Idle) return; // Don't salvage while looting a corpse
            if (_core.Actions.BusyState != 0) return;
            ProcessIdleSalvage();
        }

        /// <summary>
        /// Process autostack — only when server is idle (not casting spells).
        /// </summary>
        public bool ProcessStack()
        {
            if (!_settings.IsMacroRunning) return false;
            if (_core.Actions.BusyState != 0) return false; // Don't move items while casting
            if (DateTime.Now < _nextInventoryActionTime) return false;
            if (!_inventoryDirty) return false;
            if (ProcessAutoStack()) { _nextInventoryActionTime = DateTime.Now.AddMilliseconds(500); return true; }
            return false;
        }

        /// <summary>
        /// Process autocram — only when server is idle (not casting spells).
        /// </summary>
        public bool ProcessCram()
        {
            if (!_settings.IsMacroRunning) return false;
            if (_core.Actions.BusyState != 0) return false; // Don't move items while casting
            if (DateTime.Now < _nextInventoryActionTime) return false;
            if (!_inventoryDirty) return false;
            if (ProcessAutoCram()) { _nextInventoryActionTime = DateTime.Now.AddMilliseconds(500); return true; }
            _inventoryDirty = false;
            _nextInventoryActionTime = DateTime.Now.AddMilliseconds(2000);
            return false;
        }

        public void OnHeartbeat()
        {
            if (_settings.CurrentLootPath != _loadedProfilePath)
            {
                LoadProfile(_settings.CurrentLootPath);
            }
            else if (!string.IsNullOrEmpty(_settings.CurrentLootPath) && File.Exists(_settings.CurrentLootPath))
            {
                var writeTime = File.GetLastWriteTimeUtc(_settings.CurrentLootPath);
                if (writeTime > _loadedProfileWriteTimeUtc)
                {
                    DebugLog($"Profile changed on disk, reloading path='{_settings.CurrentLootPath}'");
                    LoadProfile(_settings.CurrentLootPath);
                }
            }
            TestSelectionLogic();

            if (!_settings.IsMacroRunning) return;

            // Periodically clear autostack/autocram blacklists
            if ((DateTime.Now - _lastBlacklistClear).TotalMilliseconds >= BLACKLIST_CLEAR_INTERVAL_MS)
            {
                _failedStackPairs.Clear();
                _cramBlacklist.Clear();
                _lastBlacklistClear = DateTime.Now;
            }

            // Dirty detection for inventory changes
            int currentInvCount = 0;
            try { currentInvCount = _core.WorldFilter.GetInventory().Count; } catch { }
            if (currentInvCount != _lastInventoryCount)
            {
                _inventoryDirty = true;
                _lastInventoryCount = currentInvCount;
            }

            if (!_settings.EnableLooting) return;

            // Don't loot if a higher-priority system has claimed the state
            if (_settings.CurrentState != "Idle" && _settings.CurrentState != "Looting")
            {
                if (_lootState != LootState.Idle) ResetLootingState();
                return;
            }

            ProcessLootingState();
        }

        private void ProcessLootingState()
        {
            switch (_lootState)
            {
                case LootState.Idle:
                    // Don't start next corpse while salvage is running
                    if (_salvagePhase != SalvagePhase.Idle) return;

                    int nearest = FindNearestCorpse();
                    if (nearest != 0)
                    {
                        DebugLog($"Idle->Approaching corpse={nearest}");
                        _targetCorpseId = nearest;
                        _currentItemIdx = 0;
                        _itemsLootedFromCorpse = 0;
                        _corpseItemsToCheck.Clear();
                        _settings.CurrentState = "Looting";
                        
                        // Open the corpse ONCE
                        _host.Actions.SelectItem(_targetCorpseId);
                        _host.Actions.UseItem(_targetCorpseId, 0);
                        
                        _lootState = LootState.Approaching;
                        _stateEnteredAt = DateTime.Now;
                        _lastOpenAttemptAt = DateTime.Now;
                        _lootTimer = DateTime.Now;
                    }
                    break;

                case LootState.Approaching:
                    // Check if corpse opened
                    if (_core.Actions.OpenedContainer == _targetCorpseId)
                    {
                        _lootState = LootState.WaitingForContents;
                        _stateEnteredAt = DateTime.Now;
                        _openedContainerAt = DateTime.Now;
                        _lootTimer = DateTime.Now;
                        DebugLog($"Approaching->WaitingForContents corpse={_targetCorpseId} (opened)");
                    }
                    else
                    {
                        // NOT open yet — only retry UseItem after the configured delay
                        // UseItem is a TOGGLE — calling it again would close it
                        if ((DateTime.Now - _lastOpenAttemptAt).TotalMilliseconds >= _settings.LootOpenRetryMs)
                        {
                            // 8 seconds passed, corpse still not open — retry ONCE
                            _host.Actions.SelectItem(_targetCorpseId);
                            _host.Actions.UseItem(_targetCorpseId, 0);
                            _lastOpenAttemptAt = DateTime.Now;
                            DebugLog($"Approaching: retry open after 8s corpse={_targetCorpseId}");
                        }

                        // If close enough but not open, just wait — the server is processing
                        // Timeout after 10 seconds total
                        if ((DateTime.Now - _stateEnteredAt).TotalSeconds > 10)
                        {
                            DebugLog($"Approaching timeout corpse={_targetCorpseId}");
                            _lootedCorpses.Add(_targetCorpseId);
                            ResetLootingState();
                        }
                    }
                    break;

                case LootState.WaitingForContents:
                    // Corpse is confirmed open — DO NOT call UseItem again (it would close it!)
                    if (_core.Actions.OpenedContainer == _targetCorpseId)
                    {
                        // Give server a moment to populate contents
                        if ((DateTime.Now - _openedContainerAt).TotalMilliseconds < _settings.LootContentSettleMs) return;

                        var items = _core.WorldFilter.GetByContainer(_targetCorpseId);
                        if (items != null && items.Count > 0)
                        {
                            _corpseItemsToCheck = items.Select(i => i.Id).ToList();
                            DebugLog($"Corpse contents loaded id={_targetCorpseId}, items={_corpseItemsToCheck.Count}");
                            _lootState = LootState.Identifying;
                            _lootTimer = DateTime.Now;
                        }
                        else
                        {
                            // Empty — wait briefly for items to populate, then close
                            if ((DateTime.Now - _openedContainerAt).TotalMilliseconds > _settings.LootEmptyCorpseMs)
                            {
                                DebugLog($"Corpse confirmed empty id={_targetCorpseId}");
                                _lootState = LootState.Closing;
                            }
                        }
                    }
                    else
                    {
                        // Container closed unexpectedly (server closed it, or something toggled it)
                        // Wait briefly then retry open ONCE
                        if ((DateTime.Now - _stateEnteredAt).TotalSeconds > 4)
                        {
                            DebugLog($"WaitingForContents: container closed, giving up on corpse={_targetCorpseId}");
                            _lootedCorpses.Add(_targetCorpseId);
                            ResetLootingState();
                        }
                        else if ((DateTime.Now - _lastOpenAttemptAt).TotalMilliseconds >= _settings.LootOpenRetryMs)
                        {
                            // Retry open once after 3s
                            _host.Actions.SelectItem(_targetCorpseId);
                            _host.Actions.UseItem(_targetCorpseId, 0);
                            _lastOpenAttemptAt = DateTime.Now;
                            DebugLog($"WaitingForContents: re-opening corpse={_targetCorpseId}");
                        }
                    }
                    break;

                case LootState.Identifying:
                    // Inter-item delay
                    if (_lastLootPickupTime != DateTime.MinValue && 
                        (DateTime.Now - _lastLootPickupTime).TotalMilliseconds < _settings.LootInterItemDelayMs)
                        return;

                    if (_currentItemIdx >= _corpseItemsToCheck.Count)
                    {
                        // Before closing: verify no lootable items are still on the corpse
                        var remaining = _core.WorldFilter.GetByContainer(_targetCorpseId);
                        if (remaining != null)
                        {
                            foreach (var rem in remaining)
                            {
                                string skipReason;
                                bool isSalv;
                                if (ShouldLootItem(rem.Id, rem, out skipReason, out isSalv))
                                {
                                    DebugLog($"Pre-close check: {rem.Name} still on corpse, looting");
                                    _host.Actions.MoveItem(rem.Id, _core.CharacterFilter.Id, 0, false);
                                    _lootTimer = DateTime.Now;
                                    _lootRetryCount = 0;
                                    _lootState = LootState.Looting;
                                    return;
                                }
                            }
                        }
                        _lootState = LootState.Closing;
                        if (_itemsLootedFromCorpse == 0)
                        {
                            try { _host.Actions.AddChatText($"[RynthAi] No items on corpse matched loot rules ({_corpseItemsToCheck.Count} scanned)", 5); } catch { }
                        }
                        return;
                    }

                    int id = _corpseItemsToCheck[_currentItemIdx];
                    var wo = _core.WorldFilter[id];
                    if (wo == null || wo.Container != _targetCorpseId)
                    {
                        _currentItemIdx++;
                        _lootTimer = DateTime.Now;
                        return;
                    }

                    // Assessment
                    if (_assessingItemId != id)
                    {
                        _assessingItemId = id;
                        _assessItemStart = DateTime.Now;
                        _lastAssessRequest = DateTime.MinValue;
                    }

                    double assessMs = (DateTime.Now - _assessItemStart).TotalMilliseconds;
                    bool needsId = uTank2.PluginCore.PC.FLootPluginQueryNeedsID(id);

                    if (needsId && (DateTime.Now - _lastAssessRequest).TotalMilliseconds >= 250)
                    {
                        _host.Actions.RequestId(id);
                        _lastAssessRequest = DateTime.Now;
                    }

                    if (needsId && assessMs < _settings.LootAssessWindowMs) return;

                    string decisionReason;
                    if (ShouldLootItem(id, wo, out decisionReason, out _isSalvagingCurrentItem))
                    {
                        string actionStr = _isSalvagingCurrentItem ? "SALVAGE" : "KEEP";
                        DebugLog($"Assess {actionStr} id={id} name='{wo.Name}' via={decisionReason}");
                        _host.Actions.AddChatText($"[RynthAi] Scooping {wo.Name} (via {decisionReason})", 1);
                        _host.Actions.MoveItem(id, _core.CharacterFilter.Id, 0, false);
                        _lootState = LootState.Looting; _lootTimer = DateTime.Now; _lootRetryCount = 0;
                        return;
                    }
                    DebugLog($"Assess SKIP id={id} name='{wo.Name}' needsId={needsId} elapsedMs={assessMs:0} via={decisionReason}");
                    _currentItemIdx++;
                    _assessingItemId = 0;
                    _lootTimer = DateTime.Now;
                    break;

                case LootState.Looting:
                    int vId = _corpseItemsToCheck[_currentItemIdx];
                    var itemToVerify = _core.WorldFilter[vId];
                    if (itemToVerify == null || itemToVerify.Container != _targetCorpseId)
                    {
                        DebugLog($"Looted/moved id={vId}");
                        if (_isSalvagingCurrentItem)
                        {
                            _salvageQueue.Enqueue(vId);
                            DebugLog($"Queued for salvage id={vId} (queue={_salvageQueue.Count})");
                        }
                        _isSalvagingCurrentItem = false;
                        _inventoryDirty = true;
                        _itemsLootedFromCorpse++;
                        _lastLootPickupTime = DateTime.Now;
                        _currentItemIdx++; _lootState = LootState.Identifying;
                    }
                    else if ((DateTime.Now - _lootTimer).TotalMilliseconds > _settings.LootRetryTimeoutMs)
                    {
                        _lootRetryCount++;
                        if (_lootRetryCount > 5)
                        {
                            DebugLog($"Loot retries exhausted id={vId}");
                            _currentItemIdx++; _lootState = LootState.Identifying;
                        }
                        else
                        {
                            DebugLog($"Loot retry id={vId} attempt={_lootRetryCount}");
                            _host.Actions.MoveItem(vId, _core.CharacterFilter.Id, 0, false);
                            _lootTimer = DateTime.Now;
                        }
                    }
                    break;

                case LootState.Closing:
                    // Brief settle before closing
                    if (_openedContainerAt != DateTime.MinValue && (DateTime.Now - _openedContainerAt).TotalMilliseconds < _settings.LootClosingDelayMs)
                        return;

                    DebugLog($"Closing corpse id={_targetCorpseId}");
                    _lootedCorpses.Add(_targetCorpseId); // Mark as done
                    // Close the corpse by using it (toggle)
                    _host.Actions.UseItem(_targetCorpseId, 0);
                    _host.Actions.SelectItem(0);
                    ResetLootingState();
                    break;
            }
        }

        private int FindUST()
        {
            foreach (var item in _core.WorldFilter.GetInventory())
            {
                if (item.Name == null) continue;
                
                // NEVER match actual salvage bags — they contain "Salvage" but are ObjectClass.Salvage
                if (item.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Salvage) continue;
                
                // Match by exact name "Ust" or names containing "Salvaging Tool"
                // The AC item is literally named "Ust" — exact match is safest
                if (item.Name.Equals("Ust", StringComparison.OrdinalIgnoreCase))
                    return item.Id;
                    
                // Fallback: "Ust" as part of a longer name (e.g., custom servers)
                // But exclude items that contain common false-positive substrings
                if (item.Name.Equals("Salvaging Tool", StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Equals("Ust of Mhoire", StringComparison.OrdinalIgnoreCase))
                    return item.Id;
            }
            return 0;
        }
        // ══════════════════════════════════════════════════════════════════════════
        //  SINGLE-ITEM SALVAGE PROCESSOR (No UB dependency)
        //  Opens panel → adds ONE item → Salvage → repeat for next
        //  First item: full delays. Subsequent items: faster.
        // ══════════════════════════════════════════════════════════════════════════

        private void ProcessIdleSalvage()
        {
            if (_salvageQueue.Count == 0 && _salvagePhase == SalvagePhase.Idle) return;
            if (_core.Actions.BusyState != 0) return;

            // SAFETY: Global timeout — re-queue item if stuck
            if (_salvagePhase != SalvagePhase.Idle && (DateTime.Now - _salvagePhaseStart).TotalMilliseconds > 10000)
            {
                DebugLog($"IdleSalvage: TIMEOUT in phase {_salvagePhase}, re-queuing item {_currentSalvageItemId}");
                if (_currentSalvageItemId != 0 && IsItemInPlayerInventory(_currentSalvageItemId))
                {
                    var wo = _core.WorldFilter[_currentSalvageItemId];
                    if (wo != null && wo.ObjectClass != Decal.Adapter.Wrappers.ObjectClass.Salvage)
                        _salvageQueue.Enqueue(_currentSalvageItemId);
                }
                _salvagePhase = SalvagePhase.Idle;
                _currentSalvageItemId = 0;
                return;
            }

            // Delays: first item slower (panel opening), subsequent near-instant
            double openDelay = _salvagePanelOpened ? _settings.SalvageOpenDelayFastMs : _settings.SalvageOpenDelayFirstMs;
            double addDelay = _salvagePanelOpened ? _settings.SalvageAddDelayFastMs : _settings.SalvageAddDelayFirstMs;
            double salvageDelay = _settings.SalvageSalvageDelayMs;
            double resultDelay = _salvagePanelOpened ? _settings.SalvageResultDelayFastMs : _settings.SalvageResultDelayFirstMs;

            switch (_salvagePhase)
            {
                case SalvagePhase.Idle:
                    // Dequeue next valid item
                    while (_salvageQueue.Count > 0)
                    {
                        _currentSalvageItemId = _salvageQueue.Dequeue();
                        if (!IsItemInPlayerInventory(_currentSalvageItemId)) { _currentSalvageItemId = 0; continue; }
                        var wo = _core.WorldFilter[_currentSalvageItemId];
                        if (wo == null || wo.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Salvage)
                        { _currentSalvageItemId = 0; continue; }

                        _salvagePhase = SalvagePhase.OpeningPanel;
                        _salvagePhaseStart = DateTime.Now;
                        DebugLog($"IdleSalvage: Dequeued id={_currentSalvageItemId} name='{wo.Name}'");
                        return;
                    }
                    // Queue empty — reset panel flag for next batch
                    _salvagePanelOpened = false;
                    break;

                case SalvagePhase.OpeningPanel:
                    if ((DateTime.Now - _salvagePhaseStart).TotalMilliseconds < openDelay) return;
                    int ustId = FindUST();
                    if (ustId == 0)
                    {
                        _host.Actions.AddChatText("[RynthAi] No Ust found — salvage cleared", 2);
                        _salvageQueue.Clear();
                        _salvagePhase = SalvagePhase.Idle;
                        _currentSalvageItemId = 0;
                        return;
                    }
                    if (!IsItemInPlayerInventory(_currentSalvageItemId))
                    { _salvagePhase = SalvagePhase.Idle; _currentSalvageItemId = 0; return; }

                    try { _host.Actions.UseItem(ustId, 0); }
                    catch 
                    { 
                        // Re-queue the item on failure
                        _salvageQueue.Enqueue(_currentSalvageItemId);
                        _salvagePhase = SalvagePhase.Idle; _currentSalvageItemId = 0; return; 
                    }

                    _salvagePhase = SalvagePhase.AddingItem;
                    _salvagePhaseStart = DateTime.Now;
                    break;

                case SalvagePhase.AddingItem:
                    if ((DateTime.Now - _salvagePhaseStart).TotalMilliseconds < addDelay) return;

                    if (!IsItemInPlayerInventory(_currentSalvageItemId))
                    { _salvagePhase = SalvagePhase.Idle; _currentSalvageItemId = 0; return; }

                    var salvItem = _core.WorldFilter[_currentSalvageItemId];
                    _host.Actions.AddChatText($"[RynthAi] Salvaging {salvItem?.Name}", 1);

                    try { _host.Actions.SalvagePanelAdd(_currentSalvageItemId); }
                    catch 
                    { 
                        _salvageQueue.Enqueue(_currentSalvageItemId); // Re-queue on failure
                        _salvagePhase = SalvagePhase.Idle; _currentSalvageItemId = 0; return; 
                    }

                    _salvagePhase = SalvagePhase.Salvaging;
                    _salvagePhaseStart = DateTime.Now;
                    break;

                case SalvagePhase.Salvaging:
                    if ((DateTime.Now - _salvagePhaseStart).TotalMilliseconds < salvageDelay) return;
                    try { _host.Actions.SalvagePanelSalvage(); } catch { }
                    _salvagePhase = SalvagePhase.WaitingForResult;
                    _salvagePhaseStart = DateTime.Now;
                    break;

                case SalvagePhase.WaitingForResult:
                    if ((DateTime.Now - _salvagePhaseStart).TotalMilliseconds < resultDelay) return;
                    if (_core.Actions.BusyState != 0) return;

                    // VERIFY the item was actually salvaged
                    if (IsItemInPlayerInventory(_currentSalvageItemId))
                    {
                        // Item still in inventory — salvage FAILED, re-queue it
                        var failItem = _core.WorldFilter[_currentSalvageItemId];
                        if (failItem != null && failItem.ObjectClass != Decal.Adapter.Wrappers.ObjectClass.Salvage)
                        {
                            _salvageQueue.Enqueue(_currentSalvageItemId);
                            DebugLog($"IdleSalvage: Item {_currentSalvageItemId} still in inventory — re-queued ({failItem.Name})");
                        }
                    }
                    else
                    {
                        DebugLog($"IdleSalvage: Item {_currentSalvageItemId} salvaged OK");
                    }

                    _salvagePanelOpened = true;
                    _currentSalvageItemId = 0;
                    _inventoryDirty = true;

                    if (_settings.EnableCombineSalvage)
                    { _salvagePhase = SalvagePhase.CombiningSalvage; _salvagePhaseStart = DateTime.Now; }
                    else
                    { _salvagePhase = SalvagePhase.Idle; }
                    break;

                case SalvagePhase.CombiningSalvage:
                    if ((DateTime.Now - _salvagePhaseStart).TotalMilliseconds < 150) return;
                    CombineSalvageBags();
                    _salvagePhase = SalvagePhase.Idle; // Next item picks up immediately
                    break;
            }
        }

        /// <summary>
        /// Checks if an item is anywhere in the player's inventory (main pack or sub-packs).
        /// AC inventory structure: Player → main items + sub-packs → items inside sub-packs.
        /// </summary>
        private bool IsItemInPlayerInventory(int itemId)
        {
            var item = _core.WorldFilter[itemId];
            if (item == null) return false;

            int playerId = _core.CharacterFilter.Id;

            // Direct child of player (main pack)
            if (item.Container == playerId) return true;

            // Child of a sub-pack (pack.Container == playerId)
            var container = _core.WorldFilter[item.Container];
            if (container != null && container.Container == playerId) return true;

            return false;
        }

        /// <summary>
        /// Finds salvage bags of the same material type and combines them.
        /// AC allows combining bags via MoveItem (stack merge).
        /// </summary>
        private void CombineSalvageBags()
        {
            try
            {
                var salvageBags = _core.WorldFilter.GetInventory()
                    .Where(i => i.Name != null && i.Name.Contains("Salvage") && !i.Name.Contains("Tool"))
                    .ToList();

                // Group by material name and combine bags of the same type
                var groups = salvageBags.GroupBy(b => b.Name).Where(g => g.Count() > 1);
                foreach (var group in groups)
                {
                    var bags = group.OrderByDescending(b => b.Values(LongValueKey.StackCount, 1)).ToList();
                    if (bags.Count >= 2)
                    {
                        DebugLog($"CombineSalvage: Merging {bags[1].Name} ({bags[1].Id}) into ({bags[0].Id})");
                        _host.Actions.MoveItem(bags[1].Id, bags[0].Id, 0, false);
                        return; // One combine per tick
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"CombineSalvage error: {ex.Message}");
            }
        }

        private void ResetLootingState()
        {
            _lootState = LootState.Idle;
            _settings.CurrentState = "Idle";
            _targetCorpseId = 0;
            _currentItemIdx = 0;
            _itemsLootedFromCorpse = 0;
            _corpseItemsToCheck.Clear();
            _lootRetryCount = 0;
            _assessingItemId = 0;
            _assessItemStart = DateTime.MinValue;
            _lastAssessRequest = DateTime.MinValue;
            _stateEnteredAt = DateTime.MinValue;
            _lastOpenAttemptAt = DateTime.MinValue;
            _openedContainerAt = DateTime.MinValue;
            _lastContentsCheckAt = DateTime.MinValue;
            _lastLootPickupTime = DateTime.MinValue;
            _isSalvagingCurrentItem = false;
            // NOTE: Do NOT clear _salvageQueue here — items survive between corpses
        }

        private int FindNearestCorpse()
        {
            int best = 0; double d = double.MaxValue;
            foreach (var wo in _core.WorldFilter.GetLandscape())
            {
                if (wo.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Corpse 
                    && !_lootedCorpses.Contains(wo.Id)
                    && wo.Id != _targetCorpseId
                    && ShouldLootCorpse(wo))
                {
                    double dist = _core.WorldFilter.Distance(_core.CharacterFilter.Id, wo.Id);
                    if (dist <= _settings.CorpseApproachRangeMax && dist < d) { d = dist; best = wo.Id; }
                }
            }
            return best;
        }

        /// <summary>
        /// Returns true if there are unlooted corpses within loot range OR we're actively looting.
        /// Uses CorpseApproachRangeMax — only pause for corpses we can actually reach.
        /// </summary>
        public bool HasUnlootedCorpsesNearby()
        {
            if (!_settings.EnableLooting) return false;
            
            // If we're actively looting a corpse, nav must wait
            if (_lootState != LootState.Idle) return true;
            
            // Check for unlooted corpses within actual loot range only
            int playerId = _core.CharacterFilter.Id;
            foreach (var wo in _core.WorldFilter.GetLandscape())
            {
                if (wo.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Corpse 
                    && !_lootedCorpses.Contains(wo.Id)
                    && ShouldLootCorpse(wo))
                {
                    double dist = _core.WorldFilter.Distance(playerId, wo.Id);
                    if (dist <= _settings.CorpseApproachRangeMax) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Determines if a corpse should be looted based on ownership settings.
        /// Reads the corpse's LongDesc property ("Killed by {name}.") to determine who killed it.
        /// 
        /// LootOwnership: 0=MyKillsOnly, 1=FellowshipKills, 2=AllCorpses
        /// 
        /// Fellowship mode relies on:
        /// 1. Chat-based FellowshipTracker for known member names
        /// 2. Server-enforced corpse permissions (you can't open corpses you don't have rights to)
        ///    Our existing approach timeout gracefully handles failed opens.
        /// </summary>
        private bool ShouldLootCorpse(WorldObject corpse)
        {
            if (corpse == null) return false;

            // AllCorpses mode — loot everything
            if (_settings.LootOwnership >= 2) return true;

            string longDesc = "";
            try { longDesc = corpse.Values((StringValueKey)16, ""); } catch { }

            string killerName = ExtractKillerName(longDesc);
            string myName = _core.CharacterFilter.Name;

            // MyKillsOnly — only loot corpses I killed
            if (_settings.LootOwnership == 0)
            {
                if (string.IsNullOrEmpty(killerName)) return false;
                return killerName.Equals(myName, StringComparison.OrdinalIgnoreCase);
            }

            // FellowshipKills mode
            if (_settings.LootOwnership == 1)
            {
                // Always loot my own kills
                if (!string.IsNullOrEmpty(killerName) && killerName.Equals(myName, StringComparison.OrdinalIgnoreCase))
                    return true;

                // If we're in a fellowship, check if the killer is a known member
                if (_fellowshipTracker != null && _fellowshipTracker.IsInFellowship)
                {
                    // If killer is a tracked fellow member, loot it
                    if (!string.IsNullOrEmpty(killerName) && _fellowshipTracker.IsMember(killerName))
                        return true;

                    // If we're in a fellowship but can't identify the killer or they're not tracked yet,
                    // still attempt to loot — the server enforces permissions and our timeout handles failures.
                    // This covers the case where members joined before us and aren't tracked by chat.
                    return true;
                }

                // Not in a fellowship — only loot my kills
                if (string.IsNullOrEmpty(killerName)) return false;
                return killerName.Equals(myName, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// Extracts the killer name from "Killed by {name}." format.
        /// </summary>
        private string ExtractKillerName(string longDesc)
        {
            if (string.IsNullOrEmpty(longDesc)) return null;

            const string prefix = "Killed by ";
            int idx = longDesc.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            string remainder = longDesc.Substring(idx + prefix.Length);
            remainder = remainder.TrimEnd('.', ' ', '\n', '\r');
            return string.IsNullOrEmpty(remainder) ? null : remainder;
        }

        private void TestSelectionLogic()
        {
            int sel = _core.Actions.CurrentSelection;
            if (sel != _lastSelectedId && sel != 0)
            {
                _lastSelectedId = sel;
                var info = uTank2.PluginCore.PC.FWorldTracker_GetWithID(sel);
                if (info != null && _vtLootCore != null)
                {
                    var dec = _vtLootCore.GetLootDecision(info);
                    if (dec != null && (dec.IsKeep || dec.IsSalvage))
                        _host.Actions.AddChatText($"[RynthAi] Match: {_core.WorldFilter[sel].Name} ({dec.RuleName})", 5);
                }
            }
        }

        private bool ShouldLootItem(int id, WorldObject wo, out string reason, out bool isSalvage)
        {
            reason = "none";
            isSalvage = false;

            if (wo == null)
            {
                reason = "null-worldobject";
                return false;
            }

            bool needsId = uTank2.PluginCore.PC.FLootPluginQueryNeedsID(id);

            // 1. PRIMARY PATH: Local VTClassic Engine (Always up-to-date)
            if (!needsId && _vtLootCore != null)
            {
                var info = uTank2.PluginCore.PC.FWorldTracker_GetWithID(id);
                if (info != null)
                {
                    var decision = _vtLootCore.GetLootDecision(info);
                    if (decision != null)
                    {
                        if (decision.IsKeep || decision.IsSalvage || decision.IsKeepUpTo)
                        {
                            isSalvage = decision.IsSalvage;
                            reason = $"vtclassic:{decision.RuleName}";
                            return true;
                        }
                        else
                        {
                            reason = $"vtclassic-noloot:{decision.RuleName}";
                            return false;
                        }
                    }
                }
            }

            // 2. SECONDARY PATH: Local Parsed Profile
            if (_parsedLootProfile != null && _parsedLootProfile.Rules != null && _parsedLootProfile.Rules.Count > 0)
            {
                foreach (var rule in _parsedLootProfile.Rules)
                {
                    if (rule == null) continue;
                    if (rule.IsMatch(wo))
                    {
                        if (rule.Action == LootAction.Keep || rule.Action == LootAction.Salvage)
                        {
                            isSalvage = (rule.Action == LootAction.Salvage);
                            reason = $"parser:{rule.Name}";
                            return true;
                        }

                        reason = $"parser-noloot:{rule.Name}:{rule.Action}";
                        return false;
                    }
                }
            }

            // 3. TERTIARY PATH: Global VTank
            if (!needsId)
            {
                var immediate = uTank2.PluginCore.PC.FLootPluginClassifyImmediate(id);
                if (immediate != null)
                {
                    if (immediate.IsKeep || immediate.IsSalvage)
                    {
                        isSalvage = immediate.IsSalvage;
                        reason = $"vtank-immediate:{immediate.RuleName}";
                        return true;
                    }

                    if (immediate.IsKeepUpTo)
                    {
                        int heldCount = CountInventoryMatchesForRule(immediate.RuleName);
                        if (heldCount < immediate.Data1)
                        {
                            reason = $"vtank-keepupto:{immediate.RuleName} held={heldCount} max={immediate.Data1}";
                            return true;
                        }
                        reason = $"vtank-keepupto-limit:{immediate.RuleName} held={heldCount} max={immediate.Data1}";
                        return false;
                    }
                }
            }

            if (needsId) reason = "needsid-timeout";
            else if (_parsedLootProfile == null || _parsedLootProfile.Rules == null || _parsedLootProfile.Rules.Count == 0)
                reason = "no-parser-rules";

            return false;
        }

        private int CountInventoryMatchesForRule(string ruleName)
        {
            if (string.IsNullOrEmpty(ruleName)) return 0;
            int count = 0;

            foreach (var inv in _core.WorldFilter.GetInventory())
            {
                if (uTank2.PluginCore.PC.FLootPluginQueryNeedsID(inv.Id)) continue;
                var invDecision = uTank2.PluginCore.PC.FLootPluginClassifyImmediate(inv.Id);
                if (invDecision == null || invDecision.RuleName != ruleName) continue;

                int stackMax = inv.Values(LongValueKey.StackMax, 0);
                count += stackMax > 0 ? inv.Values(LongValueKey.StackCount, 1) : 1;
            }

            return count;
        }

        private void DebugLog(string message)
        {
            try
            {
                File.AppendAllText(_debugLogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }

        private bool ProcessAutoCram()
        {
            if (!_settings.EnableAutocram) return false;

            var inv = _core.WorldFilter.GetInventory().ToList();
            int playerId = _core.CharacterFilter.Id;

            int mainPackCount = 0;
            foreach (var item in inv)
            {
                if (item.Container != playerId) continue;
                if (item.Values(LongValueKey.EquippedSlots, 0) != 0) continue;
                if (_settings.ItemRules.Any(r => r.Id == item.Id)) continue;
                if (item.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Container) continue;
                if (item.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Foci) continue;
                if (item.Values(LongValueKey.ItemSlots, 0) > 0) continue; // Has pack slots = is a container
                if (item.Values((LongValueKey)151, 0) > 0) continue;      // Alt container capacity key
                if (_cramBlacklist.Contains(item.Id)) continue;
                if (_salvageQueue.Contains(item.Id)) continue;
                if (item.Id == _currentSalvageItemId) continue;
                // Salvage bags ARE crammable — don't skip them
                mainPackCount++;

                int pack = FindOpenPack(inv, playerId);
                if (pack != 0)
                {
                    DebugLog($"AutoCram: Moving {item.Name} (id={item.Id}) to pack {pack}");
                    try
                    {
                        _host.Actions.MoveItem(item.Id, pack, 0, false);
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"AutoCram: MoveItem FAILED: {ex.Message}");
                    }
                    _cramBlacklist.Add(item.Id);
                    return true;
                }
                else
                {
                    DebugLog($"AutoCram: No open pack found for {item.Name}");
                }
                break; // Only try one item per tick
            }
            // No debug log when nothing to cram — this fires every tick and floods the log
            return false;
        }

        private int FindOpenPack(List<WorldObject> inv, int playerId)
        {
            foreach (var p in inv)
            {
                if (p.Container != playerId) continue;
                
                // Must be a container — check both ObjectClass and capacity properties
                bool isContainer = (p.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Container);
                if (!isContainer) continue;
                
                // Skip foci
                if (p.Name != null && p.Name.IndexOf("Foci", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                int itemsInPack = 0;
                foreach (var item in inv)
                {
                    if (item.Container == p.Id) itemsInPack++;
                }

                // Get capacity
                int cap = p.Values(LongValueKey.ItemSlots, 0);
                if (cap <= 0) cap = p.Values((LongValueKey)151, 0);
                if (cap <= 0) cap = 24;

                // 3-slot safety margin — prevents concurrent move conflicts
                if (itemsInPack <= cap - 3) return p.Id;
            }
            return 0;
        }

        private bool ProcessAutoStack()
        {
            if (!_settings.EnableAutostack) return false;

            int playerId = _core.CharacterFilter.Id;
            var inv = _core.WorldFilter.GetInventory().ToList();
            
            var playerItems = new List<WorldObject>();
            foreach (var item in inv)
            {
                if (item.Container == playerId) { playerItems.Add(item); continue; }
                var container = inv.FirstOrDefault(c => c.Id == item.Container);
                if (container != null && container.Container == playerId) playerItems.Add(item);
            }

            var groups = playerItems
                .Where(x => GetMaxStackSize(x) > 1 && !_salvageQueue.Contains(x.Id) && x.Id != _currentSalvageItemId)
                .GroupBy(x => x.Name)
                .Where(g => g.Count() > 1);

            foreach (var g in groups)
            {
                int max = GetMaxStackSize(g.First());
                var parts = g
                    .Where(x => x.Values(LongValueKey.StackCount, 1) < max)
                    .OrderByDescending(x => x.Values(LongValueKey.StackCount, 1))
                    .ToList();
                
                if (parts.Count < 2) continue;
                
                string pairKey = parts[0].Id + "_" + parts[parts.Count - 1].Id;
                if (_failedStackPairs.Contains(pairKey)) continue;
                
                DebugLog($"AutoStack: Merging {parts[parts.Count - 1].Name} (count={parts[parts.Count - 1].Values(LongValueKey.StackCount, 1)}, id={parts[parts.Count - 1].Id}) into (count={parts[0].Values(LongValueKey.StackCount, 1)}, id={parts[0].Id}), max={max}");
                try
                {
                    _host.Actions.MoveItem(parts[parts.Count - 1].Id, parts[0].Container, parts[0].Id, true);
                }
                catch (Exception ex)
                {
                    DebugLog($"AutoStack: MoveItem FAILED: {ex.Message}");
                }
                _failedStackPairs.Add(pairKey);
                return true;
            }
            return false;
        }

        private int GetMaxStackSize(WorldObject item)
        {
            // Primary: Use the standard Decal StackMax property
            int max = item.Values(LongValueKey.StackMax, 0);
            if (max > 1) return max;

            // Secondary: Some server emulators use a different key for certain items
            int altMax = item.Values((LongValueKey)38, 0);
            if (altMax > 1) return altMax;

            // Tertiary: Known AC item type fallbacks
            if (item.Name != null)
            {
                if (item.Name.Contains("Pyreal")) return 25000;
                if (item.Name.Contains("Trade Note")) return 100;
                if (item.Name.Contains("Taper") || item.Name.Contains("Scarab") || 
                    item.Name.Contains("Prismatic")) return 100;
                if (item.Name.Contains("Arrow") || item.Name.Contains("Bolt") || 
                    item.Name.Contains("Quarrel") || item.Name.Contains("Dart") ||
                    item.Name.Contains("Throwing")) return 250;
                if (item.Name.Contains("Pea") || item.Name.Contains("Grain") || 
                    item.Name.Contains("Salvage")) return 100;
            }

            return 1; // Not stackable
        }

        public void Dispose()
        {
            if (_vtLootCore != null) { _vtLootCore.UnloadProfile(); _vtLootCore = null; }
        }
    }
}