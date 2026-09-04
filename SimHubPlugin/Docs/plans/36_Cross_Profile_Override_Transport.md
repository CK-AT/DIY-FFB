# Cross-Profile Transport of Function / SimVar / Active-Function Overrides

## Problem Statement

The Profile Browser's **Apply** action can copy a source profile's *graph tuning*
onto another vehicle, but it silently drops every other per-profile customization.
A user who has dialed in a vehicle cannot carry its **function tuning**, **MSFS
sim-var bindings**, or **active/used functions** to a sibling profile without a
manual JSON export→import round-trip — and that round-trip is key-preserving, so it
cannot even retarget a different vehicle.

The `AircraftFfbProfile` (`DiyFfbPluginSettings.cs:132-175`) holds five pieces of
portable state:

| State | Field | Transported by Apply today |
|-------|-------|----------------------------|
| Graph selection + rotor index | `GraphPath`, `XPlaneRotorIndex` | ✅ ("Use graph") |
| Graph tuning (param values) | `GraphParamValues` | ✅ ("Copy tuning") |
| Function tuning (config overrides) | `FunctionOverrides` | ❌ |
| MSFS sim-var bindings | `MsfsReadVarOverrides`, `MsfsWriteVarOverrides` | ❌ |
| Active / used functions | `ActiveFunctionIds` | ❌ |

The single runtime cross-profile copy is `ApplyProfileToVehicle`
(`DiyFfbPlugin.cs:3777-3829`); it copies only `GraphParamValues` (+ graph path +
rotor index). The word "tuning" in the browser (`CheckCopyTuning`, `HasTuning`,
`TunedParamCount`) means *graph params only*, so the toggle already under-delivers
on its own label.

## Decision (from design review, revised during implementation)

- **Extend the existing method** (`ApplyProfileToVehicle`) and the Apply card
  toggles rather than introducing a new mechanism.
- **Granular toggles**, not one "copy everything" switch — each opt-in copy is a
  separate checkbox, enabled only when the source actually carries that data.
- **Allow + warn, not hard-gate.** The original plan hard-disabled the extra toggles
  across vehicle categories. Implementation found this unworkable: category detection
  (`GetActiveGraphCategory`, `DiyFfbPlugin.cs:4002`) scans only the **top-level**
  graph's Output `SignalGroup`s for `FlightStick*`, but the MSFS templates put their
  real Output nodes inside **embedded sub-graphs** — the top level only has bus-send
  outputs (`SignalGroup: "Shared"`). So the detector returns `Vehicle` for an MSFS
  helicopter graph, making category gating both unreliable *and* fail-open for exactly
  the graphs in use. Meanwhile a cross-graph copy is **inert, not harmful**: any
  function id / MSFS alias the destination graph doesn't use is ignored at scan time
  (like orphan `GraphParamValues`). So instead of disabling, the card shows a
  **non-blocking warning** when the source graph differs from the graph the
  destination will land on.
- Reuse the deep-clone helpers the dirty-tracking snapshot path already uses
  (`CloneMsfsOverrides`, the copies in `BuildCurrentAircraftProfile`,
  `DiyFfbPlugin.cs:3470-3480`); add a JSON-round-trip `CloneFunctionOverrides` for the
  one type without a `Clone()`.
- Dirty-tracking metadata (`LastReviewedGraphHash`, `LastReviewedParamSnapshots`) is
  **never** transported; keep that boundary explicit.

## Why copies are inert-not-harmful across graphs

- `ActiveFunctionIds` / `FunctionOverrides` are keyed by **global function IDs**; a
  function id the destination graph doesn't use is never activated/applied (see the
  `HasBaseConfig` guard in `ApplyProfileFunctionOverrides`,
  `TieredConfigOrchestrator.cs:416`).
- MSFS sim-var bindings are keyed by alias; an alias not declared by the destination
  graph is skipped by `UpdateMsfsCustomVars` / `UpdateMsfsWritableVars`.

So the worst case of a cross-graph copy is dead dict entries, not wrong behavior. The
warning exists to prevent user *confusion*, not to prevent a fault.

## Design

### 1. Broaden `ApplyProfileToVehicle`

Add three flags alongside `useTuning`:

```csharp
public void ApplyProfileToVehicle(
    string gameId, string carId, string graphPath,
    AircraftFfbProfile sourceProfile,
    bool useTuning,            // GraphParamValues (existing)
    bool copyFunctionTuning,   // FunctionOverrides
    bool copySimVarBindings,   // MsfsRead/WriteVarOverrides
    bool copyActiveFunctions)  // ActiveFunctionIds
```

After the existing graph/param block (`DiyFfbPlugin.cs:3799-3811`), add:

- **Function tuning** — deep-copy `sourceProfile.FunctionOverrides` into
  `destProfile.FunctionOverrides` (replace, not merge — a function's override is
  atomic). Clone each `FunctionConfigOverrides` so later edits don't alias the source.
- **Sim-var bindings** — `destProfile.MsfsReadVarOverrides = CloneMsfsOverrides(src…)`
  and the same for write; reuse the existing helper.
- **Active functions** — `destProfile.ActiveFunctionIds = new HashSet<int>(src…)`.

