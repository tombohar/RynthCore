using System.Collections.Generic;
using RynthCore.HookResolver.Core.Validation;
using RynthCore.HookResolver.Core.Validation.Rules;

namespace RynthCore.HookResolver.Core.Symbols;

public static class AllSymbols
{
    // Shared prologue used by all CombatActionHooks dispatch wrappers
    // (small functions: sub esp,0xC; push ebx; push esi; push edi; call rel32).
    private static readonly byte[] CombatActionPrologue = [0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8];

    // The three helper functions every CombatActionHooks wrapper CALLs,
    // verified empirically across all 7 siblings (incl. CastSpell @0x006A3E60)
    // in the SHA-keyed manifest. A candidate that doesn't call all three is
    // NOT a combat-action wrapper — this is what rejects a bad walk-back.
    private static readonly int[] CombatDispatchers = [0x005473A0, 0x005DF0F0, 0x006B4010];

    private static IReadOnlyList<IValidationRule> CombatCallCheck { get; } =
        [new CallTargetsRule(CombatDispatchers, minMatches: 3)];

    public static IReadOnlyList<HookSymbol> Default { get; } =
    [
        new HookSymbol
        {
            Id = "MultiClientHooks.Client::IsAlreadyRunning",
            PatternId = "iar-v1",
            FallbackVa = 0x004122A0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x56, 0x68, 0x30, 0x58, 0x79, 0x00, 0x6A, 0x01,
                0x6A, 0x00, 0x6A, 0x00, 0x8B, 0xF1, 0xFF, 0x15
            ]
        },
        new HookSymbol
        {
            Id = "MultiClientHooks.CLBlockAllocator::OpenDataFile",
            PatternId = "openDataFile-v1",
            FallbackVa = 0x00675920,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x8B, 0x44, 0x24, 0x10, 0x53, 0x55, 0x8B, 0x6C,
                0x24, 0x0C, 0x56, 0x57, 0x8B, 0xF1, 0x50, 0x8B,
                0xC8, 0x8B, 0x44, 0x24, 0x20, 0x6A, 0x00, 0x80,
                0xE1, 0x02, 0x80, 0xF9, 0x02, 0x50, 0x6A, 0x00
            ]
        },
        new HookSymbol
        {
            Id = "BusyCountHooks.IncrementBusyCount",
            PatternId = "busy-inc-v1",
            FallbackVa = 0x00565610,
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern =
            [
                0x8B, 0x51, 0x14, 0x42, 0x8B, 0xC2, 0x83, 0xF8,
                0x01, 0x89, 0x51, 0x14, 0x75, 0x05, 0xE9, null,
                null, null, null, 0xC3
            ]
        },
        new HookSymbol
        {
            Id = "BusyCountHooks.DecrementBusyCount",
            PatternId = "busy-dec-v1",
            FallbackVa = 0x00565630,
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern =
            [
                0xFF, 0x49, 0x14, 0x75, 0x05, 0xE9, null, null,
                null, null, 0xC3
            ]
        },
        new HookSymbol
        {
            Id = "BusyCountHooks.UpdateCursorState",
            PatternId = "busy-cursor-v1",
            FallbackVa = 0x005653D0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x83, 0xEC, 0x08, 0x53, 0x55, 0x56, 0x57, 0x89,
                0x4C, 0x24, 0x10, 0xE8, null, null, null, null,
                0x85, 0xC0, 0x0F, 0x95, 0xC3, 0x33, 0xC0, 0x84, 0xDB
            ]
        },
        new HookSymbol
        {
            Id = "CombatModeHooks.SetCombatMode",
            PatternId = "combatmode-v1",
            FallbackVa = 0x0056CB80,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x51, 0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x1C,
                0x57, 0x8B, 0x7C, 0x24, 0x14, 0x3B, 0xF8, 0x8B,
                0xD8, 0x0F, 0x84, 0xEA, 0x01, 0x00, 0x00, 0x8A,
                0x44, 0x24, 0x18, 0x84, 0xC0
            ]
        },
        new HookSymbol
        {
            Id = "LogoutLifecycle.ExecuteLogOff",
            PatternId = "logout-exec-v1",
            FallbackVa = 0x0055E4A0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x53, 0x56, 0x8B, 0xF1, 0x33, 0xDB, 0x88, 0x9E,
                0x13, 0x02, 0x00, 0x00, 0x88, 0x9E, 0x21, 0x02,
                0x00, 0x00, 0x8B, 0x0D, null, null, null, null,
                0xA1, null, null, null, null, 0x89, 0x86, 0x00,
                0x02, 0x00, 0x00
            ]
        },
        new HookSymbol
        {
            Id = "LogoutLifecycle.RecvNotice_Logoff",
            PatternId = "logout-recv-v1",
            FallbackVa = 0x004ECBA0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x81, 0xEC, 0x90, 0x00, 0x00, 0x00, 0x56, 0x8B,
                0xF1, 0xB0, 0x01, 0x8D, 0x4C, 0x24, 0x04, 0x88,
                0x46, 0x2D, 0x88, 0x46, 0x2E, 0xE8, null, null,
                null, null, 0xA1, null, null, null, null, 0x68,
                0x01, 0x00, 0x00, 0x10
            ]
        },
        new HookSymbol
        {
            Id = "DoMotionHooks.DoMotion",
            PatternId = "domotion-v1",
            FallbackVa = 0x00510AF0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x83, 0xEC, 0x64, 0x56, 0x8B, 0xF1, 0x8B, 0x8E,
                0xC4, 0x00, 0x00, 0x00, 0x33, 0xC0, 0x3B, 0xC8,
                0xC7, 0x86, 0xCC, 0x00, 0x00, 0x00, 0x01, 0x00,
                0x00, 0x00, 0x75, 0x0C
            ]
        },
        new HookSymbol
        {
            Id = "AccountHooks.SendNotice_WorldName",
            PatternId = "account-worldname-v1",
            FallbackVa = 0x00693A60,
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern =
            [
                0xE8, null, null, null, null, 0x8B, 0x10, 0x68,
                0xA2, 0x86, 0x01, 0x00, 0x8B, 0xC8, 0xFF, 0x52,
                0x10, 0x85, 0xC0, 0x74, 0x22, 0x56, 0x8B, 0x70, 0x04
            ]
        },
        new HookSymbol
        {
            Id = "PowerbarHooks.UIElement::SetVisible",
            PatternId = "uielement-setvisible-v1",
            FallbackVa = 0x00462390,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x51, 0x53, 0x56, 0x57, 0x8B, 0x3D, null, null,
                null, null, 0x8B, 0xF1, 0xE8, null, null, null,
                null, 0x8B, 0x9E, 0xA4, 0x00, 0x00, 0x00, 0x88,
                0x44, 0x24, 0x0F
            ]
        },
        new HookSymbol
        {
            Id = "PowerbarHooks.Begin",
            PatternId = "powerbar-begin-v1",
            FallbackVa = 0x004DB390,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x83, 0xEC, 0x10, 0x55, 0x8B, 0x6C, 0x24, 0x18,
                0x56, 0x8D, 0x45, 0xFF, 0x83, 0xF8, 0x03, 0x57,
                0x8B, 0xF1, 0x0F, 0x87, 0xBC, 0x00, 0x00, 0x00,
                0xFF, 0x24, 0x85, null, null, null, null
            ]
        },
        new HookSymbol
        {
            Id = "PowerbarHooks.Level",
            PatternId = "powerbar-level-v1",
            FallbackVa = 0x004DB1B0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x8B, 0x44, 0x24, 0x04, 0x3B, 0x41, 0x04, 0x75,
                0x23, 0x68, 0x34, 0x00, 0x00, 0x10, 0x81, 0xC1,
                0x08, 0xFA, 0xFF, 0xFF, 0xE8, null, null, null,
                null, 0x85, 0xC0, 0x74, 0x0F
            ]
        },
        new HookSymbol
        {
            Id = "PowerbarHooks.Finish",
            PatternId = "powerbar-finish-v1",
            FallbackVa = 0x004DB1E0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x8B, 0x44, 0x24, 0x04, 0x3B, 0x41, 0x04, 0x75,
                0x33, 0x56, 0x8D, 0xB1, 0x08, 0xFA, 0xFF, 0xFF,
                0xC7, 0x41, 0x04, 0x00, 0x00, 0x00, 0x00, 0x68,
                0x34, 0x00, 0x00, 0x10, 0x8B, 0xCE, 0xE8, null,
                null, null, null
            ]
        },
        new HookSymbol
        {
            Id = "ChatHooks.UIElement::SetVisible",
            PatternId = "uielement-setvisible-v1",
            FallbackVa = 0x00462390,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x51, 0x53, 0x56, 0x57, 0x8B, 0x3D, null, null,
                null, null, 0x8B, 0xF1, 0xE8, null, null, null,
                null, 0x8B, 0x9E, 0xA4, 0x00, 0x00, 0x00, 0x88,
                0x44, 0x24, 0x0F
            ]
        },
        new HookSymbol
        {
            Id = "ChatHooks.ListenToElementMessage",
            PatternId = "chat-listen-v1",
            FallbackVa = 0x004CE6F0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x56, 0x8B, 0x74, 0x24, 0x08, 0x8B, 0x46, 0x08,
                0x48, 0x57, 0x8B, 0xF9, 0x74, 0x72, 0x83, 0xE8,
                0x06, 0x75, 0x7D, 0x8B, 0x4E, 0x04, 0x8B, 0x01,
                0x6A, 0x06, 0xFF, 0x90, 0x94, 0x00, 0x00, 0x00
            ]
        },
        new HookSymbol
        {
            Id = "RadarHooks.UIElement::SetVisible",
            PatternId = "uielement-setvisible-v1",
            FallbackVa = 0x00462390,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x51, 0x53, 0x56, 0x57, 0x8B, 0x3D, null, null,
                null, null, 0x8B, 0xF1, 0xE8, null, null, null,
                null, 0x8B, 0x9E, 0xA4, 0x00, 0x00, 0x00, 0x88,
                0x44, 0x24, 0x0F
            ]
        },
        new HookSymbol
        {
            Id = "RadarHooks.UIElement::GetSurfaceBox",
            PatternId = "uielement-surfacebox-v1",
            FallbackVa = 0x00462220,
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern =
            [
                0xF6, 0x81, 0x56, 0x05, 0x00, 0x00, 0x01, 0x74,
                0x11, 0x56, 0x8B, 0x74, 0x24, 0x08, 0x56, 0xE8,
                null, null, null, null, 0x8B, 0xC6, 0x5E, 0xC2,
                0x04, 0x00
            ]
        },
        new HookSymbol
        {
            Id = "RadarHooks.DrawObjects",
            PatternId = "radar-drawobjects-v1",
            FallbackVa = 0x004D9FE0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x81, 0xEC, 0xDC, 0x00, 0x00, 0x00, 0x53, 0x55,
                0x56, 0x8B, 0xF1, 0xD9, 0x86, 0x00, 0x06, 0x00,
                0x00, 0x8B, 0x86, 0x0C, 0x06, 0x00, 0x00, 0xD8,
                0x25, null, null, null, null, 0x33, 0xDB, 0x3B, 0xC3
            ]
        },
        new HookSymbol
        {
            Id = "RadarHooks.DrawChildren",
            PatternId = "radar-drawchildren-v1",
            FallbackVa = 0x004DA380,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x8B, 0x44, 0x24, 0x0C, 0x8B, 0x54, 0x24, 0x04,
                0x53, 0x55, 0x56, 0x8B, 0x74, 0x24, 0x1C, 0x57,
                0x56, 0x8B, 0xF9, 0x8B, 0x4C, 0x24, 0x1C, 0x50,
                0x51, 0x52, 0x8B, 0xCF, 0xE8, null, null, null,
                null, 0x56
            ]
        },
        // ── Hooks NOT routed through HookResolver.Resolve. The engine still finds
        //    these via PatternScanner.FindPattern + FindPrologueBefore (or
        //    pattern-or-fixed-VA verify). Engine VA column will show "—" for all
        //    of these because they don't emit HookResolver[...] log lines.

        new HookSymbol
        {
            Id = "LoginLifecycle.SendLoginCompleteNotification",
            PatternId = "loginComplete-v1",
            Anchor = SymbolAnchor.AnchorOnCallSite,
            Pattern =
            [
                0x6A, 0x01, 0xC6, 0x86, null, null, null, null,
                0x01, 0xE8, null, null, null, null, 0x8B, 0x0D,
                null, null, null, null, 0x83, 0xC4, 0x04, 0x6A,
                0x00, 0x6A, 0x0B, 0xE8
            ],
            WalkBack = new PrologueWalkBack([0x56, 0x8B, 0xF1], MaxDistance: 0x100)
        },

        new HookSymbol
        {
            Id = "SmartBoxHooks.DispatchSmartBoxEvent",
            PatternId = "smartbox-dispatch-v1",
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x10,
                0x8B, 0x53, 0x30, 0x83, 0xFA, 0x04, 0x8B, 0x43,
                0x2C, 0x56, 0x8B, 0xF1, 0x89, 0x44, 0x24, 0x14,
                0x89, 0x54, 0x24, 0x08, 0x72, null, 0x8B, 0x08
            ]
        },

        new HookSymbol
        {
            Id = "SmartBoxHooks.DispatchGameEvent",
            PatternId = "smartbox-dispatch-v1",
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x10,
                0x8B, 0x53, 0x30, 0x83, 0xFA, 0x04, 0x8B, 0x43,
                0x2C, 0x56, 0x8B, 0xF1, 0x89, 0x44, 0x24, 0x14,
                0x89, 0x54, 0x24, 0x08, 0x72, null, 0x8B, 0x08
            ]
        },

        new HookSymbol
        {
            Id = "UiLifecycleHooks.SetUIReady",
            PatternId = "setUIReady-v1",
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x56, 0x8B, 0x74, 0x24, 0x08, 0x85, 0xF6, 0x75,
                0x16, 0x8B, 0x0D, null, null, null, null, 0x85,
                0xC9, 0x74, 0x0C, 0x8B, 0x41, 0x04, 0x85, 0xC0,
                0x74, 0x05, 0xE8, null, null, null, null, 0x89,
                0x35, null, null, null, null, 0x5E, 0xC3
            ]
        },

        // ── MovementActionHooks: tiny `mov [edx], ACTION_CODE` wrappers. Each
        //    pattern is the unique action-code write; walk back to the shared
        //    7-byte combat-family prologue (sub esp,0xC; push regs; call).

        new HookSymbol
        {
            Id = "MovementActionHooks.StopMovement",
            PatternId = "movement-stop-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0x61, 0xF6, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300)
        },
        new HookSymbol
        {
            Id = "MovementActionHooks.DoMovement",
            PatternId = "movement-do-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0x1E, 0xF6, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300)
        },
        new HookSymbol
        {
            Id = "MovementActionHooks.JumpNonAutonomous",
            PatternId = "movement-jump-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0xC9, 0xF7, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300)
        },
        new HookSymbol
        {
            Id = "MovementActionHooks.SetAutonomyLevel",
            PatternId = "movement-autonomy-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0x52, 0xF7, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300)
        },

        // ── CombatActionHooks: same wrapper shape as Movement. CastSpell is
        //    known-fragile (engine has it disabled); the snapshot will tell us
        //    if the walk-back lands on a sane function.

        new HookSymbol
        {
            Id = "CombatActionHooks.CancelAttack",
            PatternId = "combat-cancel-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0xB7, 0x01, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300),
            AdvisoryRules = CombatCallCheck
        },
        new HookSymbol
        {
            Id = "CombatActionHooks.ChangeCombatMode",
            PatternId = "combat-change-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0x53, 0x00, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300),
            AdvisoryRules = CombatCallCheck
        },
        new HookSymbol
        {
            Id = "CombatActionHooks.QueryHealth",
            PatternId = "combat-queryhealth-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0xBF, 0x01, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300),
            AdvisoryRules = CombatCallCheck
        },
        new HookSymbol
        {
            Id = "CombatActionHooks.MeleeAttack",
            PatternId = "combat-melee-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0x08, 0x00, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300),
            AdvisoryRules = CombatCallCheck
        },
        new HookSymbol
        {
            Id = "CombatActionHooks.MissileAttack",
            PatternId = "combat-missile-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0x0A, 0x00, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300),
            AdvisoryRules = CombatCallCheck
        },
        new HookSymbol
        {
            Id = "CombatActionHooks.RequestId",
            PatternId = "combat-requestid-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0xC8, 0x00, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300),
            AdvisoryRules = CombatCallCheck
        },
        new HookSymbol
        {
            Id = "CombatActionHooks.CastSpell",
            PatternId = "combat-castspell-v1",
            Anchor = SymbolAnchor.AnchorOnBody,
            Pattern = [0xC7, 0x02, 0x4A, 0x00, 0x00, 0x00],
            WalkBack = new PrologueWalkBack(CombatActionPrologue, MaxDistance: 300),
            // Required (not advisory): if the walk-back ever lands on a
            // non-combat function, this flips CastSpell to Unresolved instead
            // of silently emitting a wrong VA — the whole point of the tool.
            RequiredRules = CombatCallCheck
        },

        // ── ChatCallbackHooks: hardcoded VA + signature verify in the engine.
        //    Tool just pattern-scans; if it resolves to the same VA the engine
        //    uses, the pattern is sound.

        new HookSymbol
        {
            Id = "ChatCallbackHooks.IncomingChatAddTextToScroll",
            PatternId = "chat-incoming-scroll-v1",
            FallbackVa = 0x005649F0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x81, 0xEC, 0x48, 0x09, 0x00, 0x00, 0x8A, 0x84,
                0x24, 0x54, 0x09, 0x00, 0x00, 0x53, 0x55, 0x56,
                0x33, 0xF6, 0x84, 0xC0, 0x57, 0x8B, 0xBC, 0x24,
                0x5C, 0x09, 0x00, 0x00, 0x89, 0x4C, 0x24, 0x24
            ]
        },
        new HookSymbol
        {
            Id = "ChatCallbackHooks.IncomingChatWrapper",
            PatternId = "chat-incoming-wrapper-v1",
            FallbackVa = 0x0058A000,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0xA1, 0xE4, 0x0B, 0x87, 0x00, 0x85, 0xC0, 0x75,
                0x06, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3, 0x56,
                0x8B, 0x74, 0x24, 0x10, 0x83, 0xFE, 0x01, 0x74, 0x0F
            ]
        },
        new HookSymbol
        {
            Id = "ChatCallbackHooks.OutgoingChat",
            PatternId = "chat-outgoing-v1",
            FallbackVa = 0x005821A0,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x83, 0xEC, 0x0C, 0x53, 0x55, 0x56, 0x57, 0x8B,
                0xF9, 0xE8, null, null, null, null, 0x85, 0xC0,
                0x8B, 0x74, 0x24, 0x20, 0x74, 0x30, 0x8B, 0x06,
                0x50, 0xFF, 0x15, null, null, null, null, 0x8B,
                0xD8, 0xC7, 0x44, 0x24, 0x20, 0x00, 0x00, 0x00,
                0x00, 0xE8, null, null, null, null, 0x8B, 0x08,
                0x8D, 0x54, 0x24, 0x20, 0x52, 0x53, 0x50, 0xFF,
                0x51, 0x20, 0x8B, 0x44, 0x24, 0x20, 0x85, 0xC0,
                0x0F, 0x85, null, null, null, null, 0x8B, 0x44,
                0x24, 0x24, 0x6A, 0x00
            ]
        },

        new HookSymbol
        {
            Id = "UpdateObjectInventoryHooks.UpdateObjectInventory",
            PatternId = "updateInv-v1",
            FallbackVa = 0x0055A190,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x8B, 0x44, 0x24, 0x04, 0x50, 0xE8, 0x96, 0xE7,
                0xFA, 0xFF, 0x8B, 0x4C, 0x24, 0x08, 0x51, 0x8D,
                0x48, 0x3C, 0xE8, 0x69, 0xFF, 0xFF, 0xFF, 0xC2,
                0x08, 0x00
            ]
        },
        new HookSymbol
        {
            Id = "SalvageHooks.OpenSalvagePanel",
            PatternId = "salvage-open-v1",
            FallbackVa = 0x004CBF70,
            Anchor = SymbolAnchor.AnchorOnPrologue,
            Pattern =
            [
                0x8B, 0x44, 0x24, 0x04, 0x56, 0x8B, 0xF1, 0x8B,
                0x8E, 0x00, 0x06, 0x00, 0x00, 0x51, 0x8B, 0xCE,
                0x89, 0x86, 0x08, 0x06, 0x00, 0x00, 0xE8, null,
                null, null, null, 0x8B, 0x8E, 0x00, 0x06, 0x00,
                0x00, 0xE8, null, null, null, null
            ]
        }
    ];
}
