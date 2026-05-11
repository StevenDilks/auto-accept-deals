# IL2CPP Surface Map — AutoAcceptDeals

Reference document for the game-side symbols later phases of `AutoAcceptDeals` will hook. Built once, by hand, against a concrete game version. If the game updates and a phase-2+ build breaks, the first thing to check is whether the symbols below have moved or changed signatures.

## Dump metadata

| Item | Value |
| --- | --- |
| Game | Schedule I (`TVGS`) |
| Game version | `0.4.5f2` (per `MelonLoader/Latest.log`) |
| Unity | `2022.3.62f2` |
| MelonLoader | `0.7.1 Open-Beta` (master plan said 0.6.x — actual install is 0.7.1; csproj refs in `MelonLoader/net6/` and `MelonLoader/Il2CppAssemblies/` still resolve fine) |
| Cpp2IL | bundled with MelonLoader at `MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/Cpp2IL.exe`; output already on disk at `cpp2il_out/` |
| Decompiler | `ilspycmd` 8.2.0.7535 (`dotnet tool install -g ilspycmd --version 8.2.0.7535`) |
| Dump scratch dir | `C:\Users\Steve\aad-decomp\` (gitignored, outside repo) |
| Dump date | 2026-04-25 |

**Reproduction:**

```bash
ilspycmd -p --nested-directories \
  -o /c/Users/Steve/aad-decomp/Assembly-CSharp \
  "C:/Program Files (x86)/Steam/steamapps/common/Schedule I/MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/Assembly-CSharp.dll"

ilspycmd -p --nested-directories \
  -o /c/Users/Steve/aad-decomp/Interop \
  "C:/Program Files (x86)/Steam/steamapps/common/Schedule I/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll" \
  -r "C:/Program Files (x86)/Steam/steamapps/common/Schedule I/MelonLoader/Il2CppAssemblies"
