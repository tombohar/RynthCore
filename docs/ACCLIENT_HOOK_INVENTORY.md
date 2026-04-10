# ACClient Hook Inventory

This file tracks RynthCore hook targets against the user's real AC client binary, with the AC client decompile used for semantics and the live binary used as the source of truth for hook placement.

## Client Fingerprint

- Path: `C:\Turbine\Asheron's Call\acclient.exe`
- File size: `4,841,472` bytes
- SHA256: `BCA95BBEBED4B9ED1FF09D0DA83144E2FC4208F63AD7ADA5CB47C3CA207CCBA9`
- PE machine: `0x014C` (`x86`)
- PE image size: `0x0056D000`

## Rules For This Inventory

- Use the decompile to understand semantics, call order, and callback behavior.
- Use the live `acclient.exe` to derive patterns, validate uniqueness, and choose the real hook address.
- Do not ship fixed RVAs from the decompile unless they have been validated against the live binary.
- Prefer hooking the AC client function that already implements the desired behavior over lower-level memory access.
- Every production hook should eventually have:
  - a primary binary pattern
  - at least one fallback pattern or alternate seam when practical
  - runtime validation
  - an explicit unsupported-state log/UI message

## Priority Inventory

### Lifecycle And UI

| Surface | Decompiled seam | Actual-binary plan for this client | Status | Priority |
| --- | --- | --- | --- | --- |
| `OnLoginComplete` | `CPlayerSystem::SendLoginCompleteNotification` in `CPlayerSystem.cpp:4017-4031` | Pattern-scan the containing function using the unique `SetDisplayInventory(1)` plus `BroadcastGlobalMessage(..., 0xB, 0)` sequence, then hook the containing `thiscall` function. | Live in RynthCore | P0 |
| `OnUIInitialized` | `ClientUISystem::OnStartup` in `ClientUISystem.cpp:48` and `APIManager::SetUIReady(1)` in `APIManager.cpp:134` | Hook the tiny `OnStartup -> SetUIReady(1)` wrapper or the `SetUIReady(1)` transition path. Prefer the wrapper if the pattern is unique in the live binary. | Not implemented | P0 |
| `OnBusyCountIncremented` | `ClientUISystem::IncrementBusyCount` in `ClientUISystem.cpp:630` | Hook the wrapper that increments `m_cBusy` and updates cursor state when crossing `0 -> 1`. | Not implemented | P1 |
| `OnBusyCountDecremented` | `ClientUISystem::DecrementBusyCount` in `ClientUISystem.cpp:641` | Hook the wrapper that decrements `m_cBusy` and updates cursor state when crossing `1 -> 0`. | Not implemented | P1 |

### Chat And Command

| Surface | Decompiled seam | Actual-binary plan for this client | Status | Priority |
| --- | --- | --- | --- | --- |
| `OnChatWindowText` | `ClientSystem.cpp:108` | Hook the function that allocates a BSTR from `i_text`, calls `GetACPlugin()->OnChatWindowText`, then checks `bEat` before continuing UI output. | Ready for binary patterning | P0 |
| `OnChatBarEnter` | `ClientCommunicationSystem.cpp:14407` | Hook the function that allocates a BSTR from the entered line, calls `GetACPlugin()->OnChatBarEnter`, and returns early when the plugin consumes the line. | Ready for binary patterning | P0 |

### World, Target, And Object Flow

| Surface | Decompiled seam | Actual-binary plan for this client | Status | Priority |
| --- | --- | --- | --- | --- |
| `OnDeleteObject` | `ACCObjectMaint::DeleteObject` in `ACCObjectMaint.cpp:73-85` | Hook the clean wrapper that checks `APIIsReady`, calls `OnDeleteObject`, then calls `ACCWeenieObject::SetCorpseDeleted` and `CObjectMaint::DeleteObject`. | Ready for binary patterning | P0 |
| `OnSelectedTargetChange` | `ACCWeenieObject.cpp:710` and `ACCWeenieObject.cpp:1997` | Hook the selected-target setter path after `CM_UI::SendNotice_SelectionChanged` and before the direct plugin callback. | Ready for binary patterning | P0 |
| World/object dispatch | `ACSmartBox::DispatchSmartBoxEvent` in `ACSmartBox.cpp:37-171` | Hook the SmartBox dispatch seam as the high-value world/object/network filter layer. This is the likely replacement for many WorldFilter-style surfaces. | Ready for binary patterning | P0 |
| Object creation | `ACCObjectMaint::CreateObject` in `ACCObjectMaint.cpp:196` | Hook the object creation wrapper for higher-level object lifecycle callbacks when we need richer data than a raw SmartBox event. | Ready for binary patterning | P1 |
| Object inventory updates | `ACCObjectMaint::UpdateObjectInventory` in `ACCObjectMaint.cpp:377` | Hook the inventory update path for container/item-change compatibility events. | Ready for binary patterning | P1 |
| Update/create/delete server dispatch | `CM_Physics::DispatchSB_CreateObject`, `DispatchSB_UpdateObject`, `DispatchSB_DeleteObject` in `CM_Physics.cpp` | Use as fallback seams if the outer SmartBox dispatcher proves too broad or unstable. | Decompile-confirmed | P1 |

