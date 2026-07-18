# Plan 28 — Typed MSFS output for TelemFFB (A:/L:/K:/B:) + smooth axis write

Give TelemFFB a **typed MSFS write layer** — target an `A:` sim var, `L:` local
var, `K:` key event, or `B:` input event, chosen per binding — and route the
**controlled axis** (`telemffb_controls_axes`) through it. Two drivers: (1) fix
the axis-choppiness bug (see §1 — it's a missing SimConnect **priority flag**,
not the var type), and (2) generalize output to all four types, mirroring
SimHub's Plan 24 `MsfsVarOut`, which also serves the FFB-API writes and pedal
control (SimHub drives H500C pedals via `B:PEDALS_PRESET`).

> **Root cause corrected.** Earlier this plan blamed the `K:` mechanism and
> proposed switching to `A:`. **Wrong** — SimHub streams the *same*
> `K:AXIS_CYCLIC_LATERAL_SET` / `…LONGITUDINAL_SET` / `AXIS_COLLECTIVE_SET`
> events to the H500C *smoothly*. The real difference is the SimConnect send
> flags (§1). So the immediate fix is **one flag on the existing K: send**, not
> a switch to `A:`.

**Related:** [Plan 26](26_telemffb_python_backend.md) (DIY backend), the FFB-API
rig-side impl (writes `L:FFB_*`), and SimHub's Plan 24 (`A:`/`L:`/`K:`/`B:`
writeback, all live-validated on MSFS 2024 — the source of truth for the `B:`
transport).

---

## 1. Why (measured, not theorized)

Instrumentation (`DIY_AXIS_DEBUG`, Plan-26 backend) proved the axis pipeline end
to end on the rig:

- `get_input()` polled **165–290 Hz** — our position is clean.
- `AXIS_*_SET` **queued ~56 Hz** and **actually transmitted ~56/axis/s** via
  `sc.send_event` (SC-loop flush ~700 Hz, queue backlog 1) — TelemFFB side is
  fully fast.
- MSFS animates the control at **~2 Hz**.

