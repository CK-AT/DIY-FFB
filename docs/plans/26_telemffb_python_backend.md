# Plan 26 — TelemFFB Python Backend for the DIY FFB Pedal

Add a new **device backend** to [VPforce-TelemFFB](https://github.com/walmis/VPforce-TelemFFB)
(`D:\Projects\VPforce-TelemFFB`) so its flight-sim FFB engine can drive our
ESP32 pedal/flight-control hardware directly — **without touching TelemFFB's
effects engine and without any ESP32 firmware changes**. The backend is a
Python module that presents a `FFBRhino`-compatible device object to TelemFFB
but, underneath, speaks our existing **COBS-framed protobuf-over-serial**
protocol to the gateway.

This is the recommended path over on-device USB HID PID (see the sibling
analysis): far less work, no impersonation of VPforce hardware, no firmware
risk. Its cost is that it is **TelemFFB-specific** — it does nothing for other
DirectInput FFB titles. It is also **flight-only**: it lets our HW be used as
TelemFFB-driven pedals / collective / cyclic-axis / trim, not for sim racing.

**Primary motivation — hardware-validate the Helicopter FFB LVAR API.** The
concrete driver is to **test, on our own hardware, TelemFFB's implementation of
the MSFS Helicopter FFB LVAR API** we authored. Two documents already exist and
define the work on either side of the wire:

- **The contract:** [`Standards/MSFS_Helicopter_FFB_API.md`](../../Standards/MSFS_Helicopter_FFB_API.md)
  — the `L:FFB_*` LVAR API (discovery, mode-gating, per-frame trim/feel/hyd
  read + fly-through/enable write).
- **The rig-side implementation plan:** `VPforce-TelemFFB/docs/ffb_simvar_api_implementation.md`
  — adds an `FFBApiHelicopter` class that reads `_TRIM`/`_HYD_ASSIST_LOSS`/
  `_TR_ON`, writes `_ENABLED`/`_FLY_THROUGH`, and drives the effects engine
  (spring toward trim, soften on TR, friction from hyd loss).

That rig-side plan's testing/milestones say "**validate on hardware**" — but
today TelemFFB can only drive VPforce devices. **This backend is the missing
enabler for that step:** it lets `FFBApiHelicopter`'s forces reach our pedal.
Running TelemFFB as a `pedals` or `collective` instance against our HW is the
end-to-end test bed for the API.

**Why this fit is clean** (and re-prioritizes the work below): the FFB API is
about **trim, feel, and hydraulics** — which map entirely onto TelemFFB's
*condition effects* (spring/damper/friction) plus spring-center, i.e. our
`k_spring`/`trim_offset`/`k_damper`/`k_friction`. So the condition-effect path
(§6) is the critical one; the lossy constant/periodic→DDS vibration mapping is
**off the critical path** for this goal and can trail. And **fly-through
write-back needs real per-frame force/position readback** (§7): our load-cell
pedal senses *force*, which the rig-side plan explicitly prefers over position
deviation — an advantage over position-only devices.

**Secondary benefit:** once the backend exists, TelemFFB's broader per-aircraft
library (DCS / IL-2 / BMS supplemental effects, gunfire, engine rumble,
ETL/VRS) is also available on our HW — but that is a bonus, not the driver.

---

## 1. The two architectures, and why they line up

Investigation of both sides established that the FFB *contract* is essentially
identical; only the transport and the aggregation boundary differ.

**TelemFFB → VPforce Rhino** (`telemffb/hw/ffb_rhino.py`): the engine allocates
individual **effect blocks** on the device (`create_effect`), sets each block's
parameters via HID reports, and starts/stops them. The **device firmware sums
all active blocks** into motor force each tick.

- **Condition effects** — spring, damper, friction, inertia, spring-adjuster,
  detent — are sent as **coefficients / saturation / center-point offset**
  only (`FFBReport_SetCondition`, report 103). **The firmware reads its own
  position sensor and integrates position → force.** TelemFFB never sends
  position-derived force for these.
- **Constant / periodic effects** are resolved **host-side**: TelemFFB sends an
  exact `magnitude` (−1..1) and `direction` (degrees); the device just outputs
  that vector.
- The engine reads current axis position back every telemetry frame via
  `HapticEffect.device.get_input()` and uses it only to *adjust* condition
  parameters (trim center, G-offset), never to compute the base spring/damper.

**SimHub plugin → our ESP32** ([diy_ffb_protocol.proto:608](../../proto/diy_ffb_protocol.proto#L608),
[FlightControlFunction.cpp:45](../../ESP32/src/FlightControlFunction.cpp#L45)):
the **host pre-aggregates** everything into one `FlightFfbAction` per tick:
`k_spring`, `k_damper`, `k_friction` (coefficients), `trim_offset` (spring
center, mm), `load_force` (resolved constant force, N), `buffet_amp`, and DDS
vibration amplitudes. **Our firmware also reads its own load-cell/position
sensor and integrates the spring/damper/friction coefficients into force** —
identical to the Rhino condition model.

**The consequence:** the semantic model matches. The backend's job is purely
(a) transport translation and (b) collapsing TelemFFB's *many independent
effect blocks* into our *single aggregated `FlightFfbAction` per tick*. The
firmware needs nothing new.

```
TelemFFB effects engine (unchanged)
   │  HapticEffect["spring"].spring(coef).start()
   │  HapticEffect["cm"].periodic(f,mag,dir).start()
   ▼
DiyFfbDevice  (NEW — assigned to HapticEffect.device)
   ├─ effect table: {handle_id → {type, coef/mag/freq/dir/center/envelope, playing}}
   ├─ aggregator tick (≈ our FFB rate): table → FlightFfbAction + DdsFundamentals
   ├─ serial TX: protobuf → CRC16/Modbus → COBS → 0x00  @ 3 Mbaud
   └─ serial RX: AxisState → get_input() views (axisXY / forceXY / CP_XY)
   ▼
ESP32 gateway → CAN → axis controller (unchanged)
```

---

## 2. In scope

- A new backend module `telemffb/hw/ffb_diy.py` providing `DiyFfbDevice`
  (drop-in for `FFBRhino`) and a translating effect handle. Assigned to
  `HapticEffect.device`, so **the entire effects engine, all mixins, and all
  aircraft profiles work unmodified**.
- An **effect-table aggregator** collapsing all active TelemFFB effects into
  per-axis `FlightFfbAction` (+ `DdsFundamentals`) messages per aggregator tick.
- **Two-axis (cyclic) support as the PRIMARY path** — one logical TelemFFB
  device fans out to **two** DIY functions: `FUNCTION_ID_FLIGHT_STICK_ROLL`
  (TelemFFB X) and `FUNCTION_ID_FLIGHT_STICK_PITCH` (TelemFFB Y). See §6.1.
  Single-axis roles (collective, pedals) are the degenerate case of the same
  machinery.
- The **wire codec**: our COBS + CRC16/Modbus framing (§4) and the vendored
  generated protobuf (§7).
- **Serial port selection.** The protobuf link is a **UART bridge** — a generic
  USB-serial COM port (Arduino `Serial` = UART0 @ 3 Mbaud, since
  `ARDUINO_USB_CDC_ON_BOOT=0`). It is **not** the native-USB device, so it does
  **not** carry a DIY-specific VID/PID; auto-detecting by `0x303b`/gateway-PID
  would be wrong (that identity is the HID gamepad — see the note below).
  Discovery is therefore **user-selected COM port** (as the SimHub plugin does),
  with optional matching by the bridge chip's VID/PID as a convenience only.
- The single application branch point: a device-backend selector at
  [`main.py:360`](../../../VPforce-TelemFFB/main.py) plus a CLI/system-setting
  flag choosing the DIY backend and COM port.
- **Input readback**: map `AxisState{position, force}` into the
  `get_input()` view surface the engine polls each frame.
- **Envelopes applied host-side in software** (the aggregator re-sends every
  tick anyway), covering TelemFFB's attack/decay on constant/periodic effects.

## 3. Out of scope

- **Any ESP32 firmware change.** The existing `FlightFfbAction` path is
  sufficient. If a gap is found (§6), it becomes a separate firmware plan.
- **On-device USB HID PID.** Separate, larger effort; deliberately not chosen
  here.
- **Sim racing.** TelemFFB is flight-only.
- **Simultaneous SimHub + TelemFFB control** of the same axis. They are
  mutually exclusive — see §5. The port being single-open enforces this.
- **Config authoring UI in TelemFFB.** v1 assumes the DIY functions are already
  configured as flight axes by the SimHub plugin (§5). Pushing `FunctionConfig`
  from TelemFFB is a future extension (§8).

---

## 4. Wire contract the backend must implement

> **Two distinct USB interfaces — don't conflate them.** The gateway exposes
> (1) a **native-USB HID gamepad** and (2) a **UART-bridge serial COM port**.
> The runtime source of truth for the HID identity is
> [`CommManager.cpp:114-127`](../../ESP32/src/CommManager.cpp#L114) (it
> **overrides** the platformio `USB_PRODUCT`/`USB_PID` defines): VID `0x303b`,
> PID `0x8210 + gateway_id` (gw1 = `0x8211`) / `0x8220 + axis_id`, product
> `"DIY-FFB-Gateway-N"` / `"DIY-FFB-Axis-N"`. **This backend uses neither** — it
> talks protobuf over the *UART-bridge* COM port (a separate, generic
> USB-serial device). The `0x303b` identity is the HID gamepad only.

Confirmed against the SimHub plugin ([FrameSerial.cs](../../SimHubPlugin/FrameSerial.cs),
[DiyFfbPlugin.cs:100](../../SimHubPlugin/DiyFfbPlugin.cs#L100)) and firmware
([SerialManager.cpp](../../ESP32/src/SerialManager.cpp)):

**Frame (TX), per message:**
1. Serialize a protobuf `Message` (the `oneof payload`) → bytes.
2. Append **CRC16/Modbus** over those bytes, 2 bytes little-endian.
3. **COBS-encode** the (protobuf ‖ CRC) blob.
4. Append `0x00` delimiter. Write to serial.

**Frame (RX):** read until `0x00` → COBS-decode → verify trailing 2-byte
CRC16/Modbus over the remainder → parse `Message`.

- **Baud:** `3000000`. Port: 8N1.
- **Startup:** send a few `0x00` bytes first to establish COBS sync (firmware
  does the same, [SerialManager.cpp:38](../../ESP32/src/SerialManager.cpp#L38)).
- Python deps: `pyserial`, `cobs` (or a 15-line COBS impl), `crcmod`/`crccheck`
  for CRC16-Modbus, plus generated `*_pb2.py`.

**Downlink messages we send:**
- `FFBAction.flight_ffb` (`FlightFfbAction`) — per aggregator tick, the core.
- `DdsFundamentals` — when a periodic effect's fundamental frequency changes.
- (Future) `FunctionConfig` / `AxisConfig` — see §8.

**Uplink messages we consume:**
- `AxisState{axis_id, position, force}` — feeds `get_input()`.
- `DeviceInfo` (in reply to `DeviceInfoRequest`) — feeds `.info` / `.serial` /
  `get_firmware_version()`.
- `GatewayState` — presence/axis map for connection status.

---

## 5. Prerequisites & the exclusivity rule

- The DIY functions the role uses must already be running as
  **`FlightControlFunction`**s on assigned axes, each with a `FunctionConfig`
  loaded (base center, output range, and `vib_harmonic_ratios` — these define
  what our DDS slots mean). **For the primary cyclic test that is TWO configured
  functions** — `FLIGHT_STICK_ROLL` and `FLIGHT_STICK_PITCH` — on two axes.
  **v1 relies on the SimHub plugin having configured these**; the backend only
  streams `FFBAction` and reads `AxisState`.
- **SimHub and TelemFFB must not both drive the axis.** Both emit
  `FlightFfbAction` on the same serial link, and the port is single-open — so
  whichever process opens the COM port wins and the other is locked out. This
  naturally enforces exclusivity, but must be **documented prominently**: to use
  TelemFFB, close/stop the SimHub plugin's connection to that gateway.
- Because `vib_harmonic_ratios` live in the (SimHub-authored) `FunctionConfig`,
  the vibration *character* is partly fixed outside TelemFFB in v1. A neutral
  "TelemFFB" profile in SimHub (integer harmonic ratios 1..5, sane center/range)
  is a recommended companion artifact.

**For the LVAR-API end-to-end test specifically** (the primary goal): the API
is a *proposal* — no shipping aircraft implements the aircraft side yet. So the
test also needs an **aircraft-side stub** publishing `L:FFB_API_VERSION = 1`
and a movable `L:FFB_<AXIS>_TRIM` (± `_HYD_ASSIST_LOSS`, `_TR_ON`). Options: a
tiny MSFS WASM/gauge, MobiFlight, or a throwaway script that writes those LVARs.
Both existing plans call this out (rig-plan §8 "a stub aircraft… minimally";
proposal §3.2 aircraft contract). This stub is **independent of this backend**
and can be built in parallel; without it, only the *rig-side* behavior (correct
`_ENABLED` write, correct forces for a given synthetic `_TRIM`) can be checked.

### 5.1 Multi-instance vs. our single serial port (the choke point)

**The tension:** TelemFFB runs **one OS process per control**. The master
auto-launches a child per control that has `autolaunch<Type>` set
([`main.py:102`](../../../VPforce-TelemFFB/main.py) → [`utils.check_launch_instance`](../../../VPforce-TelemFFB/telemffb/utils.py#L3555)),
and **each instance opens its own device** by VID:PID (`-D FFFF:{pid} -t
{type}`, then `HapticEffect.open(...)`). In VPforce hardware these are *separate
physical USB devices*. On our side, cyclic, collective and pedals all live
behind **one gateway on one COM port**, and a serial port is **single-open per
process**. So two or more control instances would each try to open the same
port and all but the first would fail.

**It does NOT affect the primary goal.** The cyclic is a *single* `joystick`
instance; our backend already fans that one process out to the pitch+roll
functions over the one port (§6.1). One process, one port, no collision.

**Resolutions, in order:**

1. **v1 — single instance.** Verify the cyclic with only the `joystick`
   device configured (leave `autolaunchPedals`/`autolaunchCollective` off).
   Test collective / pedals in *separate sessions*, not simultaneously. This
   fully covers the cyclic API verification and needs zero extra plumbing.
2. **Simultaneous multi-control — a serial broker.** A small standalone process
   **owns the COM port** and exposes a local socket / named pipe. Each TelemFFB
   instance's `DiyFfbDevice` connects to the broker instead of the port
   directly. **Downstream is trivial — no demux:** every `FFBAction` already
   carries its `function_id` and the gateway dispatches on it, so the broker
   just **write-arbitrates** (serializes each instance's already-tagged frames
   onto the one port). **Only the upstream needs routing:** the broker reads
   `AxisState` frames and **routes each to the instance that owns that
   function/axis**. This preserves TelemFFB's per-instance model (each instance
   still owns exactly one control's `_ENABLED` + SimConnect) with no engine
   changes, and it **subsumes the SimHub-exclusivity rule** — the broker becomes
   the one port owner, so "who holds the port" is answered once.
3. **Rejected for v1 — collapse to one instance managing all controls.** A
   single TelemFFB instance could drive all functions and write all three
   `_ENABLED` flags, sidestepping the port contention, but it fights the
   master/child, one-SimConnect-per-instance, one-control-per-instance design
   (rig-plan §4, §6). More invasive than the broker for no upside.

**Decision: build the broker first** (not deferred). It is a thin
port-multiplexer, it decouples the serial transport from TelemFFB entirely (the
codec lives in the broker, not smeared across instances), and it means we never
build a direct-port version only to rip it out. **Status: built** — see
[`tools/diy_ffb_bridge/`](../../tools/diy_ffb_bridge/) (`framing.py`,
`broker.py`, `monitor.py`, offline tests green). Even the single-instance cyclic
case now connects through the broker.

---

## 6. Effect → `FlightFfbAction` mapping (the aggregator)

The effect table is keyed by handle id; each `create_effect`/`setX`/`start`/
`stop` updates a slot. **Each TelemFFB effect carries an X and a Y component**
(`coef_x`/`coef_y`, and `parameterBlockOffset` 0/1 in `SetCondition`; `direction`
for constant/periodic). The aggregator **splits by axis** (§6.1) and, each tick,
collapses the active slots into a `FlightFfbAction` **per DIY function**. The
table below is stated per axis — the same rules run once for the X→roll stream
and once for the Y→pitch stream (single-axis roles run one stream only). All
coefficients arrive from TelemFFB as raw ints where **4096 = 1.0 gain**.

| TelemFFB effect | Source params (per axis) | → `FlightFfbAction` field | Notes |
|---|---|---|---|
| `SPRING` (+ `SPRING_ADJUSTER`) | `coef_{x,y}`, `cpOffset{X,Y}` | `k_spring` (Σ coef/4096), `trim_offset` | Sum coefficients; combine centers (dominant-by-coef, see below). Convert cpOffset [-1..1] → mm via axis range. |
| `DAMPER` | `coef_{x,y}` | `k_damper` | Sum coef/4096. |
| `FRICTION` | `coef_{x,y}` | `k_friction` | Sum coef/4096. |
| `INERTIA` | `coef_{x,y}` | *(none)* | **No inertia term in `FlightFfbAction`.** Fold into `k_damper` (approx) or drop — **log the choice** (no silent cap). Candidate firmware gap (§8). |
| `CONSTANT` | `magnitude` [-1..1], `direction` deg | `load_force` (N) | Resolve direction into X/Y components (§6.1); each component → its axis's `load_force`. Scale by configurable N-per-unit (device max force). Apply envelope in SW. |
| `SINE/SQUARE/TRIANGLE/SAWTOOTH*` | `frequency` Hz, `magnitude` [0..1], `direction` | `DdsFundamentals.dds{1,2}` + `vib_amp_slot*` | Direction picks which axis stream(s) receive the vibration. See DDS mapping below. |
| Stall-buffet-style periodic (by effect name convention) | `magnitude` | `buffet_amp` | Optional: route named buffet effects to our dedicated `buffet` element instead of a DDS bank. |
| Envelope on any of the above | attack/decay force+time | applied in **software** | Backend re-sends every tick; ramp the mag/amp itself. |
| `DETENT` | condition block | *(v1: drop, log)* | No detent semantics on our axes; future. |
| Device/master gain (`set_gain`, `FFB_GAIN_*`) | 0..100 | scales all outputs | Maintain a gain vector; apply per class. |

### 6.1 Two-axis cyclic fan-out (the primary path)

Our cyclic is **two independent functions** — `FUNCTION_ID_FLIGHT_STICK_ROLL`
(6) and `FUNCTION_ID_FLIGHT_STICK_PITCH` (5) ([proto:222](../../proto/diy_ffb_protocol.proto#L221)),
each its own linked axis with its own scalar `FlightFfbAction`. TelemFFB, by
contrast, models a cyclic as **one 2-axis device** where X/Y are two parameter
blocks of the same effect. The backend bridges the two models:

- **Axis map** (per proposal §3.5 sign convention: +X = roll right, +Y = pitch
  nose-up):
  - TelemFFB **X (roll)** → `FUNCTION_ID_FLIGHT_STICK_ROLL`
  - TelemFFB **Y (pitch)** → `FUNCTION_ID_FLIGHT_STICK_PITCH`
- **Downlink:** the aggregator emits **two `FFBAction` messages per tick**, one
  per `function_id`, each with that axis's collapsed scalars. (The gateway
  already dispatches `FFBAction` to the correct axis by `function_id` — no
  firmware change.)
- **Condition effects** carry both blocks: `coef_x`/`cpOffsetX` drive the roll
  stream, `coef_y`/`cpOffsetY` the pitch stream. This is the exact shape
  `FFBApiHelicopter` produces — `ffbTrimCyclicRoll` → cpOffsetX,
  `ffbTrimCyclicPitch` → cpOffsetY (rig-plan §4.3).
- **Constant/periodic** specify a `direction` (degrees); resolve to
  `(cos, sin)` and route each component to the roll/pitch stream.
- **Sign & scale** live in one `normalized↔axis` helper per function (mirrors
  rig-plan §4.4) so inversion bugs stay in one place. Confirm roll/pitch sign
  against the actual servo direction on first bring-up.
- **Function-set per TelemFFB role** (device type → functions driven):
  - `joystick`/cyclic → **{roll (X), pitch (Y)}** ← the primary test target
  - `collective` → `FUNCTION_ID_FLIGHT_STICK_COLLECTIVE` (single, mind inversion)
  - `pedals` → `FUNCTION_ID_FLIGHT_PEDALS` (single)

**Combining multiple springs:** our model has one `centering_spring` with one
offset. Sum coefficients; for the center, take the coefficient-weighted mean of
active spring centers (in practice TelemFFB runs one dominant spring plus small
adjusters, so weighted mean ≈ the dominant spring's center). Document the
approximation.

**DDS mapping (the lossy part).** Our firmware exposes **2 fundamental
oscillators, each with up to 5 harmonic slots** at ratios fixed by
`FunctionConfig.vib_harmonic_ratios` (`kAmpScale = 0.01 mm/LSB`,
[FlightControlFunction.cpp:60](../../ESP32/src/FlightControlFunction.cpp#L60)).
TelemFFB may run *many* simultaneous periodics (engine rumble + gunfire + ETL).
Strategy:
1. Bucket active periodics by fundamental frequency.
2. Assign the **two highest-energy fundamentals** to `dds1`/`dds2`
   (`DdsFundamentals`).
3. For each, place its magnitude in the harmonic slot whose configured ratio
   best matches (base fundamental → slot 1).
4. **Log any periodic that could not be placed** (frequency budget exceeded) —
   never silently drop.

This is the one place the fit is imperfect; it is acceptable because our DDS is
a *synthesized-vibration* model, not an arbitrary waveform mixer, and multiple
overlapping rumbles perceptually collapse anyway.

---

## 7. The `get_input()` view surface

The engine polls `HapticEffect.device.get_input()` every telemetry frame and
expects these views (subagent-confirmed call sites across the mixins):
`axisXY()`, `rawAxisXY()`, `CP_XY()`, `forceXY()`, `CP_scaled_axisXY()`, plus
`buttons`/`getPressedButtons()`/`isButtonPressed()`.

**Discovery (required before `AxisState` is interpretable).** `AxisState`
carries only `axis_id` — not `function_id` — so the client must first learn the
axis↔function topology:

1. Read `GatewayState.axes_present` (bitmask, bit 0 = axis 1) → the live axes.
2. For each present axis, send `AxisAction{axis_id, return_function_config = true}`
   → the gateway replies with that axis's `FunctionConfig`, giving its
   `function_id` and `linked_axes` (note the `AXIS_SUBTRACTIVE` /
   `AXIS_INDEPENDENT` flag bits).
3. Build `axis_id → function_id` and `function_id → linked_axes` maps.
   (`return_active_function` gives only the primary axis→function pair — use
   `return_function_config` to get `linked_axes`.)

With those maps, the device attributes each incoming `AxisState` to its function
and combines linked axes. **Match the SimHub plugin's rules exactly** (reference:
[`DiyFfbPlugin.cs:2942-2994`](../../SimHubPlugin/DiyFfbPlugin.cs#L2942)):

- **Cache keyed by the *physical* axis** = `axis_id & AXIS_ID_MASK` (strip the
  `AXIS_SUBTRACTIVE` (128) / `AXIS_INDEPENDENT` (64) flag bits).
- **Function position** = the position of its **primary** linked axis
  (`linked_axes[0]`, masked) — **not** summed; linked axes are
  position-mirrored.
- **Function force** = the **signed sum** of forces over all assigned linked
  axes (mask each, skip `AXIS_UNDEFINED`); **subtractive axes are negated**
  (`AXIS_SUBTRACTIVE` ⇒ `f = -f`) to give the net directional force. (The plugin
  did a plain unsigned sum until commit `96ce7abf`, which fixed
  `GetFunctionForce` to apply this sign — the `DiyFfbDevice` must match.)
- **Differential / per-side force** (e.g. flight pedals): filter linked axes by
  the `AXIS_SUBTRACTIVE` bit (subtractive = LEFT, additive = RIGHT); same-side
  forces add. Positions are mirrored, so only force splits by side.
- Cyclic pitch/roll and collective are single-axis, so both reduce to that one
  axis.

Then assemble the 2-axis view the engine expects (roll→X, pitch→Y). For a cyclic
this means combining the **roll** function's state into X and the **pitch**
function's into Y:
- roll `AxisState.position` → `axisXY().x`; pitch → `axisXY().y` (each in [-1,1]).
- roll `AxisState.force` → `forceXY().x`; pitch → `forceXY().y`. **This drives
  force-based fly-through** — our load cell gives a real per-axis force, which
  the rig-side plan prefers over position deviation (rig-plan §4.3).
- commanded `trim_offset` per function → `CP_XY()` (x=roll, y=pitch).
- Single-axis roles (collective/pedals) populate one component; the other stays
  neutral (0).
- Buttons: our grip buttons arrive via `GripReader`/HID separately, **not** on
  this axis channel — v1 returns no buttons (empty), and grip input stays on its
  own USB HID path. Document that TelemFFB button features are unavailable
  through this backend.

Provide these as small view objects mirroring `FFBReport_Input`'s helpers so no
mixin needs changing.

---

## 8. Generated protobuf & code layout

**Split across two repos** — the protocol/transport/discovery foundation lives
in *our* repo (authoritative, next to the `.proto`); only the TelemFFB-specific
glue lives in the fork.

- **This repo — [`tools/diy_ffb_bridge/`](../../tools/diy_ffb_bridge/) (built):**
  `framing.py` (COBS + CRC16/Modbus), `broker.py` (port multiplexer),
  `client.py` (`BrokerClient` + `Topology` discovery + `AxisStateTracker`
  per-function position/force), generated `diy_ffb_protocol_pb2.py`
  (`regen_proto.sh`), offline tests.
- **TelemFFB fork — new file `telemffb/hw/ffb_diy.py`:** `DiyFfbDevice(QObject)`
  — the FFBRhino-compatible glue. Imports the foundation (via `pip install -e`
  of the bridge package, or a vendored copy), holds the effect table +
  aggregator (→ per-function `FlightFfbAction`), and exposes `get_input()`
  views over `AxisStateTracker`.
- Application wiring (minimal):
  - A backend factory replacing the hard-coded `HapticEffect.open(...)` at
    [`main.py:360`](../../../VPforce-TelemFFB/main.py): if the selected backend
    is `diy`, construct `DiyFfbDevice(port=...)` and assign
    `HapticEffect.device`.
  - CLI/system-setting: `--backend diy --port COMx` (the UART-bridge COM port,
    user-selected as in SimHub; §4 note). Reuse the existing
    `--type pedals|collective` for role.
  - Connect the same Qt signals (`deviceConnected`, `buttonPressed`,
    `buttonReleased`) `main.py` already expects.

**Firmware gaps to watch** (would spill into a separate firmware plan, not this
one): inertia term, per-effect envelope fidelity, >2 simultaneous vibration
fundamentals, detent. None block v1.

---

## 9. Phasing

Ordered to reach the **LVAR-API hardware test** as early as possible; vibration
(off the critical path for that goal) trails.

1. **✅ Broker + codec (done).** COBS/CRC framing + protocol-agnostic serial
   broker (owns port, fans out to clients), in [`tools/diy_ffb_bridge/`](../../tools/diy_ffb_bridge/).
   Offline tests green (`test_framing.py`, `test_broker.py`). `monitor.py`
   decodes the uplink — the first **hardware** smoke test (run broker vs. a
   pedal flight-configured by SimHub, confirm `AxisState`/`GatewayState`/
   `DeviceInfo`). *Remaining: run that hardware smoke test.*
1b. **✅ Client foundation (done).** `client.py` — `BrokerClient`, `Topology`
   discovery (`GatewayState` + `return_function_config`), and `AxisStateTracker`
   (per-function position = primary axis; force = signed sum, subtractive
   negated — matching plugin `96ce7abf`). Offline tests green (`test_client.py`,
   incl. the differential-pedal negation case + a full discovery/FFB round-trip
   through the broker with a fake serial).
2. **✅ Aggregator + `DiyFfbDevice` glue (written; HW-validation pending).**
   `aggregator.py` (`EffectAggregator`) collapses spring/damper/friction/constant
   + spring-center → per-function `FlightFfbAction`; offline tests green
   (`test_aggregator.py`). Fork-side `telemffb/hw/ffb_diy.py` (`DiyFfbDevice`,
   branch `ck_diy_ffb_bridge`) reuses `HapticEffect`/`FFBEffectHandle`, parses
   the report stream into the aggregator, ticks per-function `FlightFfbAction`,
   and builds `get_input()`. *Syntax-checked only — not yet run in the TelemFFB
   env (needs PyQt6/hidapi); no hardware feel test yet.* *(Path `FFBApiHelicopter`
   exercises: spring→`_TRIM`, soften on `_TR_ON`, friction from `_HYD_ASSIST_LOSS`.)*
3. **✅ Two-axis cyclic fan-out (§6.1) — implemented.** X→roll / Y→pitch split in
   both the aggregator (two `FlightFfbAction`s/tick) and `get_input()` (two
   `AxisState`s → `axisXY()`/`forceXY()`); tested offline in `test_aggregator.py`.
   *Remaining: verify per-axis feel + force-based fly-through on hardware.*
   **This is the primary device shape** — most users have a cyclic.
4. **LVAR-API end-to-end (cyclic).** With the aircraft-side stub (§5), run
   TelemFFB's `FFBApiHelicopter` as the **cyclic (`joystick`) instance** driving
   both stick functions: confirm `_ENABLED` lifecycle, per-axis spring-center
   tracks `ffbTrimCyclicRoll`/`ffbTrimCyclicPitch`, `_TR_ON` unclutch, hydraulic
   loss → friction, and per-axis `_FLY_THROUGH` write-back. Then repeat for
   collective/pedals as single-axis instances. **This is the goal.**
5. **Selector + docs.** `main.py` backend branch, user-selected UART-bridge COM
   port (optionally convenience-matched by the bridge chip's VID/PID),
   SimHub-exclusivity + companion-profile documentation.
6. **Vibration (secondary).** Constant/periodic → DDS mapping + `DdsFundamentals`
   + software envelopes. Enables the broader effect library (engine rumble,
   gunfire, ETL) on our HW — not required for the API test.

## 10. Effort & risk

Self-contained, medium module (~600–900 LOC + generated proto). No firmware
work. Main risks, in priority order:
(a) **Two-axis correctness** (the primary path): X/Y↔roll/pitch sign and scale,
and keeping the two per-function `FlightFfbAction` streams in sync each tick.
Mitigated by a single `normalized↔axis` helper per function and a bring-up sign
check against real servo direction.
(b) v1 depends on an externally-authored `FunctionConfig` — now **two** for a
cyclic (mitigated by a companion SimHub profile pair; removed later by §8 config
push).
(c) The DDS many→2 vibration mapping is inherently lossy — but it is **secondary**
(Phase 6) and off the LVAR-API critical path, so it does not gate the goal.
(d) Upstreaming to TelemFFB requires accepting a non-`0xFFFF` device — until
then, run from our fork/source.
