# Plan 27 — Standalone DIY FFB Configurator (broker-connected)

A **dedicated tuning app for the DIY FFB rig**, launched from TelemFFB's
"configurator" button when the DIY backend is active, that connects **through
the broker** (Plan 26) so the user can tune the rig **live while TelemFFB is
driving it**. Replaces the dead "Launch VPforce Configurator" action (Rhino-only,
never installed on a DIY rig) with something meaningful for our hardware.

**Prerequisite:** [Plan 26](26_telemffb_python_backend.md) — the serial broker,
the wire codec, the protobuf, and the `telemffb.hw.diy` client
(`BrokerClient`/`Topology`/framing). This plan reuses all of it.

---

## 1. The key enabler: passthrough is (almost) free

The broker is a **protocol-agnostic** relay: it write-arbitrates any client's
frames onto the port and broadcasts every decoded uplink `Message` to all
clients (Plan 26 §5.1). So a configurator is **just another broker client** —
no broker change:

- **Downstream (configurator → gateway):** send `AxisConfig` / `FunctionConfig`
  (config writes), `AxisAction` (calibration / homing / restart), tagged as they
  already are; the gateway dispatches by axis/function.
- **Upstream (gateway → configurator):** receive `AxisConfig` / `FunctionConfig`
  (in reply to `return_*_config`), `StaticBalanceResult`, `DeviceInfo`,
  `AxisState` — all via the existing broadcast; filter to what we asked for.

Live tuning **alongside** TelemFFB works because the ESP32 applies config
changes as they arrive while it keeps consuming the per-tick `FlightFfbAction`
stream. The broker's write lock guarantees the two clients' frames never
interleave on the wire.

```
                       ┌─ TelemFFB instances  (FlightFfbAction stream)
 broker (owns COMx) ───┤
                       └─ DIY Configurator    (AxisConfig / FunctionConfig / AxisAction)
        ▲ broadcasts all uplink (AxisState, config replies, StaticBalanceResult) to both
```

---

## 2. Relationship to the SimHub plugin (scope reality)

The SimHub **DiyFfbPlugin** already tunes the rig — and it is a large WPF app
(graph editor, force curves, tiered config, calibration flows). A standalone
configurator that reaches **full parity is a major undertaking** and would
duplicate years of work. So this plan is explicitly **not** "reimplement the
SimHub plugin." Two viable framings — **decide the intent before building**:

- **A) Lightweight live-tuner (recommended MVP).** A focused app for the knobs
  that matter to *feel*, editable live over the broker: per-function spring /
  damping / friction / output range / DDS harmonics, per-axis oscillation guard,
  and calibration (homing, static balance). Rich authoring (force-curve splines,
  the graph) stays in SimHub. Small, high-value, achievable.
- **B) Eventual SimHub replacement for DIY.** Full config authoring in a
  standalone app. Large, multi-phase; only if the goal is to stop depending on
  SimHub for the flight use case.

The rest of this plan assumes **(A)**, with a note where (B) would extend it.

### 2.1 First shot: tune the host-side references (not ESP32 config)

The **very first shot is narrower and safety-driven**: tune the **`DiyFfbDevice`
references** — the host-side scaling that turns TelemFFB's 0..1 ratios into the
absolute N / mm / N·s/mm the firmware consumes (Plan 26 §6.0). These live
**host-side** (`AxisReferences` per function), because the firmware already
runs on absolute units; TelemFFB just has to scale correctly before sending.

Per function: `spring_n_per_mm`, `damper_ns_per_mm`, **`base_damping_ns_per_mm`
(safety floor — never zero)**, `friction_n`, `load_n` (trim range comes from
discovery). The aggregator already applies these with conservative, over-damped
placeholder defaults; the configurator's job #1 is exposing them so each rig
gets **safe, tuned** values — most importantly a real base damping so a
direct-drive axis is never left undamped.

**These references never go to the gateway** — they're applied host-side before
the `FlightFfbAction` is built. So editing them doesn't need the broker; the
live-update path is a **host channel to the running `DiyFfbDevice`** (re-read a
per-function config file, or a small control socket / TelemFFB IPC), *not* the
broker. The broker passthrough (below) is for the *later* ESP32-config editing
and for live `AxisState` feedback while tuning.

Persist as a host-side per-function config file (loaded by `DiyFfbDevice` at
open, re-read on change). ESP32 config editing (§3) is a subsequent phase.

---

## 3. Config surface — later phase (ESP32 config via broker)

All already defined in [`diy_ffb_protocol.proto`](../../proto/diy_ffb_protocol.proto):

| Concern | Message / fields | Direction |
|---|---|---|
| Discover topology | `GatewayState.axes_present`, `AxisAction.return_function_config` → `FunctionConfig` (`base.linked_axes`) | read |
| Per-function feel | `FunctionConfig.flight_control` — `centering_spring_const`, `damping`, `pos_min/max`, `phase_offset`, `vib_harmonic_ratios`, `vib2_harmonic_ratios`; `FunctionConfig.friction`, `simulated_mass` | read + write |
| Function base | `FunctionBase` — `output_min/max`, `output_mode`, `controller_output_axis`, `store` | read + write |
| Per-axis hardware | `AxisConfig` — `kinematic_parameters`, `steps_per_mm`, loadcell rating, `OscillationGuard`, `StaticBalanceConfig` | read + write |
| Calibration | `AxisAction.start_homing`, `start_static_balance_calibration` → `StaticBalanceResult` | write → read |
| Identity | `DeviceInfoRequest` → `DeviceInfo` (fw / board / uid) | read |
| Save/load to file | `ConfigItemsList` (list of `AxisConfig`/`FunctionConfig`) — same format SimHub uses | local file |

