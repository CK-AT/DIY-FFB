# Proposal: An FFB-Oriented LVAR API for MSFS2024 Helicopters

**Status:** Proposal / request for comment
**Author:** Christian Krenn / DIY FFB project

---

## 1. What this is

This document proposes a **small, standard set of LVARs** (a "Helicopter FFB LVAR API")
a helicopter can expose to make FFB implementation straightforward.

---

## 2. Design goals for the API

- **Normalized and documented.** Positions/commands in a fixed unit and range
  with an explicit sign convention. No per-aircraft scale factors, no guessing
  inversion.
- **Intent, not implementation.** Expose *"trim actuator unclutched"* and *"trim reference is
  here"* explicitly.
- **Trim behaviour under full control of the aircraft.** As the rig receives all necessary signals directly there is no need to mirror the
  trim behaviour (TR/beep) on the FFB side and thus no risk of fighting or misaligned trim implementations.
- **Direction-explicit & mode-gated.** Every variable names who writes it, and the
  API only takes effect while the rig has switched the aircraft into FFB mode.
- **Discoverable & versioned.** The rig can detect whether the aircraft implements
  the API, which revision and which features.
- **Extendable.** Feature flags allow adding support for e.g. aero cues like VRS, RBS and ETL in future versions.

**Convention:** all variables live in the `L:FFB_*` namespace and use
dimensionless normalized units unless noted. Runtime variables are updated every
frame; the discovery/capability variables (§3.1 `API_VERSION` and `FFB_FEATURES`)
are static and read once.

---

## 3. Proposed API

The API is **mode-gated**: the FFB software turns it on, and while on, the
aircraft yields trim authority to the physical stick and publishes the state the
rig needs. Direction is stated per variable — **rig→aircraft** = the rig writes,
**aircraft→rig** = the aircraft writes.

The rig drives control position through the normal axis inputs (the physical
stick moves the aircraft as usual); this API covers only **trim, feel, and
hydraulics** — there is no need to bind "compensated" control inputs any more.

### 3.1 Discovery & mode

Controls: `CYCLIC`, `COLLECTIVE`, `PEDALS` (substitute `<CONTROL>`). A control
covers its axes — `CYCLIC` = both cyclic axes, `COLLECTIVE` and `PEDALS` one each.

| LVAR | Dir | Unit | Meaning |
|------|-----|------|---------|
| `L:FFB_API_VERSION` | aircraft→rig | number | API revision (start at `1`). `0`/absent = not implemented; the rig treats the aircraft as unsupported. |
| `L:FFB_FEATURES` | aircraft→rig | bitfield | Aircraft capabilities packed into one word — read **once** on connect (re-read on aircraft change), valid when `API_VERSION >= 1`. `0`/absent = no optional features, so the rig assumes nothing is trimmed. Bit layout below. This is capability, not runtime state: it does not change in flight and is not part of the per-frame group (§3.4). |
| `L:FFB_<CONTROL>_ENABLED` | rig→aircraft | bool | The FFB software sets `1` to switch **that control** into FFB mode — declaring the rig provides force feedback for its axes — and `0` on disconnect. Per-control, so a user with (say) only an FFB cyclic enables `CYCLIC` while collective and pedals stay in normal mode. Driving the mode from the rig removes the need for additional configuration settings on the aircraft's tablet/EFB that a user could arm by accident (and then wonder why the controls feel wrong). Must **not persist across aircraft load/reload** — power-up default is `0` — so a crashed or disconnected rig never leaves the next flight stuck in FFB mode. |

**`L:FFB_FEATURES` bit layout** (bit 0 = LSB). Positions are **frozen once published — append only**; never reorder or reuse a retired bit.

| Bit | Meaning |
|-----|---------|
| 0 | `CYCLIC` has trim — an auto-trim / trim reference the rig should spring toward (`_TRIM` is meaningful) |
| 1 | `COLLECTIVE` has trim |
| 2 | `PEDALS` has trim |
| 3–7 | reserved (trim / feel group) |
| 8+ | reserved (future capability groups) |

