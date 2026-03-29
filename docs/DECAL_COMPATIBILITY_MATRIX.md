# Decal Compatibility Matrix

This matrix tracks clean-room compatibility targets that can be implemented from the AC client decompile and public Decal-facing materials without inspecting Decal or VTank binaries.

Use [ACCLIENT_HOOK_INVENTORY.md](./ACCLIENT_HOOK_INVENTORY.md) as the source of truth for hook planning against the user's real `acclient.exe`. The decompile is for semantics; the live binary is for final hook placement.

## Lifecycle And Callback Surface

| Surface | Source seam | Status | Notes |
| --- | --- | --- | --- |
| `OnLoginComplete` | `CPlayerSystem::SendLoginCompleteNotification` and `CM_Character::Event_LoginCompleteNotification` | In progress | Best first lifecycle event for plugin startup. |
| `OnChatWindowText` | `ClientSystem.cpp` | Confirmed | Incoming chat interception with eat/suppress behavior. |
| `OnChatBarEnter` | `ClientCommunicationSystem.cpp` | Confirmed | Outgoing chat interception before normal handling. |
| `OnDeleteObject` | `ACCObjectMaint.cpp` | Confirmed | Clean object lifecycle seam. |
| `OnSelectedTargetChange` | `ACCWeenieObject.cpp` | Confirmed | Target-change callback path exists. |
| `OnCombatModeChange` | `ClientCombatSystem.cpp` | Confirmed | Decal-shaped combat mode callback exists. |
| Trade window callbacks | `ClientTradeSystem.cpp` and `ClientCommunicationSystem.cpp` | Confirmed | Open/add/accept/decline/close/clear/completed all have clear fire sites. |
| `OnUIInitialized` | `ClientUISystem::OnStartup` and `APIManager::SetUIReady(1)` | Investigating | Interface exists; direct Decal fire site not yet confirmed. |
| Busy count callbacks | `ClientUISystem.cpp` | Investigating | Interface exists; direct plugin callback bridge still needs a clean fire site. |

## Public Host API Surface

| API | Source seam | Status | Notes |
| --- | --- | --- | --- |
| `WriteToChat` | `APIManager__IAsheronsCallImpl.cpp` | Confirmed | Good candidate for early compatibility exposure. |
| Selection get/set | `APIManager__IAsheronsCallImpl.cpp` | Confirmed | Useful for Decal-style wrappers. |
| `UseObject` / `UseObjectOn` / `UseEquippedItem` | `APIManager__IAsheronsCallImpl.cpp` | Confirmed | Interaction helpers are implemented. |
| `MoveItemExternal` / `MoveItemInternal` | `APIManager__IAsheronsCallImpl.cpp` | Confirmed | Inventory movement path is present. |
| `IsStandingStill` / `StopCompletely` / `TurnToHeading` / `SetAutoRun` | `APIManager__IAsheronsCallImpl.cpp` | Confirmed | Useful movement helpers. |
| `GetCurCoords` / `GetPlayerID` / `GetGroundContainerID` | `APIManager__IAsheronsCallImpl.cpp` | Confirmed | Straightforward compatibility candidates. |
| `GetScreenDimensions` / `ItemIsKnown` / `GetItemName` | `APIManager__IAsheronsCallImpl.cpp` | Confirmed | Low-risk helpers. |
| `IssueChatBarCommand` | `APIManager__IAsheronsCallImpl.cpp` | Stub-like | Do not rely on this yet. |
| `GetCombatMode` / `GetBusyCount` / salvage panel APIs | `APIManager__IAsheronsCallImpl_vtbl.cpp` | Unconfirmed | Declared in vtable, implementation not yet verified in the split. |

## Lower-Level Hook Seams

| Seam | Source file | Status | Notes |
| --- | --- | --- | --- |
| `ACSmartBox::DispatchSmartBoxEvent` | `ACSmartBox.cpp` | Confirmed | Strong world/object/network event seam. |
| `ACCObjectMaint` object lifecycle | `ACCObjectMaint.cpp` | Confirmed | Create/delete/inventory/content updates. |
| `ClientAdminSystem` plugin queries | `ClientAdminSystem.cpp` | Confirmed | Useful for future clean-room admin/plugin info compatibility. |
| `ClientUISystem::OnStartup` | `ClientUISystem.cpp` | Confirmed | Good host readiness seam. |
| `CM_*` and `ECM_*` notice buses | multiple client systems | Confirmed | Good source for higher-level compatibility events. |

## Implementation Order

1. Add clean-room lifecycle callbacks beginning with `OnLoginComplete`.
2. Expose the confirmed `IAsheronsCall` subset through NexCore-native APIs.
3. Bridge world/object events from `ACSmartBox` and `ACCObjectMaint`.
4. Layer in chat, trade, target, and combat callbacks.
5. Revisit optional binary compatibility only if it stays within the guardrails in `docs/LEGAL_COMPATIBILITY.md`.