Read current config with the `return_*_config` actions (same discovery the
`DiyFfbLink` already does); write by sending the edited `AxisConfig`/
`FunctionConfig`. Persist on-device via `FunctionBase.store`; optionally
save/load `ConfigItemsList` files for profiles.

**Config authority caveat:** while TelemFFB is driving, per-tick
`FlightFfbAction` scalars (`k_spring`, `k_damper`, …) are authoritative for those
terms; `FunctionConfig` values like `damping`/`centering_spring_const` are the
rest/safety values applied when no FFB frames flow. So the configurator tunes
the *baseline + hardware + vibration character + calibration*; the live gains
during flight come from TelemFFB. Surface this in the UI so it isn't confusing.

---

## 4. Architecture

- **Standalone Python/Qt app** (PyQt6, matching TelemFFB) that reuses
  `telemffb.hw.diy`: `framing`, `client.BrokerClient`/`Topology`, and the
  generated `diy_ffb_protocol_pb2`. No new transport code.
- **Connect:** `BrokerClient` to the broker's TCP address (default
  `127.0.0.1:45111`, passed in by the launcher). Run discovery like `DiyFfbLink`
  (`GatewayState` → probe each axis → cache `AxisConfig`/`FunctionConfig`).
- **UI:** a tab/panel per discovered function + per axis; sliders/fields bound to
  the config fields in §3; "Apply" sends the edited message, "Store" sets
  `FunctionBase.store`; calibration buttons send `AxisAction`.
- **Live feedback:** subscribe to `AxisState` (position/force) for a live
  readout while tuning — nice for verifying feel.

## 5. Launch integration (TelemFFB side)

- When `--backend diy`, repurpose the `vpconf_action` (and any button) to
  **"Launch DIY Configurator"** → spawn the configurator as a subprocess,
  passing the broker address (and role). When backend is Rhino, keep the
  existing `launch_vpconf`.
- Guard for "broker not running" (external-broker setups) — the configurator
  retries connect (BrokerClient already does) and shows a clear status.
- The configurator is a separate process (like TelemFFB's child instances), so
  it doesn't block the UI and can be closed independently.

## 6. Location & dependencies (open)

The configurator depends on `telemffb.hw.diy` (broker client + protobuf), which
now lives **in the TelemFFB fork**. Options:

- **A) Live in the fork** next to the bridge (`telemffb/diy_configurator/` or
  under `telemffb/hw/diy/`). Zero dependency friction; TelemFFB launches it
  trivially. Odd home for a "DIY" tool, but consistent with where the bridge
  already lives. *(Recommended for the MVP.)*
- **B) Own package/repo**, depending on the bridge as an installable package.
  Cleaner ownership; reintroduces the cross-repo/packaging work Plan 26 avoided.

Recommend **(A)** until it proves worth extracting.

## 7. Phasing (MVP)

1. **Host-side references config (first shot, §2.1) — ✅ file + live reload done.**
   `references.py`: human-editable per-function JSON (`spring_n_per_mm`,
   `damper_ns_per_mm`, **`base_damping`**, `friction_n`, `load_n`), template
   written on first run, malformed-safe. `DiyFfbDevice` loads it at open and
   **re-reads live** (~1×/s) so edits apply while driving; `--references PATH`
   (default = platform config dir). No broker — pure host-side scaling. Tests
   green. *Remaining: a GUI editor on top of the JSON (nice-to-have).*
2. **Live feedback + host control channel.** Read live `AxisState`
   (position/force) — via the running `DiyFfbDevice` (host IPC) or a broker
   client — so references can be tuned while TelemFFB drives, seeing the effect.
3. **TelemFFB launch button** wired for `--backend diy` → spawn the configurator.
4. *(Later)* **ESP32 config editing via broker** — `AxisConfig`/`FunctionConfig`
   (kinematics, DDS ratios, oscillation guard), `AxisAction` calibration
   (homing, static balance → `StaticBalanceResult`), `ConfigItemsList` profiles.
   This is where the broker passthrough (§1) is used.
5. *(Later / framing B)* richer authoring (force curves, graph) if we commit to
   replacing SimHub for DIY.

## 8. Risks / open questions

- **Scope creep toward SimHub parity** — the biggest risk. Hold the MVP line
  (feel + calibration, live); leave rich authoring in SimHub.
- **Config authority overlap** with TelemFFB's live gains (§3 caveat) — needs
  clear UI framing so users know what they're changing.
- **Two writers on the config** (SimHub-authored baseline vs configurator edits)
  — if a user also uses SimHub, whose config wins? On-device `store` is
  last-writer; document that the configurator and SimHub shouldn't both be
  managing the same rig simultaneously.
- **Location** (§6) — pick A vs B before starting.
- **Field-level validation / units** — reuse the ranges the SimHub plugin and
  firmware already enforce; don't invent new ones.