TelemFFB is sending fine; MSFS applies it slowly. But **not because it's a `K:`
event** — SimHub streams the *same* `K:AXIS_CYCLIC_LATERAL_SET` etc. to the
H500C smoothly (graph template `heli_unboosted_msfs.json`). The difference is the
`TransmitClientEvent` **flags**: SimHub sends with
`groupId = SIMCONNECT_GROUP_PRIORITY_HIGHEST` +
`flags = SIMCONNECT_EVENT_FLAG_GROUPID_IS_PRIORITY`
([MsfsSimConnectClient.cs:962](../../SimHubPlugin/Msfs/MsfsSimConnectClient.cs#L962)),
so MSFS applies it every frame. TelemFFB's `send_event` sends with (almost
certainly) group 0 / no flags, so MSFS parks it in a low-priority group serviced
~2 Hz. **Fix = send the axis events with that priority flag** — same event, same
mechanism, one flag.

## 2. Current TelemFFB capability (the gap)

| Type | Mechanism | Status in TelemFFB |
|------|-----------|--------------------|
| `A:` sim var | `set_simdatum_to_msfs` → `SetDataOnSimObject` | ✅ supported |
| `L:` local var | `send_event_to_msfs('L:…')` → `set_simdatum` | ✅ supported |
| `K:` key event | `send_event_to_msfs` → `TransmitClientEvent` | ✅ supported, but **without the priority flag** → choppy when streamed (§1) |
| `B:` input event | `SetInputEvent` (enumerate → hash → set) | ❌ **missing** — net-new (SimHub uses it for H500C pedals: `B:PEDALS_PRESET`) |

So the work is: (a) **send `K:` axis events with the `GROUPID_IS_PRIORITY` +
`HIGHEST` flag** (the actual choppiness fix), (b) add the **`B:` transport**
(needed for pedals parity + general use), (c) a small **typed dispatch** routing
a `prefix:name` target to the right mechanism, (d) a **per-axis output binding**
so `telemffb_controls_axes` can target any type.

## 3. Design

### 3.1 Typed write dispatch
One entry point, e.g. `write_var(target: str, value, units=None)`, dispatching on
prefix:
- `A:` / `L:` → `set_simdatum_to_msfs` (already queued + flushed).
- `K:` → `send_event_to_msfs` (existing).
- `B:` → new `set_input_event_to_msfs` (see 3.2).

`L:` and `K:` already exist; this just formalizes them behind one typed call so
callers (axis output, FFB-API writes) don't hard-code a mechanism.

### 3.2 `B:` input events (the new transport)
Port SimHub's Plan 24 approach: **enumerate** the aircraft's input events
(`SimConnect_EnumerateInputEvents` → name→hash map, cached per aircraft; note the
>4 KB reply needs a growable rx buffer per Plan 24), then **set** by hash
(`SimConnect_SetInputEvent`). Queue + flush on the SC thread like the others.
Rebuild the hash map on aircraft change. This is the only genuinely new SC code.

### 3.3 Per-axis output binding
Each controlled axis gets a configurable target instead of the hard-coded
`AXIS_*_SET`:
- `target` — a `K:`/`A:`/`L:`/`B:` name,
- `units` and value **range/scale** (normalized → target units).

Defaults follow the H500C graph (proven smooth): **`K:AXIS_CYCLIC_LATERAL_SET`
/ `…LONGITUDINAL_SET` / `AXIS_COLLECTIVE_SET`** for cyclic+collective (now with
the priority flag), **`B:PEDALS_PRESET`** for pedals. Lives in the aircraft
config next to `telemffb_controls_axes`. Plain `AXIS_*_SET` (`K:`, no flag)
remains the fallback for non-DIY users — but note the flag fix (§4) makes even
that smooth, so it's a strict improvement.

## 4. First application — fix the axis choppiness
Send the streamed `K:AXIS_*_SET` events with `groupId = GROUP_PRIORITY_HIGHEST`
and `flags = EVENT_FLAG_GROUPID_IS_PRIORITY`, matching SimHub. Confirm on the sim
PC whether pysimconnect's `send_event` exposes group/flags (→ pass them) or not
(→ add a dedicated high-priority axis-send that calls `TransmitClientEvent` with
the flag). Re-measure with `DIY_AXIS_DEBUG` + watch the sim: the animation should
track at frame rate. **No `A:` switch needed** — same `K:` event, plus the flag.

## 5. Phasing
1. **Priority-flag fix (the bug).** Transmit cyclic/collective `K:AXIS_*_SET`
   with `GROUPID_IS_PRIORITY`+`HIGHEST`; verify smooth with `DIY_AXIS_DEBUG`.
   Smallest possible change; unblocks the axis feel.
2. **Typed dispatch.** `write_var(target, …)` routing `A:`/`L:`/`K:`/`B:` by
   prefix; K: path carries the priority flag; existing A:/L:/K: reused.
3. **`B:` input-event transport.** Enumerate/hash/set (Plan 24) — needed for
   **pedals** (`B:PEDALS_PRESET`, per the H500C graph) and general use.
4. **Per-axis output binding.** Config per axis (`target`/`units`/`range`),
   defaulting to the H500C-proven targets (K:AXIS for cyclic/collective, B: for
   pedals); `K:` `AXIS_*_SET` fallback preserves non-DIY behavior.
5. **Unify FFB-API + LVAR writes** (`L:FFB_*`, etc.) onto `write_var`.

## 6. Risks / notes
- **Confirm pysimconnect's `send_event` flags** — the fix assumes it currently
  sends `AXIS_*_SET` without `GROUPID_IS_PRIORITY`+`HIGHEST`. Verify on the sim
  PC; if it already sets them, the cause is elsewhere and we re-measure.
- **`B:` enumerate cost** — cache per aircraft; the reply can exceed 4 KB (Plan
  24 growable-buffer note).
- **Don't regress non-DIY users** — `K:`/`AXIS_*_SET` stays the default when no
  typed target is configured (HPG etc. keep working).
- **Scope discipline** — this is an *output* layer; input binding stays in
  SimHub/graph (Plan 25).
