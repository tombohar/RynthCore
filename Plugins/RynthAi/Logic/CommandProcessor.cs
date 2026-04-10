using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using Decal.Filters;
using RynthAi.Meta;

namespace NexSuite.Plugins.RynthAi
{
    public class CommandProcessor
    {
        private readonly PluginCore _plugin;
        private readonly UISettings _settings;
        private readonly MetaManager _meta;
        private readonly NavigationManager _nav;
        private readonly BuffManager _buff;
        private readonly CombatManager _combat;
        private readonly LootManager _loot;
        private readonly LuaManager _lua;
        private readonly RynthJumper _jumper;
        private readonly SpellManager _spellManager;
        private readonly FellowshipTracker _fellowship;

        public CommandProcessor(PluginCore plugin, UISettings settings, MetaManager meta, NavigationManager nav, BuffManager buff, CombatManager combat, LootManager loot, LuaManager lua, RynthJumper jumper, SpellManager spellManager, FellowshipTracker fellowship)
        {
            _plugin = plugin;
            _settings = settings;
            _meta = meta;
            _nav = nav;
            _buff = buff;
            _combat = combat;
            _loot = loot;
            _lua = lua;
            _jumper = jumper;
            _spellManager = spellManager;
            _fellowship = fellowship;
        }

        public void Execute(string text, ChatParserInterceptEventArgs e)
        {
            try
            {
                string trimmed = text.Trim();
                bool isNA = trimmed.StartsWith("/na", StringComparison.OrdinalIgnoreCase);
                bool isVT = trimmed.StartsWith("/vt", StringComparison.OrdinalIgnoreCase);
                bool isLua = trimmed.StartsWith("/lua", StringComparison.OrdinalIgnoreCase);

                if (!isNA && !isVT && !isLua) return;

                string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                // --- Handle Top-Level /lua command ---
                if (isLua)
                {
                    e.Eat = true;
                    if (parts.Length < 2)
                    {
                        _plugin.PluginHost.Actions.AddChatText("[RynthAi] Usage: /lua <script>", 1);
                        return;
                    }
                    string script = trimmed.Substring(5).Trim();
                    _lua?.ExecuteString(script);
                    return;
                }

                if (parts.Length < 2)
                {
                    PrintHelp();
                    e.Eat = true;
                    return;
                }

                e.Eat = true;
                string cmd = parts[1].ToLower();

                switch (cmd)
                {
                    // --- NEW COMMANDS ---
                    case "lua":
                        if (parts.Length >= 3)
                        {
                            string script = string.Join(" ", parts, 2, parts.Length - 2);
                            _lua?.ExecuteString(script);
                        }
                        break;

                    case "opt":
                        HandleOptCommand(parts);
                        break;

                    case "addnavpt":
                        HandleAddNavPoint();
                        break;

                    // --- CORE MACRO CONTROLS ---
                    case "start":
                    case "on":
                        _settings.IsMacroRunning = true;
                        _plugin.SaveSettings();
                        _plugin.PluginHost.Actions.AddChatText("[RynthAi] Macro STARTED.", 5);
                        break;

                    case "stop":
                    case "off":
                        _settings.IsMacroRunning = false;
                        _buff?.CancelBuffing();
                        _nav?.Stop();
                        _plugin.SaveSettings();
                        _plugin.PluginHost.Actions.AddChatText("[RynthAi] Macro STOPPED.", 5);
                        break;

                    case "status":
                        _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Macro: {(_settings.IsMacroRunning ? "ON" : "OFF")} | State: {_settings.CurrentState} | Nav: {Path.GetFileName(_settings.CurrentNavPath)} | Meta: {Path.GetFileName(_settings.CurrentMetaPath)}", 1);
                        break;

                    // --- SUBSYSTEMS & PROFILES ---
                    case "settings":
                        HandleSettingsCommand(parts);
                        break;

                    case "nav":
                    case "loot":
                    case "meta":
                        HandleSubsystemCommand(cmd, parts, text);
                        break;

                    case "setmetastate":
                        if (parts.Length >= 3)
                        {
                            string newState = string.Join(" ", parts, 2, parts.Length - 2);
                            _settings.CurrentState = newState;
                            _settings.ForceStateReset = true;
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Meta State forced to: {newState}", 5);
                        }
                        break;

                    case "db":
                    case "debugbuffs":
                        _buff?.PrintBuffDebug();
                        break;

                    case "dn":
                    case "debugnav":
                        if (_nav != null)
                        {
                            _nav.DebugNav = !_nav.DebugNav;
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Nav debug: {(_nav.DebugNav ? "ON" : "OFF")}", 5);
                        }
                        break;

                    case "wd":
                    case "watchdog":
                        _meta?.PrintWatchdogStatus();
                        break;

                    case "fakedeath":
                        _plugin.PluginHost.Actions.AddChatText("[RynthAi] Faking character death trigger...", 5);
                        _meta?.HandleChat("You have died.");
                        break;

                    case "rebuff":
                    case "forcebuff":
                        _buff?.ForceFullRebuff();
                        _plugin.PluginHost.Actions.AddChatText("[RynthAi] Force-buff sequence initiated.", 5);
                        break;

                    case "cancelforcebuff":
                        _buff?.CancelBuffing();
                        _plugin.PluginHost.Actions.AddChatText("[RynthAi] Force-buff sequence cancelled.", 5);
                        break;

                    case "raycast":
                        HandleRaycastCommand(parts);
                        break;

                    case "lostest":
                        if (_combat != null)
                        {
                            var diagLines = _combat.RunLosTestDiag(_plugin.PluginCoreManager);
                            foreach (var line in diagLines)
                                _plugin.PluginHost.Actions.AddChatText("[RynthAi LOS] " + line, 1);
                        }
                        else
                        {
                            _plugin.PluginHost.Actions.AddChatText("[RynthAi] Combat manager not initialized", 1);
                        }
                        break;

                    case "blacklist":
                        if (parts.Length >= 3 && parts[2].ToLower() == "clear")
                        {
                            _plugin.PluginHost.Actions.AddChatText("[RynthAi] Blacklist cleared.", 5);
                        }
                        else
                        {
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Blacklist: attempts={_settings.BlacklistAttempts}, timeout={_settings.BlacklistTimeoutSec}s", 1);
                            _plugin.PluginHost.Actions.AddChatText("[RynthAi] Usage: /na blacklist clear", 1);
                        }
                        break;

                    // --- COMBAT & UTILITY STUBS ---
                    case "deletemonster":
                        int targetId = _plugin.PluginHost.Actions.CurrentSelection;
                        if (targetId != 0) _plugin.PluginHost.Actions.AddChatText($"[RynthAi] DeleteMonster stubbed target: {targetId}", 5);
                        break;

                    case "setattackbar":
                        if (parts.Length >= 3)
                        {
                            string target = "melee"; // default
                            float rawVal;
                            if (parts.Length >= 4 && float.TryParse(parts[3], out rawVal))
                            {
                                target = parts[2].ToLower(); // "melee" or "missile"
                            }
                            else if (float.TryParse(parts[2], out rawVal))
                            {
                                // /na setattackbar 0.5 → sets both
                                target = "both";
                            }
                            else
                            {
                                _plugin.PluginHost.Actions.AddChatText("[RynthAi] Usage: /na setattackbar [melee|missile] 0-1", 1);
                                break;
                            }
                            int pct = Math.Max(0, Math.Min(100, (int)(rawVal * 100)));
                            if (target == "missile") { _settings.MissileAttackPower = pct; }
                            else if (target == "melee") { _settings.MeleeAttackPower = pct; }
                            else { _settings.MeleeAttackPower = pct; _settings.MissileAttackPower = pct; }
                            _plugin.SaveSettings();
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Attack power ({target}): {pct}%", 5);
                        }
                        else
                        {
                            _plugin.PluginHost.Actions.AddChatText("[RynthAi] Usage: /na setattackbar [melee|missile] 0-1", 1);
                        }
                        break;

                    case "autoattackpower":
                        if (parts.Length >= 3)
                        {
                            if (parts[2].Equals("auto", StringComparison.OrdinalIgnoreCase))
                            {
                                _settings.MeleeAttackPower = -1;
                                _settings.MissileAttackPower = -1;
                                _plugin.PluginHost.Actions.AddChatText("[RynthAi] Attack power set to AUTO (full power) for both modes", 5);
                            }
                            else if (int.TryParse(parts[2], out int pwr))
                            {
                                pwr = Math.Max(0, Math.Min(100, pwr));
                                _settings.MeleeAttackPower = pwr;
                                _settings.MissileAttackPower = pwr;
                                _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Attack power set to {pwr}% for both modes", 5);
                            }
                            _plugin.SaveSettings();
                        }
                        else
                        {
                            string mPwr = _settings.MeleeAttackPower < 0 ? "AUTO" : $"{_settings.MeleeAttackPower}%";
                            string rPwr = _settings.MissileAttackPower < 0 ? "AUTO" : $"{_settings.MissileAttackPower}%";
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Melee: {mPwr}, Missile: {rPwr}. Usage: /na autoattackpower [auto|0-100]", 1);
                        }
                        break;

                    case "tapjump":
                        if (_jumper != null) _jumper.TapJump();
                        break;

                    case "spelldump":
                        // /na spelldump Focus — shows all spells containing "Focus"
                        if (parts.Length >= 3 && _spellManager != null)
                        {
                            string keyword = string.Join(" ", parts, 2, parts.Length - 2).ToLower();
                            int found = 0;
                            foreach (var kvp in _spellManager.SpellDictionary)
                            {
                                if (kvp.Key.ToLower().Contains(keyword))
                                {
                                    bool known = false;
                                    try { known = _plugin.PluginCoreManager.CharacterFilter.IsSpellKnown(kvp.Value); } catch { }
                                    _plugin.PluginHost.Actions.AddChatText($"  {kvp.Key} (id={kvp.Value}) {(known ? "[KNOWN]" : "[unknown]")}", 1);
                                    found++;
                                }
                            }
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Found {found} spells matching '{keyword}'", 1);
                        }
                        else
                        {
                            _plugin.PluginHost.Actions.AddChatText("[RynthAi] Usage: /na spelldump <keyword>", 1);
                        }
                        break;

                    case "bufftest":
                        // /na bufftest Focus Self — traces the full buff lookup path
                        if (parts.Length >= 3 && _spellManager != null)
                        {
                            string buffName = string.Join(" ", parts, 2, parts.Length - 2);
                            string cleanBase = buffName.Replace(" Self", "").Trim();
                            _plugin.PluginHost.Actions.AddChatText($"[BuffTest] Input: '{buffName}' → cleanBase: '{cleanBase}'", 1);

                            // Check what GetHighestSpellTier returns
                            var testSkills = new[] {
                                CharFilterSkillType.CreatureEnchantment,
                                CharFilterSkillType.LifeMagic,
                                CharFilterSkillType.ItemEnchantment
                            };
                            foreach (var sk in testSkills)
                            {
                                int tier = _spellManager.GetHighestSpellTier(sk);
                                _plugin.PluginHost.Actions.AddChatText($"[BuffTest] {sk} → maxTier={tier}", 1);
                            }

                            // Trace the exact lookups
                            string[] tries = new[] {
                                $"Incantation of {cleanBase} Self",
                                $"Incantation of {cleanBase}",
                            };
                            foreach (string tryName in tries)
                            {
                                bool inDict = _spellManager.SpellDictionary.ContainsKey(tryName);
                                int id = inDict ? _spellManager.SpellDictionary[tryName] : 0;
                                bool known = false;
                                if (id > 0) try { known = _plugin.PluginCoreManager.CharacterFilter.IsSpellKnown(id); } catch { }
                                _plugin.PluginHost.Actions.AddChatText($"[BuffTest] Try '{tryName}' → inDict={inDict}, id={id}, known={known}", 1);
                            }

                            // Now call the actual method
                            int result = _spellManager.GetDynamicSelfBuffId(buffName, CharFilterSkillType.CreatureEnchantment);
                            string resultName = "(none)";
                            if (result > 0 && _spellManager.SpellDictionary.Values.Contains(result))
                            {
                                foreach (var kvp in _spellManager.SpellDictionary)
                                {
                                    if (kvp.Value == result) { resultName = kvp.Key; break; }
                                }
                            }
                            _plugin.PluginHost.Actions.AddChatText($"[BuffTest] GetDynamicSelfBuffId returned: {result} = '{resultName}'", 1);
                        }
                        else
                        {
                            _plugin.PluginHost.Actions.AddChatText("[RynthAi] Usage: /na bufftest Focus Self", 1);
                        }
                        break;

                    case "fellow":
                    case "fellowship":
                        // /na fellow [subcommand] — fellowship info and actions
                        if (_fellowship == null)
                        {
                            _plugin.PluginHost.Actions.AddChatText("[RynthAi] Fellowship tracker not initialized.", 1);
                            break;
                        }

                        string fellowSub = (parts.Length >= 3) ? parts[2].ToLower() : "status";

                        if (!_fellowship.IsInFellowship && fellowSub != "help")
                        {
                            _plugin.PluginHost.Actions.AddChatText("[RynthAi] Not in a fellowship.", 1);
                            break;
                        }

                        switch (fellowSub)
                        {
                            case "status":
                                _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Fellowship: \"{_fellowship.FellowshipName}\"", 1);
                                _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Members: {_fellowship.MemberCount} | Leader: {(_fellowship.IsLeader ? "ME" : $"0x{_fellowship.LeaderId:X8}")} | Open: {_fellowship.IsOpen} | Locked: {_fellowship.IsLocked} | ShareXP: {_fellowship.ShareXP}", 1);
                                {
                                    int fidx = 0;
                                    foreach (string mName in _fellowship.GetMemberNames())
                                    {
                                        int mId = _fellowship.GetMemberId(fidx);
                                        bool isLdr = (mId == _fellowship.LeaderId);
                                        _plugin.PluginHost.Actions.AddChatText($"  [{fidx}] {mName} (0x{mId:X8}){(isLdr ? " [LEADER]" : "")}", 1);
                                        fidx++;
                                    }
                                }
                                break;

                            case "leader":
                                {
                                    int leaderId = _fellowship.LeaderId;
                                    string leaderName = "(unknown)";
                                    int li = 0;
                                    foreach (string mn in _fellowship.GetMemberNames())
                                    {
                                        if (_fellowship.GetMemberId(li) == leaderId) { leaderName = mn; break; }
                                        li++;
                                    }
                                    _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Leader: {leaderName} (0x{leaderId:X8}){(_fellowship.IsLeader ? " (you)" : "")}", 1);
                                }
                                break;

                            case "count":
                                _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Fellowship member count: {_fellowship.MemberCount}", 1);
                                break;

                            case "names":
                                {
                                    var names = new List<string>(_fellowship.GetMemberNames());
                                    _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Members ({names.Count}): {string.Join(", ", names)}", 1);
                                }
                                break;

                            case "name":
                                _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Fellowship name: \"{_fellowship.FellowshipName}\"", 1);
                                break;

                            case "open":
                                _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Open: {_fellowship.IsOpen}", 1);
                                break;

                            case "locked":
                                _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Locked: {_fellowship.IsLocked}", 1);
                                break;

                            case "sharexp":
                                _plugin.PluginHost.Actions.AddChatText($"[RynthAi] ShareXP: {_fellowship.ShareXP}", 1);
                                break;

                            case "ismember":
                                if (parts.Length >= 4)
                                {
                                    string checkName = string.Join(" ", parts, 3, parts.Length - 3);
                                    bool found = _fellowship.IsMember(checkName);
                                    _plugin.PluginHost.Actions.AddChatText($"[RynthAi] IsMember(\"{checkName}\"): {found}", 1);
                                }
                                else
                                {
                                    _plugin.PluginHost.Actions.AddChatText("[RynthAi] Usage: /na fellow ismember <name>", 1);
                                }
                                break;

                            case "help":
                                _plugin.PluginHost.Actions.AddChatText("[RynthAi] /na fellow [status|leader|count|names|name|open|locked|sharexp|ismember <name>]", 1);
                                break;

                            default:
                                _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Unknown fellow subcommand: {fellowSub}. Try /na fellow help", 1);
                                break;
                        }
                        break;

                    case "corpsedebug":
                        // /na corpsedebug — check the currently selected corpse's ownership info
                        {
                            int selId = _plugin.PluginHost.Actions.CurrentSelection;
                            if (selId == 0)
                            {
                                _plugin.PluginHost.Actions.AddChatText("[RynthAi] Select a corpse first.", 1);
                                break;
                            }
                            var wo = _plugin.PluginCoreManager.WorldFilter[selId];
                            if (wo == null)
                            {
                                _plugin.PluginHost.Actions.AddChatText("[RynthAi] Selected object not found.", 1);
                                break;
                            }
                            string corpName = wo.Name ?? "(null)";
                            string longDesc = "";
                            try { longDesc = wo.Values((StringValueKey)16, ""); } catch { }
                            string myName = _plugin.PluginCoreManager.CharacterFilter.Name;

                            // Extract killer
                            string killerName = "(unknown)";
                            const string prefix = "Killed by ";
                            int idx = longDesc.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                killerName = longDesc.Substring(idx + prefix.Length).TrimEnd('.', ' ', '\n', '\r');
                            }

                            bool isMe = killerName.Equals(myName, StringComparison.OrdinalIgnoreCase);
                            bool isFellow = _fellowship != null && _fellowship.IsMember(killerName);

                            _plugin.PluginHost.Actions.AddChatText($"[CorpseDebug] Name: {corpName}", 1);
                            _plugin.PluginHost.Actions.AddChatText($"[CorpseDebug] LongDesc: '{longDesc}'", 1);
                            _plugin.PluginHost.Actions.AddChatText($"[CorpseDebug] Killer: '{killerName}'", 1);
                            _plugin.PluginHost.Actions.AddChatText($"[CorpseDebug] IsMe: {isMe}, IsFellow: {isFellow}", 1);
                            _plugin.PluginHost.Actions.AddChatText($"[CorpseDebug] LootOwnership: {_settings.LootOwnership} (0=Mine, 1=Fellow, 2=All)", 1);
                        }
                        break;

