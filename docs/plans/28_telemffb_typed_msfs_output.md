# Plan 28 — Typed MSFS output for TelemFFB (A:/L:/K:/B:) + smooth axis write

Give TelemFFB a **typed MSFS write layer** — target an `A:` sim var, `L:` local
var, `K:` key event, or `B:` input event, chosen per binding — and route the
**controlled axis** (`telemffb_controls_axes`) through it. Immediate driver: the
axis-choppiness bug is that `AXIS_*_SET` (a `K:` key event) is applied by MSFS at
~2 Hz; writing the same value as an `A:` sim var is smooth (that's how SimHub
does it). Generalizing to all four types mirrors SimHub's Plan 24 `MsfsVarOut`
and serves the FFB-API writes too.

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

So MSFS applies **streamed `AXIS_*_SET` key events** at ~2 Hz. SimHub never hits
this because it drives control axes via **`A:` sim-var writes**
(`SetDataOnSimObject`), a different mechanism. Fix = let TelemFFB write the axis
that way too — and, while we're there, make the *target type* a first-class
choice.

## 2. Current TelemFFB capability (the gap)

| Type | Mechanism | Status in TelemFFB |
|------|-----------|--------------------|
| `A:` sim var | `set_simdatum_to_msfs` → `SetDataOnSimObject` | ✅ supported |
| `L:` local var | `send_event_to_msfs('L:…')` → `set_simdatum` | ✅ supported |
| `K:` key event | `send_event_to_msfs` → `TransmitClientEvent` | ✅ supported (choppy when streamed) |
| `B:` input event | `SetInputEvent` (enumerate → hash → set) | ❌ **missing** — net-new |

So the work is: (a) add the **`B:` transport**, (b) a small **typed dispatch**
that routes a `prefix:name` target to the right mechanism, (c) a **per-axis
output binding** so `telemffb_controls_axes` uses it (default `A:`).

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
- `target` (e.g. `A:YOKE Y POSITION`, or a `B:`/`K:`/`L:` name),
- `units` and value **range/scale** (normalized → target units),
- default = the **`A:` sim var** equivalent of today's `AXIS_*_SET`, so the
  choppiness is fixed out of the box.

Lives in the aircraft config next to `telemffb_controls_axes` (per axis:
cyclic lat/long, collective, tail rotor). Falls back to `AXIS_*_SET` (`K:`) if
no target is set, preserving current behavior for non-DIY users.

## 4. First application — fix the axis choppiness
Wire the heli/flight controls mixins' axis send through `write_var(target, …)`
with the per-axis binding, default `A:`. Re-measure with `DIY_AXIS_DEBUG` +
watch the sim: the animation should track at frame rate. Ship the `A:` default;
`B:`/`K:`/`L:` become available for cases where the right target is an input
event or a bespoke L:var.

**Open item (needs your SimHub knowledge):** the exact `A:` vars + units/ranges
SimHub writes for each control axis — cyclic lateral/longitudinal, collective,
tail rotor. Those become the built-in defaults.

## 5. Phasing
1. **Typed dispatch + `A:` axis default.** `write_var` prefix routing; controlled
   axis writes its `A:` var; verify smooth with `DIY_AXIS_DEBUG`. *(Fixes the bug.)*
2. **Per-axis binding config.** Expose `target`/`units`/`range` per axis; default
   to the `A:` var; `K:` fallback for compatibility.
3. **`B:` input-event transport.** Enumerate/hash/set (Plan 24), exposed through
   the same typed dispatch; usable for axes and FFB-API writes.
4. **Unify FFB-API + LVAR writes** (`L:FFB_*`, etc.) onto `write_var` so all MSFS
   output goes through one typed path.

## 6. Risks / notes
- **`A:`-var settability varies** — not every control var accepts
  `SetDataOnSimObject`; use the ones SimHub has proven (§4 open item), not
  guesses.
- **`B:` enumerate cost** — cache per aircraft; the reply can exceed 4 KB (Plan
  24 growable-buffer note).
- **Don't regress non-DIY users** — `K:`/`AXIS_*_SET` stays the default when no
  typed target is configured (HPG etc. keep working).
- **Scope discipline** — this is an *output* layer; input binding stays in
  SimHub/graph (Plan 25).
