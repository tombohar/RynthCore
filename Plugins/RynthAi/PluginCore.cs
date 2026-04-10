using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Numerics;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using UtilityBelt.Service;
using ImGuiNET;
using System.IO.Ports;
using UtilityBelt.Service.Lib.Settings;
using RynthAi.Meta;
using System.Runtime.InteropServices;
using System.Diagnostics;
using NexSuite.Plugins.RynthAi.Utility;
using AcClient;

namespace NexSuite.Plugins.RynthAi
{
    [FriendlyName("RynthAi")]
    public class PluginCore : PluginBase
    {
        // --- PRIVATE MANAGERS & UI ---
        private RynthAiUI _ui;
        private CombatManager _combatManager;
        private NavigationManager _navigationManager;
        private UISettings _settings;
        private SpellManager _spellManager;
        private LootManager _lootManager;
        private BuffManager _buffManager;
        private MetaManager _metaManager;
        private MissileCraftingManager _missileCraftingManager;
        private FellowshipTracker _fellowshipTracker;
        private UtilityBelt.Service.Views.Hud _mainHud;
        private BackgroundFpsUnlocker _fpsUnlocker;

        private string _settingsPath;
        private CommandProcessor _commandProcessor;

        // --- PUBLIC FOLDERS (Exposed for CommandProcessor) ---
        public string NavFolder = @"C:\Games\DecalPlugins\NexSuite\RynthAi\NavProfiles";
        public string LootFolder = @"C:\Games\DecalPlugins\NexSuite\RynthAi\LootProfiles";
        public string MetaFolder = @"C:\Games\DecalPlugins\NexSuite\RynthAi\MetaProfiles";
        public string LuaFolder = @"C:\Games\DecalPlugins\NexSuite\RynthAi\LuaScripts";
        public string AssemblyDirectory => System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

        // --- EXPOSED DECAL HOOKS & UI (For CommandProcessor) ---
        public RynthAiUI UI => _ui;

        // These create safe, public doorways for your CommandProcessor to access Decal
        public Decal.Adapter.Wrappers.PluginHost PluginHost => base.Host;
        public Decal.Adapter.CoreManager PluginCoreManager => base.Core;

        public void ToggleUI()
        {
            if (_mainHud != null) _mainHud.Visible = !_mainHud.Visible;
        }

        //FPS Throttling System
        private Stopwatch _frameTimer = Stopwatch.StartNew();
        private DateTime _nextLuaTick = DateTime.Now;



        // --- COMBAT & VITAL TRACKING ---
        private Dictionary<int, float> healthRatioTracker = new Dictionary<int, float>();
        private Dictionary<int, int> maxHealthTracker = new Dictionary<int, int>();
        private Dictionary<string, string> monsterWeaknesses = new Dictionary<string, string>();

 
        private DateTime lastRequestTime = DateTime.MinValue;
        private const double RETRY_INTERVAL_SEC = 0.75;
        private const int MAX_RETRIES = 5;

        // --- STATE TRACKING ---

        public RynthAi.Raycasting.MainLogic Raycast { get; private set; }

        // LUA
        private LuaManager _luaManager;
        // Native Jumper
        private RynthJumper _jumper;

        // ... (Your protected override void Startup() method starts here) ...
        protected override void Startup()
        {
            try
            {
                // 1. Initialize Global Folders
                if (!System.IO.Directory.Exists(NavFolder)) System.IO.Directory.CreateDirectory(NavFolder);
                if (!System.IO.Directory.Exists(LootFolder)) System.IO.Directory.CreateDirectory(LootFolder);
                if (!System.IO.Directory.Exists(MetaFolder)) System.IO.Directory.CreateDirectory(MetaFolder);
                if (!System.IO.Directory.Exists(LuaFolder)) System.IO.Directory.CreateDirectory(LuaFolder);

                // 2. Load Databases
                LoadWeaknesses();

                // Path to the spell dump
                string dllPath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string spellDumpPath = System.IO.Path.Combine(dllPath, "Combat", "Vtank spelldump.txt");

              
                // 3. Subscribe to Core Events
                Core.ChatBoxMessage += OnChatBoxMessage;
                Core.RenderFrame += OnRenderFrame;

                // Wait for server login to finish before initializing character-specific data
                Core.CharacterFilter.LoginComplete += CharacterFilter_LoginComplete;

                // Hook command line and server traffic
                CommandLineText += OnChatCommand;
                Core.EchoFilter.ServerDispatch += OnServerDispatch;

                System.Diagnostics.Debug.WriteLine("[RynthAi] Startup Complete.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[RynthAi Startup Error] " + ex.Message);
            }
        }

        protected override void Shutdown()
        {
            try
            {
                SaveSettings();

                // Safely dispose of the D3D graphics using the new RouteTab
                if (_ui != null && _ui.RouteTab != null)
                {
                    _ui.RouteTab.DisposeRouteGraphics();
                }

                if (_mainHud != null) _mainHud.Dispose();

                // --- UNHOOK EVENTS ---
                Core.RenderFrame -= OnRenderFrame;
                Core.ChatBoxMessage -= OnChatBoxMessage;
                Core.EchoFilter.ServerDispatch -= OnServerDispatch;
                CommandLineText -= OnChatCommand;
                Core.CharacterFilter.LoginComplete -= CharacterFilter_LoginComplete;

                // --- DISPOSE MANAGERS ---
                if (_buffManager != null) _buffManager.Dispose();
                if (_combatManager != null) _combatManager.Dispose();
                if (_fellowshipTracker != null) _fellowshipTracker.Dispose();
                if (_navigationManager != null) _navigationManager.Stop();
                if (_luaManager != null) _luaManager.Dispose();
                if (Raycast != null) Raycast.Dispose();


            }
            catch (Exception ex)
            {
                // Try to print to chat, but if the Host is already destroyed, just swallow it gracefully
                try { Host.Actions.AddChatText($"[RynthAi Shutdown Error] {ex.Message}", 1); } catch { }
            }
            _fpsUnlocker?.Dispose();
        }