                    // ── NATIVE JUMP SYSTEM ──
                    case "jump":
                    case "jumpsw":
                    case "jumpsx":
                    case "jumpswz":
                    case "jumpswx":
                    case "jumpswzx":
                    case "jumpswzxc":
                        HandleJumpCommand(cmd, parts);
                        break;

                    case "face":
                        if (parts.Length >= 3 && double.TryParse(parts[2], out double faceHeading))
                        {
                            _plugin.PluginCoreManager.Actions.Heading = (float)faceHeading;
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Facing {faceHeading:F0}", 5);
                        }
                        break;

                    case "reverseroute":
                        if (parts.Length >= 3)
                        {
                            bool rev = parts[2].ToLower() == "true";
                            _nav?.SetReverse(rev);
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Reverse Route: {rev}", 5);
                        }
                        break;

                    case "reverseroutequery":
                        bool isRev = _nav?.IsReversed ?? false;
                        _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Reverse Route is: {isRev}", 5);
                        break;

                    case "mexec":
                        if (parts.Length >= 3)
                        {
                            string expr = text.Substring(text.IndexOf("mexec") + 5).Trim();
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] MEXEC: {expr}", 5);
                            try { _plugin.PluginHost.Actions.InvokeChatParser($"/ub mexec {expr}"); }
                            catch (Exception ex) { _plugin.PluginHost.Actions.AddChatText($"[RynthAi] UB Error: {ex.Message}", 1); }
                        }
                        break;