Read as a double, round to the nearest integer, then mask — `((int)round(value) >> bit) & 1`. Integer values are exact up to 2⁵³, so there is ample headroom.

### 3.2 Aircraft contract for a control while `L:FFB_<CONTROL>_ENABLED == 1`

For each control whose flag is `1`, the aircraft:

- **Zeroes that control's trim demand** — cyclic → `ROTOR LONGITUDINAL TRIM PCT` /
  `ROTOR LATERAL TRIM PCT`; collective / pedals → their equivalents — held at
  **0** so the sim's virtual control does not fight the physical one. More
  generally: suppress internal trim application and let the rig own that control; this removes the need to manipulate the control input to "compensate" for the internal trim depending on TR/fly-through state.
- **Publishes** the read-group LVARs (§3.4) for that control's axes every frame.
- **Restores** normal behavior when the flag returns to `0`, resuming its own trim/feel. This path is **required** so that disabling a control — or a rig disconnect — cleanly returns it to normal mode; a user re-arming mid-session for feel reasons is not an expected use case, but the restore path must exist.

### 3.3 rig → aircraft (signals the rig writes)

Axes: `CYCLIC_PITCH`, `CYCLIC_ROLL`, `COLLECTIVE`, `PEDALS`. Substitute `<AXIS>`.

| LVAR | Unit | Meaning |
|------|------|---------|
| `L:FFB_<AXIS>_FLY_THROUGH` | bool | The rig detects a **fly-through** condition on this axis based on force or position deviation, depending on the FFB HW capabilities. The aircraft decides on how to use this information, most likely disabling autopilot/ASE auto-trim on this axis or its associated control. |

### 3.4 aircraft → rig (state the rig reads)

These variables are only meaningful while that control's `L:FFB_<CONTROL>_ENABLED == 1`; the rig must not consume them for a control in normal mode.

Per axis (`CYCLIC_PITCH`, `CYCLIC_ROLL`, `COLLECTIVE`, `PEDALS`):

| LVAR | Unit | Range | Meaning |
|------|------|-------|---------|
| `L:FFB_<AXIS>_TRIM` | number | −1..+1 | **Trim actuator position** within the control input range — the auto-trim's computed reference, independent of the zeroed `ROTOR *_TRIM PCT` (§3.2). Tracks the actuator; while trim release (TR) is active on that axis it follows the **current control input** — This removes all discontinuities (like `ROTOR *_TRIM PCT` jumping to zero under TR). If the control's `L:FFB_FEATURES` trim bit (§3.1) is `0`, the control has no trim system — the rig ignores `_TRIM` and applies no trim spring. |
| `L:FFB_<AXIS>_HYD_ASSIST_LOSS` | number | 0..1 | Hydraulic pressure loss as a fraction of nominal. The rig can use it (if available) to model heavy/locked controls on hydraulic loss and cold-and-dark. `0` = full boost, `1` = unassisted/locked. This convention ensures that the fallback on a missing value defaulting to zero is 'full boost' for an aircraft without hydraulics. |

Per control (cyclic trim can only be unclutched for both axes simultaneously):

| LVAR | Unit | Meaning |
|------|------|---------|
| `L:FFB_CYCLIC_TR_ON` | bool | Cyclic trim actuators **unclutched** (like TR pressed or e.g. TRIM FEEL CYCL = OFF). Covers both cyclic axes. |
| `L:FFB_COLLECTIVE_TR_ON` | bool | Collective trim actuator **unclutched** (like C-SYNC pressed or e.g. COLLECTIVE TRIM FEEL = OFF). |
| `L:FFB_PEDALS_TR_ON` | bool | Pedals trim actuator **unclutched** (symmetry / future expansion). |

### 3.5 Conventions (normative)

- Normalized values are **−1..+1**, `0` = neutral / mid-travel. Positive = **pitch
  nose-up**, **roll right**, **collective up**, **right pedal (yaw right)**.