```

## How to read this doc

Two namespaces appear throughout:

- **Cpp2IL form** — what you see when you decompile `cpp2il_out/Assembly-CSharp.dll`. Mirrors the original game source: `ScheduleOne.Economy.Customer`. Use this when reasoning about behavior.
- **Il2CppInterop form** — the type the mod actually compiles against from `MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll`. The game-script types are prefixed with `Il2Cpp` (e.g. `Il2CppScheduleOne.Economy.Customer`). Il2CppInterop also rewrites `protected` methods to `public` and adds `unsafe` — so things you see as `protected virtual` below are callable directly.

Method signatures are written in original-source form (Cpp2IL). The Il2CppInterop wrapper has the same parameter list and return type but lives under the `Il2CppScheduleOne.*` namespace — when both forms differ in any non-trivial way, both are written out.

Two collection-type substitutions Il2CppInterop applies that the signatures below silently assume:

- `T[]` → `Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T>` (e.g. `Map.Regions` is `Il2CppReferenceArray<MapRegionData>`, not a managed `MapRegionData[]`)
- `System.Collections.Generic.List<T>` → `Il2CppSystem.Collections.Generic.List<T>` (e.g. the first arg of `GetOfferSuccessChance`)

Both are iterable like ordinary collections; the type names just differ at compile time.

## Phase 2 prerequisite: csproj reference fix

`AutoAcceptDeals/AutoAcceptDeals.csproj` currently has:

```xml
<HintPath>$(Schedule1Path)\MelonLoader\Il2CppAssemblies\Il2CppAssembly-CSharp.dll</HintPath>
```

That file does not exist. The actual filename in `Il2CppAssemblies/` is `Assembly-CSharp.dll` (no `Il2Cpp` prefix — the prefix is only applied to non-game Unity/third-party assemblies). Phase 2's first task is to change the HintPath to:

```xml
<HintPath>$(Schedule1Path)\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll</HintPath>
```

Same applies if any later phase needs `Assembly-CSharp-firstpass.dll`.

---

## 1. Deal-acceptance oracle — *Phase 6*

**Use `Customer.EvaluateCounteroffer(ProductDefinition product, int quantity, float totalPrice) -> bool`** (protected virtual; exposed `public` by Il2CppInterop). This is what `ProcessCounterOfferServerSide` calls server-side to decide accept vs. reject for a real counter-offer, so its yes/no is the signal that matches what the live send path will obey. Phase 6 binary-searches integer **total** dollars on this; ~17 iterations to converge over a typical $1k → $20k range.

| Field | Value |
| --- | --- |
| Cpp2IL form | `ScheduleOne.Economy.Customer.EvaluateCounteroffer(ProductDefinition, int, float) -> bool` |
| Il2CppInterop form | same args, `public unsafe virtual` |
| Source (Cpp2IL) | `aad-decomp/Assembly-CSharp/ScheduleOne/Economy/Customer.cs:917` |

**Empirically confirmed Phase 6 (units probe, 2026-05-04):**

- `price` is **total dollars, not per-unit.** Confirmed via the engine's units-disambiguation probe: for greencrack qty=25, customer-offered $1290 total, `EvaluateCounteroffer(greencrack, 25, 1)` returns `true` (customer happily accepts a near-free deal), `EvaluateCounteroffer(greencrack, 25, 1290)` returns `true`, `EvaluateCounteroffer(greencrack, 25, 100000)` returns `false`. The True/True/False pattern uniquely identifies total-dollar semantics. Earlier per-unit reading was wrong — a single-point test ($52 accepts) was consistent with both interpretations and we picked the wrong one.
- Phase 7's `SendCounteroffer.price` parameter is **strongly indicated to be total** as well, since `EvaluateCounteroffer` is what its server-side handler invokes — but verify before shipping.
- **Non-deterministic.** Three identical calls `EvaluateCounteroffer(greencrack, 25, X)` for converging X returned different best-accept thresholds: 1606, 1643, 1736. ~8% spread. The function rolls internally — likely consults `Customer.OfferSuccessChance` or equivalent. Binary search assumes monotonicity which breaks under randomness; convergence drifts run-to-run. Phase 6 ships best-effort; Phase 7 will need sample-N-take-min, a safety margin, or a deterministic alternative.
- Each call is one server-side roll; the symbols-map note about "per-roll" RNG advance still applies. Bisecting at most 32 integer prices per deal hasn't shown adverse effects in play.

**Phase 7 note:** `EvaluateCounteroffer` is no longer called in the mod's hot path. Phase 7 replaced the binary search with a deterministic probability reimplementation ported from BetterCounterOfferUI 3.3.0 (OverweightUnicorn) — see `CounterOfferEngine.ProbabilityContext`. The formula is pure math over public game APIs and gives stable convergence across identical inputs.

### `Customer.GetValueProposition(ProductDefinition, float unitPrice) -> float` (static)

**Not dead — actively used.** Earlier drafts of this doc marked it as suspect because its body appears empty in Cpp2IL output (a Cpp2IL decompilation artifact, not an actual empty function). BetterCounterOfferUI 3.3.0 calls it in `CalculateSuccessProbability` and gets useful values. Phase 7 adopts this: `ProbabilityContext.Capture` calls it to compute the baseline value proposition at the customer's original offer price, and `SearchByProbability` calls it once per binary-search iteration to compute `vp2` for the current candidate price.

| Field | Value |
| --- | --- |
| Cpp2IL form | `ScheduleOne.Economy.Customer.GetValueProposition(ProductDefinition product, float unitPrice) -> float` |
| Il2CppInterop form | `static float` — callable as `Customer.GetValueProposition(product, unitPrice)` |
| Source (Cpp2IL) | `Customer.cs:924` |

### Rejected alternative: `Customer.GetOfferSuccessChance(List<ItemInstance>, float) -> float`

Listed in earlier drafts of this doc as the primary oracle. **Empirically broken or stale** — investigated and rejected during Phase 6:

- Returns `0.0` deterministically regardless of basket construction. Tested with both `new ProductItemInstance(product, qty, quality, null)` and the proper factory `product.GetDefaultInstance(qty)` + `SetQuality(quality)` (which yields a `WeedInstance` / `MethInstance` / etc. with default packaging from `ProductDefinition.ValidPackaging[0]`). Same zero result. `EvaluateCounteroffer` returned `true` on the same `(product, qty, perUnitPrice)` inputs, so the basket isn't the problem — the function is.
- **Zero callers in the entire decompiled `Assembly-CSharp`.** No game code invokes it. Strongly suggests it's stale/dead developer API and not the engine's true acceptance oracle.

If a future phase needs a **probability** rather than a boolean (e.g., to surface "85% likely to accept" in the UI), it would have to either implement chance estimation by repeated `EvaluateCounteroffer` calls under varied RNG state, or reverse the function from the disassembled native binary. Don't trust `GetOfferSuccessChance` without re-verifying it against `EvaluateCounteroffer` first.

| Field | Value |
| --- | --- |
| Cpp2IL form | `ScheduleOne.Economy.Customer.GetOfferSuccessChance(List<ItemInstance>, float) -> float` |
| Source (Cpp2IL) | `aad-decomp/Assembly-CSharp/ScheduleOne/Economy/Customer.cs:1105` |

---

## 2. Incoming customer-text event / message-handler — *Phase 5*

Recommended hook: **`ScheduleOne.Economy.Customer.OfferContract(ContractInfo info)`** (instance, public virtual). This is the per-customer entry point that gets called when a customer puts a deal offer to the player. It internally creates the `MessageChain` and calls `NotifyPlayerOfContract` to show the message UI. Patching `OfferContract` lets us react *before* the UI is rendered.

There is no public C# event for "customer offered a contract" — `Customer` exposes `onUnlocked`, `onDealCompleted`, and `onContractAssigned` (UnityEvents), but not an `onContractOffered`. So Phase 5 must Harmony-patch `OfferContract`.

Alternative hook (lower-level, finer-grained but noisier): `ScheduleOne.Messaging.MessagingManager.ReceiveMessageChain(MessageChain m, string npcID, float initialDelay, bool notify)` — the ObserversRpc that fires on each client when any NPC sends a chain. Filtering NPC→Customer is feasible (`MessagingManager.GetConversation(NPC)` + a Customer lookup) but adds complexity. Stick with `OfferContract` unless we need the raw text.

| Field | Value |
| --- | --- |
| Cpp2IL form | `ScheduleOne.Economy.Customer.OfferContract(ContractInfo info) -> void` (public virtual) |
| Il2CppInterop form | `Il2CppScheduleOne.Economy.Customer.OfferContract(ContractInfo info)` (public unsafe virtual) |
| Source (Cpp2IL) | `aad-decomp/Assembly-CSharp/ScheduleOne/Economy/Customer.cs:813` |
| Source (Interop) | `aad-decomp/Interop/Il2CppScheduleOne/Economy/Customer.cs:4744` |

Closely-related symbol the patch may also need to inspect:

| Field | Value |
| --- | --- |
| Cpp2IL form | `ScheduleOne.Economy.Customer.NotifyPlayerOfContract(ContractInfo contract, MessageChain offerMessage, bool canAccept, bool canReject, bool canCounterOffer = true) -> void` (protected virtual) |
| Il2CppInterop form | same args, `public unsafe virtual` |
| Source (Cpp2IL) | `Customer.cs:839` |
| Source (Interop) | `Interop/Il2CppScheduleOne/Economy/Customer.cs:4792` |

**Caveats — flag for Phase 5:**

- Schedule I uses FishNet networking. `OfferContract` runs on the server and replicates via `SetOfferedContract` (ObserversRpc). Whether the patch fires on a client-only player needs verification at runtime (single-player runs the player as host, so it should fire). Multiplayer-as-client may require hooking the receive-side instead.
- The patch must capture the `Customer` instance (via `__instance` in Harmony) to drive the counter-offer back at the right customer.
- Don't double-fire on counter-offers: `ContractInfo.IsCounterOffer` is set to `true` for offers we send back. The Phase 5 listener should early-exit when `info.IsCounterOffer == true`.

---

## 3. Customer / contract / deal data shape — *Phases 5, 6, 7*

The offer payload is **`ScheduleOne.Quests.ContractInfo`**:

| Field | Type | Notes |
| --- | --- | --- |
| `Payment` | `float` | total payment, not unit price |
| `Products` | `ScheduleOne.Product.ProductList` | wrapper around list of `(product, quantity)` entries — Phase 6 reads first/only entry |
| `DeliveryLocationGUID` | `string` | resolved to `DeliveryLocation` via the lazy `DeliveryLocation` property |
| `DeliveryLocation` | `ScheduleOne.Economy.DeliveryLocation` | get-only public; private setter resolves from GUID |
| `DeliveryWindow` | `ScheduleOne.Quests.QuestWindowConfig` | `{ IsEnabled, WindowStartTime (mins-of-day), WindowEndTime }` |
| `Expires` | `bool` | |
| `ExpiresAfter` | `int` | minutes until expiry |
| `PickupScheduleIndex` | `int` | which of the customer's pickup schedules to use |
| `IsCounterOffer` | `bool` | **true on offers the player has sent back** — Phase 5 must early-exit on this |

| Field | Value |
| --- | --- |
| Cpp2IL form | `ScheduleOne.Quests.ContractInfo` |
| Il2CppInterop form | `Il2CppScheduleOne.Quests.ContractInfo` |
| Source (Cpp2IL) | `aad-decomp/Assembly-CSharp/ScheduleOne/Quests/ContractInfo.cs` |

Customer-side state Phase 5/7 will read:

- `Customer.OfferedContractInfo` (`ContractInfo` get/protected-set) — the open offer
- `Customer.OfferedContractTime` (`GameDateTime`) — when it was offered
- `Customer.CurrentContract` (`Contract`) — the active accepted contract, if any
- `Customer.DefaultDeliveryLocation` (`DeliveryLocation`) — the customer's default if Phase 7 needs a fallback
- `Customer.NPC` (`NPC`) — for region lookup via `Map.GetRegionFromPosition(npc.transform.position)`

Constants worth knowing (all `public const` on `Customer`):

- `MaxOrderQuantityPerProduct = 1000`
- `OFFER_EXPIRY_TIME_MINS = 600`
- `DEAL_COOLDOWN = 600`
- `QualityTierTolerance = 2`

The accepted contract uses `ScheduleOne.Quests.Contract` (subclass of `Quest`); fields Phase 7 may touch: `ProductList`, `DeliveryLocation`, `DeliveryWindow` (`QuestWindowConfig`), `Payment`, `AcceptTime` (`GameDateTime`).

---

## 4. Counter-offer construct + send — *Phase 7*

The send path is **`ScheduleOne.Economy.Customer.SendCounteroffer(ProductDefinition product, int quantity, float price)`** (protected virtual; exposed public by Il2CppInterop). This is what `CounterofferInterface` calls when the player presses send in the phone UI. It builds a `ContractInfo` with `IsCounterOffer = true` and forwards to the server via `ProcessCounterOfferServerSide` (ServerRpc, `RequireOwnership = false`).

Phase 7 calls `SendCounteroffer` directly on the `Customer` instance captured in Phase 5's `OfferContract` patch. No need to touch the phone UI for time modes 1 and 2.

| Field | Value |
| --- | --- |
| Cpp2IL form | `ScheduleOne.Economy.Customer.SendCounteroffer(ProductDefinition product, int quantity, float price) -> void` |
| Il2CppInterop form | `Il2CppScheduleOne.Economy.Customer.SendCounteroffer(ProductDefinition, int, float)` (public unsafe virtual) |
| Source (Cpp2IL) | `Customer.cs:864` |
| Source (Interop) | `Interop/Il2CppScheduleOne/Economy/Customer.cs:4304` (method-token registration) |

Server-side companion (Phase 7 should *not* call this directly — `SendCounteroffer` invokes it via RPC):

- `Customer.ProcessCounterOfferServerSide(string productID, int quantity, float price)` — `[ServerRpc(RequireOwnership = false)]`. Calls `EvaluateCounteroffer` then either sends an accept message or rejects.
- `Customer.SetContractIsCounterOffer()` — `[ObserversRpc(RunLocally = true)]`, sets `IsCounterOffer` on the offered contract.

UI types involved (Phase 7 *opt-in* for time-mode 3 only):

- `ScheduleOne.UI.Phone.CounterofferInterface` — the panel
- `ScheduleOne.UI.Phone.CounterOfferProductSelector` — product+qty+price entry inside it
- `ScheduleOne.UI.Phone.Messages.MessagesApp.CounterofferInterface` field — entry point

**Phase 7 implementation notes:**

- `SendCounteroffer.price` parameter uses **total dollars** — same semantics as `EvaluateCounteroffer` (which `ProcessCounterOfferServerSide` calls server-side). Verify empirically by checking `customer.CurrentContract.Payment` after the first in-game send; `Payment == total_sent` confirms total-dollar semantics.
- Location and time are **not** parameters to `SendCounteroffer`. Phase 7 chose approach **(a)**: subscribe to `onContractAssigned` (UnityEvent<Contract>) on the customer, then in the handler mutate `Contract.DeliveryLocation` (by `DeliveryLocation` reference) and `Contract.DeliveryWindow` (`QuestWindowConfig.IsEnabled/WindowStartTime/WindowEndTime`), then call `Customer.PlayerAcceptedContract(EDealWindow)` for Fixed/Randomize modes.
- `onContractAssigned` confirmed as `UnityEvent<Contract>` (compiler error when passed `UnityAction` — required `UnityAction<Contract>`).
- Approach **(b)** (Harmony prefix on `ProcessCounterOfferServerSide`) was rejected: `[ServerRpc(RequireOwnership=false)]` routes the call to the host, so a client-side prefix silently no-ops for non-host clients.

---

## 5. Region & delivery-location collections — *Phases 3, 7*

Region enum: **`ScheduleOne.Map.EMapRegion { Northtown, Westville, Downtown, Docks, Suburbia, Uptown }`** — 6 values, integer-backed in declaration order.

Per-region location list: **`ScheduleOne.Map.Map.Regions`** (`MapRegionData[]`, instance field on `Singleton<Map>.Instance`). Each `MapRegionData` has:

- `Region` — `EMapRegion`
- `Name` — display string
- `RegionDeliveryLocations` — `DeliveryLocation[]` (the per-region drop-off points)
- `AdjacentRegions` — `RegionContainer[]`
- `RegionBounds` — `PolygonalZone` (used by `Map.GetRegionFromPosition(Vector3)`)

Each `DeliveryLocation` (MonoBehaviour, scene-placed):

- `LocationName` — display string
- `LocationDescription`
- `StaticGUID` — string identifier persisted across loads (the same key `ContractInfo.DeliveryLocationGUID` references)
- `GUID` — runtime `System.Guid`

| Field | Value |
| --- | --- |
| Cpp2IL form (region enum) | `ScheduleOne.Map.EMapRegion` |
| Il2CppInterop form (region enum) | `Il2CppScheduleOne.Map.EMapRegion` |
| Cpp2IL form (region data) | `ScheduleOne.Map.MapRegionData` |
| Cpp2IL form (collection) | `ScheduleOne.Map.Map.Regions` (`MapRegionData[]`) — `Singleton<Map>.Instance.Regions` |
| Cpp2IL form (location) | `ScheduleOne.Economy.DeliveryLocation` |
| Helpers | `Map.GetRegionData(EMapRegion)`, `Map.GetRegionFromPosition(Vector3)`, `Map.GetUnlockedRegions()`, `MapRegionData.GetRandomUnscheduledDeliveryLocation()` |
| Source | `aad-decomp/Assembly-CSharp/ScheduleOne/Map/{Map.cs,MapRegionData.cs,EMapRegion.cs}`, `Economy/DeliveryLocation.cs` |

**Region → location values (gap to fill at runtime):**

`DeliveryLocation` instances are MonoBehaviours placed in Unity scenes (`level0`/`level1`/etc., not in the managed assembly). Their `LocationName` and `StaticGUID` strings are scene data and are **not** present in the Cpp2IL output. Phase 3's hardcoded list cannot be transcribed from this dump alone.

Phase 3 / Phase 5 should:

1. At first runtime, walk `Map.Instance.Regions` and log each `MapRegionData.Region` together with its `RegionDeliveryLocations[i].LocationName` and `StaticGUID`.
2. Paste that table into the Phase 3 hardcoded list.
3. Keep the runtime-discovery code as the fallback path the master plan calls for.

| Region | Locations | GUIDs |
| --- | --- | --- |
| `Northtown` | _to be filled at runtime_ | _to be filled at runtime_ |
| `Westville` | _to be filled at runtime_ | _to be filled at runtime_ |
| `Downtown` | _to be filled at runtime_ | _to be filled at runtime_ |
| `Docks` | _to be filled at runtime_ | _to be filled at runtime_ |
| `Suburbia` | _to be filled at runtime_ | _to be filled at runtime_ |
| `Uptown` | _to be filled at runtime_ | _to be filled at runtime_ |

`Map.FINAL_REGION = EMapRegion.Uptown` is documented as the unlock progression's terminal region.

---

## 6. Time-of-day type used on a deal — *Phases 3, 7*

There are three time concepts in play, in order from most-coarse to most-fine:

**Coarse — `ScheduleOne.Economy.EDealWindow`** (the user-facing setting, 4 windows × 6h):

```
Morning, Afternoon, Night, LateNight
```

**Window bounds — `ScheduleOne.Economy.DealWindowInfo`** (struct):

- `int StartTime`, `int EndTime` — minutes-of-day, `[0..1440)`
- `const int WINDOW_DURATION_MINS = 360` (6h)
- `const int WINDOW_COUNT = 4`
- Statics: `DealWindowInfo.Morning`, `Afternoon`, `Night`, `LateNight`
- Helpers: `DealWindowInfo.GetWindowInfo(EDealWindow) -> DealWindowInfo`, `DealWindowInfo.GetWindow(int time) -> EDealWindow`

**Fine — `ScheduleOne.Quests.QuestWindowConfig`** (used inside `ContractInfo`):

- `bool IsEnabled`
- `int WindowStartTime`, `int WindowEndTime` — also minutes-of-day

**Absolute — `ScheduleOne.GameTime.GameDateTime`** (struct):

- `int elapsedDays`
- `int time` — minutes within day
- `GetMinSum()`, `AddMins(int)`, comparison operators
- Used for `Customer.OfferedContractTime` and `Contract.AcceptTime`

| Field | Value |
| --- | --- |
| Cpp2IL form | `ScheduleOne.Economy.EDealWindow` (enum) |
| Il2CppInterop form | `Il2CppScheduleOne.Economy.EDealWindow` |
| Cpp2IL form | `ScheduleOne.Economy.DealWindowInfo` (struct) |
| Cpp2IL form | `ScheduleOne.Quests.QuestWindowConfig` (class) — the field that lives on `ContractInfo.DeliveryWindow` |
| Cpp2IL form | `ScheduleOne.GameTime.GameDateTime` (struct) |
| Source | `aad-decomp/Assembly-CSharp/ScheduleOne/Economy/{EDealWindow.cs,DealWindowInfo.cs}`, `Quests/QuestWindowConfig.cs`, `GameTime/GameDateTime.cs` |

**Settings mapping recommendation for Phase 3:**

- Time-mode "fixed": store as `int` minutes-of-day `[0..1439]`.
- Time-mode "randomize": two `int` minutes-of-day values (lo, hi). Sample uniformly in range, then map to the nearest valid `EDealWindow` if the game requires a window enum at the API boundary (verify in Phase 7).
- Time-mode "wait for player": opt-out — phone surfaces, no automation.

`PlayerAcceptedContract` server-side accepts an `EDealWindow` parameter (`Customer.PlayerAcceptedContract(EDealWindow window)` and `Customer.SendContractAccepted(EDealWindow window, bool trackContract)`), so the time the mod surfaces back to the game is most likely an `EDealWindow`, not a raw minute. Phase 7 should confirm whether a finer-grained time can be set on the resulting `Contract.DeliveryWindow` directly.

---

## Cross-reference summary table

| Symbol | Cpp2IL namespace | Il2CppInterop namespace | Phase |
| --- | --- | --- | --- |
| `Customer` | `ScheduleOne.Economy` | `Il2CppScheduleOne.Economy` | 5, 6, 7 |
| `Customer.OfferContract` | (instance, public virtual) | same, `public unsafe virtual` | 5 |
| `Customer.NotifyPlayerOfContract` | (protected virtual) | `public unsafe virtual` | 5 |
| `Customer.SendCounteroffer` | (protected virtual) | `public unsafe virtual` | 7 |
| `Customer.ProcessCounterOfferServerSide` | (private, ServerRpc) | `public unsafe` | 7 (reference only) |
| `Customer.EvaluateCounteroffer` | (protected virtual) | `public unsafe virtual` | 6 (acceptance oracle — TOTAL price; non-deterministic) |
| `Customer.GetOfferSuccessChance` | (instance, public) | same | 6 (rejected — empirically broken / dead API; see §1) |
| `Customer.OfferedContractInfo` / `OfferedContractTime` / `CurrentContract` / `DefaultDeliveryLocation` / `NPC` | properties on `Customer` | same, public unsafe getters | 5, 7 |
| `ContractInfo` | `ScheduleOne.Quests` | `Il2CppScheduleOne.Quests` | 5, 6, 7 |
| `Contract` | `ScheduleOne.Quests` | `Il2CppScheduleOne.Quests` | 7 |
| `MessagingManager` / `MSGConversation` / `MessageChain` | `ScheduleOne.Messaging`, `ScheduleOne.UI.Phone.Messages` | `Il2CppScheduleOne.*` | 5 (alt path only) |
| `EMapRegion` | `ScheduleOne.Map` | `Il2CppScheduleOne.Map` | 3, 7 |
| `Map` (singleton) / `MapRegionData` | `ScheduleOne.Map` | `Il2CppScheduleOne.Map` | 3, 7 |
| `DeliveryLocation` | `ScheduleOne.Economy` | `Il2CppScheduleOne.Economy` | 3, 7 |
| `EDealWindow` / `DealWindowInfo` | `ScheduleOne.Economy` | `Il2CppScheduleOne.Economy` | 3, 7 |
| `QuestWindowConfig` | `ScheduleOne.Quests` | `Il2CppScheduleOne.Quests` | 3, 7 |
| `GameDateTime` | `ScheduleOne.GameTime` | `Il2CppScheduleOne.GameTime` | 3, 7 |

## Outstanding questions for later phases

These don't block any Phase, but Phase 6 / 7 should resolve them at the start of their work:

1. ~~**Determinism of `GetOfferSuccessChance`.**~~ Resolved Phase 6: function is pure but always returns 0 for our baskets and has no in-game callers. Engine uses `Customer.EvaluateCounteroffer` instead; Phase 7 then replaced that with a deterministic probability reimplementation. See §1.
2. ~~**Location and time on counter-offers.**~~ Resolved Phase 7: approach (a) chosen — subscribe to `onContractAssigned` (UnityEvent<Contract>), mutate `Contract.DeliveryLocation` and `Contract.DeliveryWindow`, call `PlayerAcceptedContract(window)` for Fixed/Randomize. See §4.
3. **Multiplayer-as-client.** `OfferContract` runs server-side. In a multiplayer session where the local player is a client (not host), confirm whether the patch fires at all and whether `SendCounteroffer` from a client correctly round-trips through the server. **Deferred to Phase 8.**
4. ~~**EDealWindow vs. minute-precision time.**~~ Resolved Phase 7: Phase 7 ships window-only. `PlayerAcceptedContract(EDealWindow)` is the API boundary; `Contract.DeliveryWindow.WindowStartTime/EndTime` are also set via `DealWindowInfo.GetWindowInfo(window)` for belt-and-suspenders.