                    case "echo":
                        string echoText = string.Join(" ", parts, 2, parts.Length - 2);
                        _plugin.PluginHost.Actions.AddChatText($"[RynthAi] {echoText}", 5);
                        break;

                    case "ui":
                        _plugin.ToggleUI();
                        break;

                    // ── COMBAT ACTION DIAGNOSTICS ──
                    case "probe":
                        // /na probe — show CombatActionHelper initialization status
                        {
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] CombatActionHelper initialized: {CombatActionHelper.IsInitialized}", 1);
                            _plugin.PluginHost.Actions.AddChatText("[RynthAi] (Check chat log at plugin load for detailed scan results)", 1);
                            if (!CombatActionHelper.IsInitialized)
                                _plugin.PluginHost.Actions.AddChatText("[RynthAi] Pattern scan failed — CM_Combat functions not found in this acclient.exe build", 1);
                        }
                        break;

                    case "testattack":
                        // /na testattack — attempts a single melee attack on current selection
                        // Use this with combat OFF to isolate the direct call
                        {
                            int selId = _plugin.PluginHost.Actions.CurrentSelection;
                            if (selId == 0)
                            {
                                _plugin.PluginHost.Actions.AddChatText("[RynthAi] Select a target first.", 1);
                                break;
                            }
                            if (!CombatActionHelper.IsInitialized)
                            {
                                _plugin.PluginHost.Actions.AddChatText("[RynthAi] CombatActionHelper not initialized.", 1);
                                break;
                            }
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Sending MeleeAttack to target 0x{selId:X8}, height=Medium, power=0.5...", 1);
                            bool ok = CombatActionHelper.MeleeAttack((uint)selId, CombatActionHelper.ATTACK_HEIGHT_MEDIUM, 0.5f);
                            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] MeleeAttack returned: {ok}", 1);
                        }
                        break;

                    default:
                        _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Unknown command: {cmd}", 1);
                        break;
                }
            }
            catch (Exception ex)
            {
                _plugin.PluginHost.Actions.AddChatText($"[RynthAi Cmd Error] {ex.Message}", 1);
            }
        }

        private void HandleOptCommand(string[] parts)
        {
            if (parts.Length < 5 || !parts[2].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.PluginHost.Actions.AddChatText("[RynthAi] Usage: /na opt set <setting> <value>", 1);
                return;
            }

            string setting = parts[3].ToLower();
            string val = parts[4].ToLower();

            switch (setting)
            {
                case "enablecombat": _settings.EnableCombat = (val == "true"); break;
                case "enablenav": _settings.EnableNavigation = (val == "true"); break;
                case "enablelooting": _settings.EnableLooting = (val == "true"); break;
                case "summonpets": _settings.SummonPets = (val == "true"); break;
                case "enablemeta": _settings.EnableMeta = (val == "true"); break;
                case "attackdistance":
                    if (float.TryParse(val, out float dist)) _settings.FollowNavMin = dist;
                    break;
                default:
                    _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Unknown option: {setting}", 1);
                    return;
            }

            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Option {setting} set to {val}", 5);
            _plugin.SaveSettings();
        }

        private void HandleAddNavPoint()
        {
            if (_settings.CurrentRoute == null)
            {
                _plugin.PluginHost.Actions.AddChatText("[RynthAi] Error: No route loaded.", 1);
                return;
            }

            var coords = _plugin.PluginCoreManager.WorldFilter[_plugin.PluginCoreManager.CharacterFilter.Id].Coordinates();

            // FIX: Using Object Initializer to avoid constructor errors
            var newPoint = new NavPoint
            {
                NS = (float)coords.NorthSouth,
                EW = (float)coords.EastWest,
                Z = (float)_plugin.PluginCoreManager.Actions.LocationZ
            };

            _settings.CurrentRoute.Points.Add(newPoint);
            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Added Nav Point at: {newPoint.NS:F2}N, {newPoint.EW:F2}E", 5);
            _plugin.UI?.RouteTab?.UpdateRouteGraphics();
        }

        private void HandleSettingsCommand(string[] parts)
        {
            if (parts.Length < 3) return;
            string sub = parts[2].ToLower();
            string fileName = (parts.Length >= 4) ? parts[3] : "default";
            if (!fileName.EndsWith(".json")) fileName += ".json";

            string server = _plugin.PluginCoreManager.CharacterFilter.Server;
            string name = _plugin.PluginCoreManager.CharacterFilter.Name;
            string globalDir = @"C:\Games\DecalPlugins\NexSuite\RynthAi\GlobalSettings";
            string charDir = Path.Combine(@"C:\Games\DecalPlugins\NexSuite\RynthAi\SettingsProfiles", server, name);

            if (!Directory.Exists(globalDir)) Directory.CreateDirectory(globalDir);
            if (!Directory.Exists(charDir)) Directory.CreateDirectory(charDir);

            string globalPath = Path.Combine(globalDir, fileName);
            string charPath = Path.Combine(charDir, fileName);

            switch (sub)
            {
                case "save": _plugin.SaveSettings(globalPath); break;
                case "load": _plugin.LoadSettings(globalPath); break;
                case "savechar": _settings.MineOnly = true; _plugin.SaveSettings(charPath); break;
                case "loadchar": _settings.MineOnly = true; _plugin.LoadSettings(charPath); break;
            }
            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Settings {sub} successful: {fileName}", 5);
        }

        private void HandleSubsystemCommand(string type, string[] parts, string fullText)
        {
            if (parts.Length < 4) return;
            string action = parts[2].ToLower();
            string fileName = string.Join(" ", parts, 3, parts.Length - 3);

            string folder = type == "nav" ? _plugin.NavFolder : (type == "meta" ? _plugin.MetaFolder : _plugin.LootFolder);
            string ext = type == "nav" ? ".nav" : (type == "meta" ? ".met" : ".utl");

            if (!fileName.EndsWith(ext)) fileName += ext;
            string fullPath = Path.Combine(folder, fileName);

            if (action == "save")
            {
                if (!File.Exists(fullPath)) File.WriteAllText(fullPath, "");
                if (type == "nav") _settings.CurrentRoute?.Save(fullPath);
                else if (type == "meta") { _settings.CurrentMetaPath = fullPath; _meta?.SaveCurrentProfile(); }
                else if (type == "loot") { _settings.EnableLooting = true; _loot?.SaveProfile(fullPath); }
            }
            else if (action == "load")
            {
                if (!File.Exists(fullPath))
                {
                    File.WriteAllText(fullPath, "");
                    _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Created new {type} profile: {fileName}", 5);
                }

                if (type == "nav") 
                { 
                    _settings.CurrentNavPath = fullPath; 
                    _settings.CurrentRoute = VTankNavParser.Load(fullPath); 
                    _nav?.ResetRouteState();
                    
                    // Find nearest waypoint for Circular/Linear routes
                    if (_settings.CurrentRoute.RouteType == NavRouteType.Circular || 
                        _settings.CurrentRoute.RouteType == NavRouteType.Linear)
                    {
                        _settings.ActiveNavIndex = _nav != null ? _nav.FindNearestWaypoint(_settings.CurrentRoute) : 0;
                    }
                    else
                    {
                        _settings.ActiveNavIndex = 0;
                    }
                    
                    // Refresh route graphics
                    if (_plugin.UI?.RouteTab != null) 
                        _plugin.UI.RouteTab.NeedsRouteGraphicsRefresh = true;
                }
                else if (type == "meta") { _settings.CurrentMetaPath = fullPath; _meta?.LoadProfile(fullPath); }
                else if (type == "loot") { _settings.EnableLooting = true; _loot?.LoadProfile(fullPath); }
            }

            SyncUI(type, fileName, folder);
            _plugin.SaveSettings();
            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] {type.ToUpper()} {action} successful: {fileName}", 5);
        }

        private void SyncUI(string type, string fileName, string folder)
        {
            if (_plugin.UI == null) return;
            if (type == "nav") _plugin.UI.RefreshNavFiles();
            if (type != "meta") return;

            _plugin.UI.RefreshMetaFiles();
            if (Directory.Exists(folder))
            {
                string[] files = Directory.GetFiles(folder, "*.met");
                Array.Sort(files);
                for (int i = 0; i < files.Length; i++)
                {
                    if (Path.GetFileName(files[i]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        _settings.MetaProfileIdx = i + 1;
                        break;
                    }
                }
            }
        }

        private void HandleRaycastCommand(string[] parts)
        {
            if (parts.Length > 2)
            {
                _settings.EnableRaycasting = (parts[2].ToLower() == "on");
                _plugin.SaveSettings();
            }
            else
            {
                string rayStatus = _combat?.GetRaycastStatus() ?? "No info";
                foreach (var line in rayStatus.Split('\n')) _plugin.PluginHost.Actions.AddChatText("[RynthAi] " + line.Trim(), 1);
            }
        }

        /// <summary>
        /// Handles /na jump commands natively using RynthJumper.
        /// Format: /na jump[swzxc] [heading] [holdtime]
        ///   - /na jump = tap jump
        ///   - /na jumpsw 180 500 = face south, jump forward with 500/1000 power
        ///   - /na jumpsx 300 = jump backward with 300/1000 power
        /// </summary>
        private void HandleJumpCommand(string cmd, string[] parts)
        {
            if (_jumper == null) return;
            if (_jumper.IsJumping)
            {
                _plugin.PluginHost.Actions.AddChatText("[RynthAi] Already jumping — wait for completion", 1);
                return;
            }

            // Simple tap jump
            if (cmd == "jump" && parts.Length <= 2)
            {
                _jumper.TapJump();
                return;
            }

            // Parse hold time
            int holdMs = 0;
            double heading = -1; // -1 = use current heading

            if (parts.Length >= 4 && int.TryParse(parts[3], out int ht))
                holdMs = ht;
            else if (parts.Length >= 3 && int.TryParse(parts[2], out int ht2))
                holdMs = ht2;

            if (parts.Length >= 3 && double.TryParse(parts[2], out double hd))
            {
                heading = hd;
                // If heading was parsed as first arg, holdtime is second
                if (parts.Length >= 4 && int.TryParse(parts[3], out int ht3))
                    holdMs = ht3;
            }

            // Parse direction keys from command suffix
            bool shift = cmd.Contains("s");
            bool forward = cmd.Contains("w");
            bool backward = cmd.Contains("x");
            bool slideLeft = cmd.Contains("z");
            bool slideRight = cmd.Contains("c");

            _jumper.StartJump(heading, holdMs, shift, forward, backward, slideLeft, slideRight);
            _plugin.PluginHost.Actions.AddChatText($"[RynthAi] Jumping: heading={heading:F0} hold={holdMs}ms keys={cmd.Replace("jump","")}", 5);
        }

        private void PrintHelp()
        {
            _plugin.PluginHost.Actions.AddChatText("[RynthAi] Commands: /na [start|stop|status], /na lostest, /na setattackbar 0-1, /na autoattackpower [auto|0-1]", 1);
            _plugin.PluginHost.Actions.AddChatText("[RynthAi] /na fellow [status|leader|count|names|name|ismember <n>], /na probe, /na testattack", 1);
            _plugin.PluginHost.Actions.AddChatText("[RynthAi] /na jump[sw|sx|swz|swx|swzx|swzxc] [heading] [holdtime], /na blacklist [clear], /lua <script>", 1);
        }
    }
}