        private void LoadWeaknesses()
        {
            // Embedded fallback data — used if monsters.json is missing
            var embedded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Olthoi", "Cold" }, { "Tusker", "Fire" }, { "Lugian", "Lightning" },
                { "Drudge", "Fire" }, { "Skeleton", "Fire" }, { "Gromnie", "Acid" },
                { "Reedshark", "Fire" }, { "Armoredillo", "Lightning" }, { "Banderling", "Fire" },
                { "Bunny", "Fire" }, { "Carenzi", "Fire" }, { "Dire Wolf", "Fire" },
                { "Doll", "Fire" }, { "Eater", "Lightning" }, { "Golem", "Acid" },
                { "Grievver", "Cold" }, { "Lich", "Fire" }, { "Matredien", "Lightning" },
                { "Mimic", "Fire" }, { "Mite", "Fire" }, { "Monouga", "Fire" },
                { "Mosswart", "Fire" }, { "Mu-miyah", "Fire" }, { "Niffis", "Lightning" },
                { "Phantasm", "Fire" }, { "Rat", "Fire" }, { "Remoran", "Fire" },
                { "Ruschk", "Fire" }, { "Sclavus", "Cold" }, { "Shreth", "Fire" },
                { "Teth", "Lightning" }, { "Thrass", "Fire" }, { "Ursuin", "Fire" },
                { "Viamontian", "Lightning" }, { "Virindi", "Lightning" }, { "Wasp", "Fire" },
                { "Wight", "Fire" }, { "Zharalim", "Lightning" }, { "Impy", "Lightning" },
            };

            // Start with embedded data
            foreach (var kvp in embedded) monsterWeaknesses[kvp.Key] = kvp.Value;

            // Normalize map for shorthand → spell element names
            var normalizeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Bludge", "Bludgeon" }, { "Bludgeon", "Bludgeon" },
                { "Pierce", "Pierce" }, { "Slash", "Slash" },
                { "Fire", "Fire" }, { "Cold", "Cold" },
                { "Lightning", "Lightning" }, { "Acid", "Acid" },
                { "Light", "Fire" }, { "Nether", "Nether" },
            };