### Combat And Trade

| Surface | Decompiled seam | Actual-binary plan for this client | Status | Priority |
| --- | --- | --- | --- | --- |
| `OnCombatModeChange` | `ClientCombatSystem.cpp:1564` | Hook the branch that invokes the plugin callback after the combat mode change resolves and auto-targeting logic runs. | Ready for binary patterning | P1 |
| Trade window opened | `ClientTradeSystem.cpp:525` | Hook `Handle_Trade__Recv_RegisterTrade` after `CM_Trade::SendNotice_RegisterTrade` and before the internal attempt-to-trade follow-up. | Ready for binary patterning | P1 |
| Trade item added | `ClientTradeSystem::Handle_Trade__Recv_AddToTrade` in `ClientTradeSystem.cpp:560+` | Hook the direct wrapper that calls `CM_Trade::SendNotice_AddItemToTrade` and then `OnTradeWindowItemAdded`. | Ready for binary patterning | P1 |
| Trade completed | `ClientCommunicationSystem.cpp:3106` | Hook the message-switch case for `0x529` (`Trade Complete!`) that directly invokes `OnTradeCompleted`. | Ready for binary patterning | P1 |
| Trade accept/decline/close/clear | `ClientTradeSystem.cpp` | Hook the direct trade-system wrappers that invoke the corresponding plugin callback. | Decompiled seam confirmed, binary anchor still needed | P1 |

## Host Call Surface To Expose Through RynthCore

These are not plugin callback hooks, but they are part of the host API surface we should expose once the callback layer is in place.

| API surface | Decompiled seam | Notes | Priority |
| --- | --- | --- | --- |
| `WriteToChat` | `APIManager__IAsheronsCallImpl.cpp` | Safe early host helper. | P0 |
| Selection get/set | `APIManager__IAsheronsCallImpl.cpp` | Pairs naturally with target-change callbacks. | P0 |
| `UseObject`, `UseObjectOn`, `UseEquippedItem` | `APIManager__IAsheronsCallImpl.cpp` | Good interaction helpers once lifecycle is stable. | P1 |
| `MoveItemExternal`, `MoveItemInternal` | `APIManager__IAsheronsCallImpl.cpp` | Useful once inventory callbacks exist. | P1 |
| `IsStandingStill`, `StopCompletely`, `TurnToHeading`, `SetAutoRun` | `APIManager__IAsheronsCallImpl.cpp` | Movement helper layer. Some of this already overlaps with existing RynthCore action hooks. | P0 |
| `GetCurCoords`, `GetPlayerID`, `GetGroundContainerID` | `APIManager__IAsheronsCallImpl.cpp` | Good basic state helpers. | P0 |
| `GetScreenDimensions`, `ItemIsKnown`, `GetItemName` | `APIManager__IAsheronsCallImpl.cpp` | Low-risk host helpers. | P1 |
| `IssueChatBarCommand` | `APIManager__IAsheronsCallImpl.cpp` | Treat as stub-like until runtime behavior is validated against the live client. | Hold |
| `GetCombatMode`, `GetBusyCount`, salvage panel APIs | `APIManager__IAsheronsCallImpl_vtbl.cpp` | Declared but not yet fully validated in the split. | Hold |

## Implementation Waves

1. Finish lifecycle/UI: `OnLoginComplete`, `OnUIInitialized`, busy count.
2. Add chat interception: `OnChatWindowText`, `OnChatBarEnter`.
3. Add world/object seams: SmartBox dispatch, delete/create/inventory, selected target.
4. Add combat/trade callbacks.
5. Expose the confirmed `IAsheronsCall` subset through RynthCore-native host APIs.

## Notes

- The split decompile is still very useful, but the login-complete work already showed that its function RVAs do not always line up with the user's live client binary.
- For this reason, future hook work should be tracked here first against the fingerprint above, then implemented in RynthCore with pattern scanning rather than fixed addresses.