Keep the existing active-vehicle reload block. When the destination is the **active**
vehicle, copying `FunctionOverrides` / `ActiveFunctionIds` must also re-run the tiered
apply so the ESP32 receives the new config: after the copy, call
`ApplyProfileFunctionOverrides` (the same entry point used on profile load,
`TieredConfigOrchestrator.cs:375`) and, for sim-var changes, `ApplyMsfsVarOverrides()`
(Plan 34). Non-active destinations persist only, exactly as today.

Keep the signature backward-compatible via an overload or default `false` args so the
legacy `PromptForGraphTemplate` path (`DiyFfbPlugin.cs:2624-2638`) is unaffected.

### 2. Mismatch warning (replaces category gating)

`UpdateGraphMismatchNote(entry)` in the dialog: when any graph-scoped toggle is on,
compare the source graph filename against the graph the destination will end up on
(the source graph when "Use graph" is checked, else the destination's current graph
via `_plugin.GetVehicleGraphPath`). Different filenames → show `TextGraphMismatch`.
Fail-open: if either graph can't be resolved, show nothing rather than a false alarm.

### 3. Apply card UI (`ProfileBrowserDialog.xaml` / `.xaml.cs`)

Extend the pinned Apply card that today holds `CheckUseGraph` / `CheckCopyTuning`
(`ProfileBrowserDialog.xaml.cs`, `UpdateApplyTarget`):

- Add `CheckCopyFunctionTuning`, `CheckCopySimVarBindings`, `CheckCopyActiveFunctions`.
- Each is **enabled only when the source has that data** — mirroring the existing
  `CheckCopyTuning.IsEnabled = entry.HasTuning` pattern with new
  `entry.HasFunctionTuning` / `HasSimVarBindings` / `HasActiveFunctions` derived props
  on `ProfileBrowserEntry`. Unchecked by default (opt-in) so a plain "apply" stays
  graph+tuning as before.
- Thread the new checkbox states through `ApplySelected` → `ApplyProfileToVehicle`
  (non-active) and through new `UseFunctionTuning`/`UseSimVarBindings`/
  `UseActiveFunctions` result flags → `ApplyProfileFromBrowser` (active), consumed in
  `DiyFfbPluginUI.xaml.cs`.
- Fix the misleading label: rename `CheckCopyTuning` display text to **"Copy graph
  tuning"** so the copy categories read clearly.

`UpdateApplyEnabled` extends its `anyToggle` check to include the three new toggles;
`OnApplyToggleChanged` also refreshes the mismatch note.

## Files touched

| File | Change |
|------|--------|
| `DiyFfbPlugin.cs` | New flags on `ApplyProfileToVehicle` + copy blocks + active-dest re-apply (`ApplyProfileFunctionOverrides` / `ApplyMsfsVarOverrides`); `CloneFunctionOverrides` helper; `ApplyProfileFromBrowser` forwards the flags |
| `ProfileBrowser/ProfileBrowserDialog.xaml(.cs)` | Three new toggles + mismatch note; `UpdateGraphMismatchNote`; result flags; `"Copy graph tuning"` relabel; thread flags through `ApplySelected` |
| `ProfileBrowser/ProfileBrowserEntry.cs` | `HasFunctionTuning` / `HasSimVarBindings` / `HasActiveFunctions` derived props |
| `DiyFfbPluginUI.xaml.cs` | Active-path consumer forwards the three new `dialog.Use*` flags |

No settings-schema change — the fields already exist and already serialize.

## Edge cases

- **Orphan overrides after copy** — a source `FunctionOverride` / sim-var alias not
  present in the destination's graph is inert at apply time (same as orphan
  `GraphParamValues` today). No pruning needed on copy.
- **Active destination** — must re-run tiered apply + `ApplyMsfsVarOverrides()` so the
  ESP32 and MSFS registrations pick up the copied state; a copy that only mutates the
  settings dict would look applied but not take effect until the next vehicle switch.
- **Partial data** — a source may have function tuning but no sim-var bindings; each
  toggle is independently enabled by its own `Has*` prop.
- **Same-vehicle apply** — already blocked by `IsSourceSameAsDestination`
  (`ProfileBrowserDialog.xaml.cs:580-594`); no change.
- **Double-click apply** bypasses the disabled button — the new flags read from the
  checkbox states, which follow the same `Has*` enablement, so double-click inherits
  the same behavior.

## Testing

- Unit: `ApplyProfileToVehicle` copies exactly the selected categories and nothing
  else (assert `LastReviewed*` untouched); clones are deep (mutating the source after
  copy doesn't affect the destination).
- Manual: dial in vehicle A (function overrides + MSFS bindings + active-function set),
  Apply to sibling B with all toggles on, switch to B, confirm the ESP32 config and
  MSFS registrations match A. Apply to a vehicle on a different graph and confirm the
  mismatch warning shows and the copy still lands (orphans inert).

## Out of scope

- A true **"Clone to…"** action (retarget a profile onto a new vehicle key) — the
  natural companion, tracked separately; this plan only broadens the existing Apply.
- Bundling graph files into exported profiles (Plan 33/34 limitation).
- Per-field selective copy within a category (all-or-nothing per category here).