### 3.6 Reserved legacy names

The following LVARs predate this API and are already written by shipped FFB software
(TelemFFB) to *any* helicopter, including aircraft that do not implement this standard:

| LVAR | Dir | Unit | Status |
|------|-----|------|--------|
| `L:FFB_HANDS_ON_CYCLIC` | rig→aircraft | bool | Legacy — reserved, do not reuse |
| `L:FFB_HANDS_ON_CYCLICX` | rig→aircraft | bool | Legacy — reserved, do not reuse |
| `L:FFB_HANDS_ON_CYCLICY` | rig→aircraft | bool | Legacy — reserved, do not reuse |
| `L:FFB_FEET_ON_PEDALS` | rig→aircraft | bool | Legacy — reserved, do not reuse |

They carry roughly the meaning `L:FFB_<AXIS>_FLY_THROUGH` (§3.3) now carries, but with
no mode gate and no version gate. They are **reserved**: a future revision of this API
must not reuse these names for anything else, and an aircraft may continue to read them
if it already does.

New implementations should use `L:FFB_<AXIS>_FLY_THROUGH` instead. These names are the
sole documented exception to the "write no `L:FFB_*` variable to an unsupported
aircraft" rule in §4 step 1 — a rig may keep writing them from its legacy code path,
but must not emit them as part of this API.

### 3.7 Why there is no rig→aircraft capability advertisement

This API is deliberately asymmetric: `L:FFB_FEATURES` (§3.1) advertises *aircraft*
capabilities, and there is no counterpart by which the aircraft learns what the rig can
do. This is a decision, not an oversight.

The obvious candidate would be telling the aircraft whether `L:FFB_<AXIS>_FLY_THROUGH`
(§3.3) is derived from real force sensing or merely from position deviation. That
information is not actionable: the rig always publishes the best signal its hardware can
produce, and the aircraft has no alternative source to fall back to. Advertising the
derivation method would only invite the aircraft to second-guess a value it cannot
improve on.

`L:FFB_API_VERSION` therefore remains the only gate. If a future revision adds
rig→aircraft variables whose presence genuinely changes aircraft behavior, a
`L:FFB_RIG_FEATURES` bitfield can be added then — absent reads as `0`, so aircraft
written against this revision stay correct.

---

## 4. Lifecycle (rig side)

A worked sequence for the FFB software. Steps 1–3 run once per aircraft load;
step 4 is the per-frame loop; step 5 is the exit.

1. **Detect.** Read `L:FFB_API_VERSION`. If it is `0`/absent or below the rig's
   minimum supported revision, treat the aircraft as unsupported and stay in
   legacy mode — do not write any `L:FFB_*` variable, except the reserved legacy
   names in §3.6. Stop here.
2. **Read capabilities once.** Read `L:FFB_FEATURES` and cache the bits
   (re-read on aircraft change, not per frame).
3. **Enter FFB mode.** For each control the rig physically provides, write
   `L:FFB_<CONTROL>_ENABLED = 1`. Leave controls the user has no FFB hardware
   for at `0` so they stay in normal mode.
4. **Per-frame loop** (while enabled):
   - *Write* `L:FFB_<AXIS>_FLY_THROUGH` per axis from measured force/position
     deviation.
   - *Read* `L:FFB_<AXIS>_TRIM`, `L:FFB_<AXIS>_HYD_ASSIST_LOSS`, and the
     per-control `L:FFB_<CONTROL>_TR_ON`.
   - *Update effects:* spring toward `_TRIM` — **unless** that control's
     `FFB_FEATURES` trim bit is `0` (then no trim spring); update friction/damping based on `_HYD_ASSIST_LOSS`; drop the trim spring while
     `_TR_ON == 1` so the control is free to reposition.
5. **Exit.** When a control's FFB ends — the user disables it, the rig
   disconnects, or the aircraft unloads — write `L:FFB_<CONTROL>_ENABLED = 0`.
   The aircraft restores normal trim/feel (§3.2).