            try
            {
                string basePath = @"C:\Games\DecalPlugins\NexSuite\RynthAi";
                string path = System.IO.Path.Combine(basePath, "monsters.json");

                if (System.IO.File.Exists(path))
                {
                    string content = System.IO.File.ReadAllText(path);
                    var lines = content.Split(new[] { ',', '{', '}', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split(':');
                        if (parts.Length == 2)
                        {
                            string name = parts[0].Trim().Trim('"');
                            string rawWeakness = parts[1].Trim().Trim('"');

                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(rawWeakness)) continue;

                            // Extract the MAGIC element: last entry in "Physical / Magic" format
                            // e.g., "Bludge / Pierce / Cold" → "Cold"
                            // e.g., "Slash / Fire" → "Fire"
                            string[] elements = rawWeakness.Split('/');
                            string magicElement = elements[elements.Length - 1].Trim();

                            // Skip "(Varies)" entries
                            if (magicElement.Contains("Varies")) continue;

                            // Normalize the element name
                            string normalized;
                            if (normalizeMap.TryGetValue(magicElement, out normalized))
                                monsterWeaknesses[name] = normalized;
                            else
                                monsterWeaknesses[name] = magicElement; // Use as-is if unknown
                        }
                    }
                }
            }
            catch { }
        }

        private void CharacterFilter_LoginComplete(object sender, EventArgs e)
        {
            try
            {
                // --- 1. Load Spell Database with In-Game Debugging ---
                string dllPath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string spellDumpPath = System.IO.Path.Combine(dllPath, "Combat", "Vtank spelldump.txt");

                // This will now print the "Raw Line" debug messages to your AC chat
                SpellDatabase.Load(Host);

                // --- 2. Get Character & Server Name safely ---
                string serverName = Core.CharacterFilter.Server;
                string charName = Core.CharacterFilter.Name;

                if (string.IsNullOrEmpty(serverName)) serverName = "UnknownServer";
                if (string.IsNullOrEmpty(charName)) charName = "UnknownCharacter";

                // --- 3. Build CUSTOM dynamic paths ---
                string basePath = @"C:\Games\DecalPlugins\NexSuite\RynthAi\SettingsProfiles";
                string charFolder = System.IO.Path.Combine(basePath, serverName, charName);

                _settingsPath = System.IO.Path.Combine(charFolder, "settings.json");
                if (!System.IO.Directory.Exists(charFolder)) System.IO.Directory.CreateDirectory(charFolder);

                // --- 4. Load settings ---
                LoadSettings();

                // --- 5. Initialize Core Managers ---
                _spellManager = new SpellManager(CoreManager.Current);
                _buffManager = new BuffManager(CoreManager.Current, _settings, _spellManager);
                _buffManager.SetTimerPath(charFolder);
                _combatManager = new CombatManager(Core, _settings, Host, _spellManager);
                _combatManager.SetMonsterWeaknesses(monsterWeaknesses);
                _combatManager.InitializeRaycasting(@"C:\Turbine\Asheron's Call");
                _navigationManager = new NavigationManager(CoreManager.Current, _settings, Host);
                _lootManager = new LootManager(CoreManager.Current, _settings, Host);
                _metaManager = new MetaManager(Core, _settings, Host);
                _missileCraftingManager = new MissileCraftingManager(CoreManager.Current, _settings, Host);
                _fellowshipTracker = new FellowshipTracker(CoreManager.Current);
                _lootManager.SetFellowshipTracker(_fellowshipTracker);

                // Initialize the Raycast Engine and let it auto-find the AC installation folder
                Raycast = new RynthAi.Raycasting.MainLogic();
                Raycast.Initialize();

                // --- 6. Initialize Command Processor ---
                _jumper = new RynthJumper(Host, _settings);
                _commandProcessor = new CommandProcessor(this, _settings, _metaManager, _navigationManager, _buffManager, _combatManager, _lootManager, _luaManager, _jumper, _spellManager, _fellowshipTracker);

                // --- 7. Initialize UI and HUD ---
                _spellManager.InitializeNatively();
                _ui = new RynthAiUI(_settings, _combatManager, Core);

                // --- 8. LUA INITIALIZATION & WIRING ---
                // Initialize AFTER _ui exists to prevent NullRef during bridge setup
                _luaManager = new LuaManager(this);

                // Wire: Run Button (Routed through the new LuaUI tab class)
                _luaManager.ExecuteString("OnBotTick()");

                // Wire: Stop Button (Emergency Shutdown)
                _ui.LuaTab.OnStopRequested = () => {
                    _settings.IsMacroRunning = false;
                    _settings.EnableNavigation = false;
                    _settings.EnableCombat = false;
                    _navigationManager?.Stop();
                    Host.Actions.AddChatText("[RynthLua] Emergency Stop Triggered.", 1);
                };

                // Wire: Settings changes from the Lua tab
                _ui.LuaTab.OnSettingsChanged = () => SaveSettings();

                // --- 9. Wire standard UI Actions ---
                _ui.OnSettingsChanged = () => SaveSettings();
                _ui.OnForceRebuffClicked = () => _buffManager?.ForceFullRebuff();
                _ui.OnLootProfileSelected = () => _lootManager?.ForceReload();
                _ui.OnMetaProfileSelected = () => _metaManager?.LoadProfile(_settings.CurrentMetaPath);
                _ui.OnMetaRulesUpdated = () => _metaManager?.SaveCurrentProfile();

                // Route refresh wire-up
                _metaManager.OnRouteDynamicallyLoaded += () => { if (_ui?.RouteTab != null) _ui.RouteTab.NeedsRouteGraphicsRefresh = true; };

                _combatManager.AttachUI(_ui);
                _ui.SetMissileCraftingManager(_missileCraftingManager);

                // Initialize direct combat action calls (acclient.exe function pointers)
                CombatActionHelper.Initialize(msg => {
                    try { Host.Actions.AddChatText(msg, 1); } catch { }
                });

                // Initialize direct movement action calls (acclient.exe CM_Movement functions)
                MovementActionHelper.Initialize(msg => {
                    try { Host.Actions.AddChatText(msg, 1); } catch { }
                });

                // Initialize Tier 2 movement (MoveToPosition via MovementManager)
                unsafe
                {
                    Tier2MovementHelper.Initialize(
                        msg => { try { Host.Actions.AddChatText(msg, 1); } catch { } },
                        (IntPtr)SmartBox.smartbox);
                }

                // --- 10. Load Navigation Profile ---
                if (!string.IsNullOrEmpty(_settings.CurrentNavPath) && System.IO.File.Exists(_settings.CurrentNavPath))
                {
                    try
                    {
                        // This loads the points into memory
                        _settings.CurrentRoute = VTankNavParser.Load(_settings.CurrentNavPath);

                        // FIX: The following block overwrites the ActiveNavIndex you just loaded from settings.json.
                        // If you want to RESUME where you left off, comment this logic out.
                        /*
                        if (_settings.CurrentRoute.RouteType == NavRouteType.Once || _settings.CurrentRoute.RouteType == NavRouteType.Follow)
                            _settings.ActiveNavIndex = 0;
                        else if (_settings.CurrentRoute.RouteType == NavRouteType.Linear || _settings.CurrentRoute.RouteType == NavRouteType.Circular)
                            _settings.ActiveNavIndex = _navigationManager.FindNearestWaypoint(_settings.CurrentRoute);
                        */

                        _navigationManager.ResetRouteState();

                        // FIX: Tell the UI to draw the rings and dots on the floor now that data is loaded
                        if (_ui?.RouteTab != null)
                        {
                            _ui.RouteTab.UpdateRouteGraphics();
                        }

                        Host.Actions.AddChatText($"[RynthAi] Restored route: {System.IO.Path.GetFileName(_settings.CurrentNavPath)}", 1);
                    }
                    catch (Exception ex)
                    {
                        _settings.CurrentNavPath = "";
                        Host.Actions.AddChatText($"[RynthAi] Failed to load route: {ex.Message}", 1);
                    }
                }

                // --- 11. Initialize standalone Meta Profile ---
                if (!string.IsNullOrEmpty(_settings.CurrentMetaPath) && System.IO.File.Exists(_settings.CurrentMetaPath))
                {
                    _metaManager?.LoadProfile(_settings.CurrentMetaPath);
                }

                _ui.RefreshNavFiles();

                // --- 12. Initialize the HUD ---
                _mainHud = UBService.Huds.CreateHud("RynthAi");
                _mainHud.ShowInBar = true;
                _mainHud.Visible = true;
                _mainHud.OnRender += MainHud_OnRender;

                Host.Actions.AddChatText($"[RynthAi] v1.9.1 loaded for {charName} on {serverName}.", 1);
            }
            catch (Exception ex)
            {
                Host.Actions.AddChatText("[RynthAi Login Error] " + ex.Message, 1);
            }
            _fpsUnlocker = new BackgroundFpsUnlocker();
            _fpsUnlocker.Start(Host);


        }

        private void MainHud_OnRender(object sender, EventArgs e)
        {
            try
            {
                // This single line safely renders your entire unified dashboard 
                // (and all its tabs) every frame without crashing!
                if (_ui != null)
                {
                    _ui.Render();
                }
            }
            catch (Exception ex)
            {
                // Replace this with however you normally log errors in your plugin, 
                // e.g., System.Diagnostics.Debug.WriteLine(ex.ToString());
                System.Diagnostics.Debug.WriteLine(ex.ToString());
            }
        }
        private void OnServerDispatch(object sender, NetworkMessageEventArgs e)
        {
            if (e.Message.Type == 0xF7B0)
            {
                int eventType = (int)e.Message[2];
                if (eventType == 0x1C0)
                {
                    int targetId = (int)e.Message[3];
                    object raw = e.Message[4];
                    if (raw is float f) healthRatioTracker[targetId] = f;
                    else if (raw is double d) healthRatioTracker[targetId] = (float)d;
                    // Report damage feedback to blacklist system — mob is taking damage, so it's reachable
                    _combatManager?.ReportDamageOnTarget(targetId);
                }
                else if (eventType == 0xC9) TryExtractVitals(e.Message);
            }
        }

        private void TryExtractVitals(Decal.Adapter.Message msg)
        {
            try
            {
                MessageStruct ev = msg.Struct("event");
                if (ev != null)
                {
                    int hm = 0; try { hm = ev.Value<int>("healthMax"); } catch { }
                    if (hm > 0)
                    {
                        int obj = ev.Value<int>("object");
                        if (obj == 0) obj = _combatManager.activeTargetId;
                        if (obj != 0) { maxHealthTracker[obj] = hm; return; }
                    }
                }

                int tId = (int)msg[3];
                if (healthRatioTracker.ContainsKey(tId))
                {
                    float currentRatio = healthRatioTracker[tId];
                    for (int i = 4; i < msg.Count - 1; i++)
                    {
                        if (msg[i] is int h && msg[i + 1] is int hMax)
                        {
                            if (hMax >= 50 && hMax < 5000000 && h >= 0 && h <= hMax)
                            {
                                float checkRatio = (float)h / hMax;
                                float currentDiff = Math.Abs(currentRatio - checkRatio);
                                if (currentDiff < 0.05f)
                                {
                                    maxHealthTracker[tId] = hMax;
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public void SaveSettings(string customPath = null)
        {
            // Use the custom path if provided (by CommandProcessor), otherwise use the default char path
            string path = customPath ?? _settingsPath;

            try
            {
                using (StreamWriter sw = new StreamWriter(path))
                {
                    // Core state
                    sw.WriteLine("IsMacroRunning=" + _settings.IsMacroRunning);
                    sw.WriteLine("CurrentNavPath=" + _settings.CurrentNavPath);
                    sw.WriteLine("ActiveNavIndex=" + _settings.ActiveNavIndex);

                    // Subsystem toggles
                    sw.WriteLine("EnableBuffing=" + _settings.EnableBuffing);
                    sw.WriteLine("EnableCombat=" + _settings.EnableCombat);
                    sw.WriteLine("EnableNavigation=" + _settings.EnableNavigation);
                    sw.WriteLine("EnableLooting=" + _settings.EnableLooting);
                    sw.WriteLine("EnableMeta=" + _settings.EnableMeta);
                    sw.WriteLine("EnableRaycasting=" + _settings.EnableRaycasting);

                    // Vitals
                    sw.WriteLine("HealAt=" + _settings.HealAt);
                    sw.WriteLine("RestamAt=" + _settings.RestamAt);
                    sw.WriteLine("GetManaAt=" + _settings.GetManaAt);
                    sw.WriteLine("TopOffHP=" + _settings.TopOffHP);
                    sw.WriteLine("TopOffStam=" + _settings.TopOffStam);
                    sw.WriteLine("TopOffMana=" + _settings.TopOffMana);
                    sw.WriteLine("HealOthersAt=" + _settings.HealOthersAt);
                    sw.WriteLine("RestamOthersAt=" + _settings.RestamOthersAt);
                    sw.WriteLine("InfuseOthersAt=" + _settings.InfuseOthersAt);

                    // Ranges
                    sw.WriteLine("MonsterRange=" + _settings.MonsterRange);
                    sw.WriteLine("RingRange=" + _settings.RingRange);
                    sw.WriteLine("MinRingTargets=" + _settings.MinRingTargets);
                    sw.WriteLine("ApproachRange=" + _settings.ApproachRange);
                    sw.WriteLine("FollowNavMin=" + _settings.FollowNavMin);
                    sw.WriteLine("CorpseApproachRangeMax=" + _settings.CorpseApproachRangeMax);
                    sw.WriteLine("CorpseApproachRangeMin=" + _settings.CorpseApproachRangeMin);

                    // Advanced toggles
                    sw.WriteLine("BoostNavPriority=" + _settings.BoostNavPriority);
                    sw.WriteLine("BoostLootPriority=" + _settings.BoostLootPriority);
                    sw.WriteLine("LootOnlyRareCorpses=" + _settings.LootOnlyRareCorpses);
                    sw.WriteLine("LootOwnership=" + _settings.LootOwnership);
                    sw.WriteLine("PeaceModeWhenIdle=" + _settings.PeaceModeWhenIdle);
                    sw.WriteLine("RebuffWhenIdle=" + _settings.RebuffWhenIdle);

                    // Blacklist
                    sw.WriteLine("BlacklistAttempts=" + _settings.BlacklistAttempts);
                    sw.WriteLine("BlacklistTimeoutSec=" + _settings.BlacklistTimeoutSec);

                    // FPS Throttling
                    sw.WriteLine("EnableFPSLimit=" + _settings.EnableFPSLimit);
                    sw.WriteLine("TargetFPSFocused=" + _settings.TargetFPSFocused);
                    sw.WriteLine("TargetFPSBackground=" + _settings.TargetFPSBackground);

                    // Attack Power
                    sw.WriteLine("MeleeAttackPower=" + _settings.MeleeAttackPower);
                    sw.WriteLine("MissileAttackPower=" + _settings.MissileAttackPower);
                    sw.WriteLine("UseRecklessness=" + _settings.UseRecklessness);
                    sw.WriteLine("MeleeAttackHeight=" + _settings.MeleeAttackHeight);
                    sw.WriteLine("MissileAttackHeight=" + _settings.MissileAttackHeight);

                    // Automation toggles
                    sw.WriteLine("EnableAutostack=" + _settings.EnableAutostack);
                    sw.WriteLine("EnableAutocram=" + _settings.EnableAutocram);
                    sw.WriteLine("EnableCombineSalvage=" + _settings.EnableCombineSalvage);
                    // Missile crafting
                    sw.WriteLine("EnableMissileCrafting=" + _settings.EnableMissileCrafting);
                    sw.WriteLine("MissileCraftAmmoThreshold=" + _settings.MissileCraftAmmoThreshold);
                    // Loot timers
                    sw.WriteLine("LootInterItemDelayMs=" + _settings.LootInterItemDelayMs);
                    sw.WriteLine("LootContentSettleMs=" + _settings.LootContentSettleMs);
                    sw.WriteLine("LootEmptyCorpseMs=" + _settings.LootEmptyCorpseMs);
                    sw.WriteLine("LootClosingDelayMs=" + _settings.LootClosingDelayMs);
                    sw.WriteLine("LootAssessWindowMs=" + _settings.LootAssessWindowMs);
                    sw.WriteLine("LootRetryTimeoutMs=" + _settings.LootRetryTimeoutMs);
                    sw.WriteLine("LootOpenRetryMs=" + _settings.LootOpenRetryMs);
                    // Salvage timers
                    sw.WriteLine("SalvageOpenDelayFirstMs=" + _settings.SalvageOpenDelayFirstMs);
                    sw.WriteLine("SalvageOpenDelayFastMs=" + _settings.SalvageOpenDelayFastMs);
                    sw.WriteLine("SalvageAddDelayFirstMs=" + _settings.SalvageAddDelayFirstMs);
                    sw.WriteLine("SalvageAddDelayFastMs=" + _settings.SalvageAddDelayFastMs);
                    sw.WriteLine("SalvageSalvageDelayMs=" + _settings.SalvageSalvageDelayMs);
                    sw.WriteLine("SalvageResultDelayFirstMs=" + _settings.SalvageResultDelayFirstMs);
                    sw.WriteLine("SalvageResultDelayFastMs=" + _settings.SalvageResultDelayFastMs);
                    sw.WriteLine("UseDispelItems=" + _settings.UseDispelItems);
                    sw.WriteLine("CastDispelSelf=" + _settings.CastDispelSelf);
                    sw.WriteLine("AutoFellowMgmt=" + _settings.AutoFellowMgmt);
                    sw.WriteLine("MChargesWhenOff=" + _settings.MChargesWhenOff);

                    // Pets
                    sw.WriteLine("SummonPets=" + _settings.SummonPets);
                    sw.WriteLine("CustomPetRange=" + _settings.CustomPetRange);
                    sw.WriteLine("PetMinMonsters=" + _settings.PetMinMonsters);

                    // Profile indices
                    sw.WriteLine("MacroSettingsIdx=" + _settings.MacroSettingsIdx);
                    sw.WriteLine("NavProfileIdx=" + _settings.NavProfileIdx);
                    sw.WriteLine("LootProfileIdx=" + _settings.LootProfileIdx);
                    sw.WriteLine("MetaProfileIdx=" + _settings.MetaProfileIdx);

                    // UI state
                    sw.WriteLine("AdvancedOptions=" + _settings.AdvancedOptions);
                    sw.WriteLine("MineOnly=" + _settings.MineOnly);
                    sw.WriteLine("ShowEditor=" + _settings.ShowEditor);

                    // Item rules (with GUID)
                    sw.WriteLine("ItemCount=" + _settings.ItemRules.Count);
                    for (int i = 0; i < _settings.ItemRules.Count; i++)
                    {
                        var r = _settings.ItemRules[i];
                        sw.WriteLine($"Item_{i}={r.Id}|{r.Name}|{r.Element}|{r.KeepBuffed}|{r.Action}");
                    }

                    // Monster rules — saved to separate TSV file for easy Excel editing
                    // Also write count to settings for backwards compat detection
                    sw.WriteLine("MonsterCount=" + _settings.MonsterRules.Count);
                    SaveMonsterRulesTSV(path);

                    // Meta Rules
                    sw.WriteLine("MetaCount=" + _settings.MetaRules.Count);
                    for (int i = 0; i < _settings.MetaRules.Count; i++)
                    {
                        var r = _settings.MetaRules[i];
                        sw.WriteLine($"Meta_{i}={r.State}|{(int)r.Condition}|{r.ConditionData}|{(int)r.Action}|{r.ActionData}");
                    }
                }
            }
            catch (Exception ex)
            {
                Host.Actions.AddChatText($"[RynthAi] Save Error: {ex.Message}", 2);
            }
        }

        public void LoadSettings(string customPath = null)
        {
            string path = customPath ?? _settingsPath;
            _settings = new UISettings();

            try
            {
                if (System.IO.File.Exists(path))
                {
                    foreach (string line in System.IO.File.ReadAllLines(path))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 1) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();

                        // Core state
                        if (k == "IsMacroRunning") _settings.IsMacroRunning = bool.Parse(v);
                        else if (k == "CurrentNavPath") _settings.CurrentNavPath = v;
                        else if (k == "ActiveNavIndex") _settings.ActiveNavIndex = int.Parse(v);

                        // Subsystem toggles
                        else if (k == "EnableBuffing") _settings.EnableBuffing = bool.Parse(v);
                        else if (k == "EnableCombat") _settings.EnableCombat = bool.Parse(v);
                        else if (k == "EnableNavigation") _settings.EnableNavigation = bool.Parse(v);
                        else if (k == "EnableLooting") _settings.EnableLooting = bool.Parse(v);
                        else if (k == "EnableMeta") _settings.EnableMeta = bool.Parse(v);
                        else if (k == "EnableRaycasting") _settings.EnableRaycasting = bool.Parse(v);

                        // Vitals
                        else if (k == "HealAt") _settings.HealAt = int.Parse(v);
                        else if (k == "RestamAt") _settings.RestamAt = int.Parse(v);
                        else if (k == "GetManaAt") _settings.GetManaAt = int.Parse(v);
                        else if (k == "TopOffHP") _settings.TopOffHP = int.Parse(v);
                        else if (k == "TopOffStam") _settings.TopOffStam = int.Parse(v);
                        else if (k == "TopOffMana") _settings.TopOffMana = int.Parse(v);
                        else if (k == "HealOthersAt") _settings.HealOthersAt = int.Parse(v);
                        else if (k == "RestamOthersAt") _settings.RestamOthersAt = int.Parse(v);
                        else if (k == "InfuseOthersAt") _settings.InfuseOthersAt = int.Parse(v);

                        // Ranges
                        else if (k == "MonsterRange") _settings.MonsterRange = int.Parse(v);
                        else if (k == "RingRange") _settings.RingRange = int.Parse(v);
                        else if (k == "MinRingTargets") _settings.MinRingTargets = int.Parse(v);
                        else if (k == "ApproachRange") _settings.ApproachRange = int.Parse(v);
                        else if (k == "FollowNavMin") _settings.FollowNavMin = float.Parse(v);
                        else if (k == "CorpseApproachRangeMax") _settings.CorpseApproachRangeMax = double.Parse(v);
                        else if (k == "CorpseApproachRangeMin") _settings.CorpseApproachRangeMin = double.Parse(v);

                        // Advanced toggles
                        else if (k == "BoostNavPriority") _settings.BoostNavPriority = bool.Parse(v);
                        else if (k == "BoostLootPriority") _settings.BoostLootPriority = bool.Parse(v);
                        else if (k == "LootOnlyRareCorpses") _settings.LootOnlyRareCorpses = bool.Parse(v);
                        else if (k == "LootOwnership") _settings.LootOwnership = int.Parse(v);
                        else if (k == "PeaceModeWhenIdle") _settings.PeaceModeWhenIdle = bool.Parse(v);
                        else if (k == "RebuffWhenIdle") _settings.RebuffWhenIdle = bool.Parse(v);

                        // Blacklist
                        else if (k == "BlacklistAttempts") _settings.BlacklistAttempts = int.Parse(v);
                        else if (k == "BlacklistTimeoutSec") _settings.BlacklistTimeoutSec = int.Parse(v);

                        // FPS Throttling
                        else if (k == "EnableFPSLimit") _settings.EnableFPSLimit = bool.Parse(v);
                        else if (k == "TargetFPSFocused") _settings.TargetFPSFocused = int.Parse(v);
                        else if (k == "TargetFPSBackground") _settings.TargetFPSBackground = int.Parse(v);

                        // Attack Power
                        else if (k == "MeleeAttackPower") _settings.MeleeAttackPower = int.Parse(v);
                        else if (k == "MissileAttackPower") _settings.MissileAttackPower = int.Parse(v);
                        else if (k == "UseRecklessness") _settings.UseRecklessness = bool.Parse(v);
                        else if (k == "MeleeAttackHeight") _settings.MeleeAttackHeight = int.Parse(v);
                        else if (k == "MissileAttackHeight") _settings.MissileAttackHeight = int.Parse(v);
                        // Backwards compat: old float AttackPower → both
                        else if (k == "AttackPower") { float old = float.Parse(v); int pct = old < 0 ? -1 : (int)(old * 100); _settings.MeleeAttackPower = pct; _settings.MissileAttackPower = pct; }

                        // Automation toggles
                        else if (k == "EnableAutostack") _settings.EnableAutostack = bool.Parse(v);
                        else if (k == "EnableAutocram") _settings.EnableAutocram = bool.Parse(v);
                        else if (k == "EnableCombineSalvage") _settings.EnableCombineSalvage = bool.Parse(v);
                        // Missile crafting
                        else if (k == "EnableMissileCrafting") _settings.EnableMissileCrafting = bool.Parse(v);
                        else if (k == "MissileCraftAmmoThreshold") _settings.MissileCraftAmmoThreshold = int.Parse(v);
                        // Loot timers
                        else if (k == "LootInterItemDelayMs") _settings.LootInterItemDelayMs = int.Parse(v);
                        else if (k == "LootContentSettleMs") _settings.LootContentSettleMs = int.Parse(v);
                        else if (k == "LootEmptyCorpseMs") _settings.LootEmptyCorpseMs = int.Parse(v);
                        else if (k == "LootClosingDelayMs") _settings.LootClosingDelayMs = int.Parse(v);
                        else if (k == "LootAssessWindowMs") _settings.LootAssessWindowMs = int.Parse(v);
                        else if (k == "LootRetryTimeoutMs") _settings.LootRetryTimeoutMs = int.Parse(v);
                        else if (k == "LootOpenRetryMs") _settings.LootOpenRetryMs = int.Parse(v);
                        // Salvage timers
                        else if (k == "SalvageOpenDelayFirstMs") _settings.SalvageOpenDelayFirstMs = int.Parse(v);
                        else if (k == "SalvageOpenDelayFastMs") _settings.SalvageOpenDelayFastMs = int.Parse(v);
                        else if (k == "SalvageAddDelayFirstMs") _settings.SalvageAddDelayFirstMs = int.Parse(v);
                        else if (k == "SalvageAddDelayFastMs") _settings.SalvageAddDelayFastMs = int.Parse(v);
                        else if (k == "SalvageSalvageDelayMs") _settings.SalvageSalvageDelayMs = int.Parse(v);
                        else if (k == "SalvageResultDelayFirstMs") _settings.SalvageResultDelayFirstMs = int.Parse(v);
                        else if (k == "SalvageResultDelayFastMs") _settings.SalvageResultDelayFastMs = int.Parse(v);
                        else if (k == "UseDispelItems") _settings.UseDispelItems = bool.Parse(v);
                        else if (k == "CastDispelSelf") _settings.CastDispelSelf = bool.Parse(v);
                        else if (k == "AutoFellowMgmt") _settings.AutoFellowMgmt = bool.Parse(v);
                        else if (k == "MChargesWhenOff") _settings.MChargesWhenOff = bool.Parse(v);

                        // Pets
                        else if (k == "SummonPets") _settings.SummonPets = bool.Parse(v);
                        else if (k == "CustomPetRange") _settings.CustomPetRange = int.Parse(v);
                        else if (k == "PetMinMonsters") _settings.PetMinMonsters = int.Parse(v);

                        // Profile indices
                        else if (k == "MacroSettingsIdx") _settings.MacroSettingsIdx = int.Parse(v);
                        else if (k == "NavProfileIdx") _settings.NavProfileIdx = int.Parse(v);
                        else if (k == "LootProfileIdx") _settings.LootProfileIdx = int.Parse(v);
                        else if (k == "MetaProfileIdx") _settings.MetaProfileIdx = int.Parse(v);

                        // UI state
                        else if (k == "AdvancedOptions") _settings.AdvancedOptions = bool.Parse(v);
                        else if (k == "MineOnly") _settings.MineOnly = bool.Parse(v);
                        else if (k == "ShowEditor") _settings.ShowEditor = bool.Parse(v);

                        // Item rules
                        else if (k.StartsWith("Item_"))
                        {
                            var bits = v.Split('|');
                            if (bits.Length >= 5)
                            {
                                _settings.ItemRules.Add(new ItemRule { Id = int.Parse(bits[0]), Name = bits[1], Element = bits[2], KeepBuffed = bool.Parse(bits[3]), Action = bits[4] });
                            }
                        }

                        // Monster rules
                        else if (k.StartsWith("Monster_"))
                        {
                            var bits = v.Split('|');
                            if (bits.Length >= 4)
                            {
                                var mr = new MonsterRule { Name = bits[0], Priority = int.Parse(bits[1]), DamageType = bits[2], WeaponId = int.Parse(bits[3]) };
                                // Load debuff/spell type flags if present (backwards compat)
                                if (bits.Length >= 13)
                                {
                                    mr.Fester = bool.Parse(bits[4]);
                                    mr.Broadside = bool.Parse(bits[5]);
                                    mr.GravityWell = bool.Parse(bits[6]);
                                    mr.Imperil = bool.Parse(bits[7]);
                                    mr.Yield = bool.Parse(bits[8]);
                                    mr.Vuln = bool.Parse(bits[9]);
                                    mr.UseArc = bool.Parse(bits[10]);
                                    mr.UseRing = bool.Parse(bits[11]);
                                    mr.UseStreak = bool.Parse(bits[12]);
                                }
                                // Load ExVuln, OffhandId, PetDamage if present
                                if (bits.Length >= 16)
                                {
                                    mr.ExVuln = bits[13];
                                    mr.OffhandId = int.Parse(bits[14]);
                                    mr.PetDamage = bits[15];
                                }
                                // Load UseBolt if present (backwards compat: defaults to true)
                                if (bits.Length >= 17)
                                {
                                    mr.UseBolt = bool.Parse(bits[16]);
                                }
                                _settings.MonsterRules.Add(mr);
                            }
                        }

                        // Meta Rules
                        else if (k.StartsWith("Meta_"))
                        {
                            var bits = v.Split('|');
                            if (bits.Length >= 5)
                            {
                                _settings.MetaRules.Add(new MetaRule { State = bits[0], Condition = (MetaConditionType)int.Parse(bits[1]), ConditionData = bits[2], Action = (MetaActionType)int.Parse(bits[3]), ActionData = bits[4] });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Host.Actions.AddChatText($"[RynthAi] Load Error: {ex.Message}", 2);
            }

            _settings.IsMacroRunning = false;

            // Try loading monster rules from TSV file (preferred) — overrides any inline Monster_ lines
            string tsvPath = GetMonsterRulesTSVPath(path);
            if (System.IO.File.Exists(tsvPath))
            {
                var tsvRules = LoadMonsterRulesTSV(tsvPath);
                if (tsvRules.Count > 0)
                {
                    _settings.MonsterRules = tsvRules;
                }
            }

            _settings.EnsureDefaultRule();
        }

        private string GetMonsterRulesTSVPath(string settingsPath)
        {
            string dir = System.IO.Path.GetDirectoryName(settingsPath);
            return System.IO.Path.Combine(dir, "monsters.tsv");
        }

        private void SaveMonsterRulesTSV(string settingsPath)
        {
            string tsvPath = GetMonsterRulesTSVPath(settingsPath);
            string tmpPath = tsvPath + ".tmp";
            
            try
            {
                // Write to temp file first to avoid lock conflicts
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs))
                {
                    sw.WriteLine("Name\tPriority\tDmgType\tWeaponId\tFester\tBroadside\tGravity\tImperil\tYield\tVuln\tArc\tRing\tStreak\tBolt\tExVuln\tOffhandId\tPetDamage");
                    foreach (var r in _settings.MonsterRules)
                    {
                        sw.WriteLine($"{r.Name}\t{r.Priority}\t{r.DamageType}\t{r.WeaponId}\t{r.Fester}\t{r.Broadside}\t{r.GravityWell}\t{r.Imperil}\t{r.Yield}\t{r.Vuln}\t{r.UseArc}\t{r.UseRing}\t{r.UseStreak}\t{r.UseBolt}\t{r.ExVuln}\t{r.OffhandId}\t{r.PetDamage}");
                    }
                }
                
                // Atomic replace — if the main file is locked, this will fail gracefully
                try
                {
                    if (File.Exists(tsvPath)) File.Delete(tsvPath);
                    File.Move(tmpPath, tsvPath);
                }
                catch
                {
                    // Main file is locked (e.g., open in Excel) — leave tmp for next attempt
                    // Don't spam chat about it, it's not critical
                }
            }
            catch (Exception ex)
            {
                try { Host.Actions.AddChatText($"[RynthAi] Monster TSV save error: {ex.Message}", 2); } catch { }
            }
        }

        private List<MonsterRule> LoadMonsterRulesTSV(string tsvPath)
        {
            var rules = new List<MonsterRule>();
            try
            {
                var lines = System.IO.File.ReadAllLines(tsvPath);
                // Skip header row
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var c = line.Split('\t');
                    if (c.Length < 4) continue;

                    var mr = new MonsterRule
                    {
                        Name = c[0],
                        Priority = int.Parse(c[1]),
                        DamageType = c[2],
                        WeaponId = int.Parse(c[3])
                    };
                    if (c.Length >= 14)
                    {
                        mr.Fester = bool.Parse(c[4]);
                        mr.Broadside = bool.Parse(c[5]);
                        mr.GravityWell = bool.Parse(c[6]);
                        mr.Imperil = bool.Parse(c[7]);
                        mr.Yield = bool.Parse(c[8]);
                        mr.Vuln = bool.Parse(c[9]);
                        mr.UseArc = bool.Parse(c[10]);
                        mr.UseRing = bool.Parse(c[11]);
                        mr.UseStreak = bool.Parse(c[12]);
                        mr.UseBolt = bool.Parse(c[13]);
                    }
                    if (c.Length >= 17)
                    {
                        mr.ExVuln = c[14];
                        mr.OffhandId = int.Parse(c[15]);
                        mr.PetDamage = c[16];
                    }
                    rules.Add(mr);
                }
            }
            catch (Exception ex)
            {
                try { Host.Actions.AddChatText($"[RynthAi] Monster TSV load error: {ex.Message}", 2); } catch { }
            }
            return rules;
        }

        private void OnChatCommand(object sender, ChatParserInterceptEventArgs e)
        {
            _commandProcessor?.Execute(e.Text, e);
        }

        // --- API Imports for FPS Governor ---
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private int _fpsDebugCounter = 0;

        private void OnRenderFrame(object sender, EventArgs e)
        {
            // === 1. FPS GOVERNOR (ThwargLauncher Safe PID Version) ===
            // This manages CPU usage by throttling frames when the window is in the background.
            if (_settings.EnableFPSLimit)
            {
                // Get the window currently on top of everything
                IntPtr foregroundWindow = GetForegroundWindow();

                // Get the Process ID of that foreground window
                GetWindowThreadProcessId(foregroundWindow, out uint foregroundPid);

                // Get the Process ID of THIS specific AC client
                uint thisPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

                // If the PIDs match, this specific client is the one being looked at
                bool isFocused = (foregroundPid == thisPid);
                int targetFPS = isFocused ? _settings.TargetFPSFocused : _settings.TargetFPSBackground;

                // --- THE SPY (Debug logging to chat) ---
                _fpsDebugCounter++;
                if (_fpsDebugCounter >= (isFocused ? 200 : 50))
                {
                    _fpsDebugCounter = 0;
                    // Uncomment the line below if you need to debug FPS stability
                    // Host.Actions.AddChatText($"[RynthFPS] PID: {thisPid} | Mode: {(isFocused ? "FOCUSED" : "BACKGROUND")} | Target: {targetFPS} FPS", 1);
                }

                // Calculate timing: 1000ms / Target
                double minFrameTimeMs = 1000.0 / Math.Max(targetFPS, 1);

                while (_frameTimer.Elapsed.TotalMilliseconds < minFrameTimeMs)
                {
                    // Use Sleep(1) for background to save massive CPU; Sleep(0) for focus to maintain responsiveness
                    System.Threading.Thread.Sleep(isFocused ? 0 : 1);
                }
                _frameTimer.Restart();
            }

            // === 2. DEFENSIVE LUA HEARTBEAT ===
            // Ensures the Lua environment stays alive and ticks the OnBotTick() function.
            if (DateTime.Now > _nextLuaTick)
            {
                _nextLuaTick = DateTime.Now.AddMilliseconds(250);
                if (_luaManager != null && _settings.IsMacroRunning)
                {
                    // Safety check to prevent "nil value" crashes if the user hasn't defined OnBotTick
                    _luaManager.ExecuteString("if OnBotTick ~= nil then OnBotTick() end");
                }
            }

            // === 2.5 NATIVE JUMPER ===
            // Ticks the jump state machine (runs even when macro is paused for jumping)
            _jumper?.Think();

            // === 3. CORE BOT LOGIC ===
            // If the macro is off, we stop here. 
            // Note: UI Rendering should stay in MainHud_OnRender to keep the ImGui lifecycle clean.
            if (!_settings.IsMacroRunning) return;

            // 3a. Meta System (Decides what the bot should be doing)
            _metaManager?.Think();

            // 3b. Maintenance (Buffs, Vitals, Recharging) — ALWAYS runs first regardless of priority
            _buffManager?.OnHeartbeat();

            // If buffing claimed the state, yield everything else this frame
            if (_settings.CurrentState == "Buffing") return;

            // 3c. INVENTORY MANAGEMENT
            // Salvage blocks everything until complete — prevents items piling up.
            if (_lootManager != null && _settings.IsMacroRunning)
            {
                _lootManager.ProcessSalvage();
                
                if (_lootManager.IsSalvageProcessing)
                    return; // Block combat, nav, AND looting until salvage finishes
                
                // Missile crafting runs after salvage, before stack/cram.
                if (_missileCraftingManager != null)
                {
                    _missileCraftingManager.ProcessCrafting();
                    if (_missileCraftingManager.IsCrafting)
                        return; // Block combat, nav, looting until crafting finishes
                }

                _lootManager.ProcessStack();
                _lootManager.ProcessCram();
            }

            // 3d. PRIORITY DISPATCHER
            // Reset the traffic light to Idle at the start of each frame.
            // Each subsystem will claim the state if it has active work to do.
            // This prevents stale states from previous frames blocking subsystems
            // (e.g., "Looting" state lingering and preventing Navigation from running).
            _settings.CurrentState = "Idle";

            // Determines the order that Combat, Navigation, and Looting run in.
            //
            //   Default:                           Combat → Loot → Nav
            //   NavPriorityBoost ON:               Nav → Combat → Loot
            //   LootPriorityBoost ON:              Loot → Combat → Nav
            //   Both boosts ON:                    Nav → Loot → Combat
            //
            // Each subsystem checks _settings.CurrentState as a traffic light.
            // The FIRST system to claim the state blocks the others.

            bool navBoost  = _settings.BoostNavPriority;
            bool lootBoost = _settings.BoostLootPriority;

            if (navBoost && lootBoost)
            {
                RunNavigation();
                RunLooting();
                RunCombat();
            }
            else if (navBoost)
            {
                RunNavigation();
                RunCombat();
                RunLooting();
            }
            else if (lootBoost)
            {
                RunLooting();
                RunCombat();
                RunNavigation();
            }
            else
            {
                RunCombat();
                RunLooting();
                RunNavigation();
            }
        }

        // ── Priority Dispatcher Helpers ──────────────────────────────────────
        // Each checks if the state is still "Idle" before claiming it.
        // Once a system claims the state, later systems in the priority chain yield.

        private void RunCombat()
        {
            if (!_settings.EnableCombat) return;
            // Yield if a higher-priority system already claimed the state this frame
            if (_settings.CurrentState != "Idle") return;
            _combatManager?.OnHeartbeat();
        }

        private void RunNavigation()
        {
            // Yield if a higher-priority system already claimed the state this frame
            if (_settings.CurrentState != "Idle") return;
            // Pause nav if there are unlooted corpses nearby — let looting finish first
            if (_settings.EnableLooting && (_lootManager?.HasUnlootedCorpsesNearby() ?? false)) return;
            bool isFighting = _settings.EnableCombat && (_combatManager?.activeTargetId ?? 0) != 0;
            _navigationManager?.ProcessNavigation(isFighting);
        }

        private void RunLooting()
        {
            // Yield if a higher-priority system already claimed the state this frame
            if (_settings.CurrentState != "Idle" && _settings.CurrentState != "Looting") return;
            _lootManager?.OnHeartbeat();
        }

        private void OnChatBoxMessage(object sender, ChatTextInterceptEventArgs e)
        {
            try
            {
                // Feed the chat text to your sub-managers
                _metaManager?.HandleChat(e.Text);
                _combatManager?.HandleChatForDebuffs(e.Text);
                _missileCraftingManager?.HandleChat(e.Text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.ToString());
            }
        }



        private double ParseCoord(string val)
        {
            if (string.IsNullOrEmpty(val)) return 0;

            // Remove letters and whitespace, then parse the number
            string clean = System.Text.RegularExpressions.Regex.Replace(val, "[^0-9.-]", "");
            if (!double.TryParse(clean, out double result)) return 0;

            // If it contains S or W, it must be negative for the coordinate system
            if (val.ToUpper().Contains("S") || val.ToUpper().Contains("W"))
                return -result;

            return result;
        }
    }
}
