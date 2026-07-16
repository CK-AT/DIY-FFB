"""Effect-table aggregator: TelemFFB effects -> per-function FlightFfbAction.

The pure, Qt-free core of the TelemFFB ``DiyFfbDevice``. TelemFFB allocates
independent effect blocks (spring, damper, friction, constant, periodic) and
expects the *device* to sum them. Our firmware instead wants pre-aggregated
per-tick scalars, so this collapses the active effect table into one
``FlightFfbAction`` per DIY function.

Axis model (plan 26 §6.1): TelemFFB is one 2-axis device (X/Y parameter blocks);
our cyclic is two functions. The caller supplies an ``assignments`` map from
logical axis -> (function_id, AxisScale); this fans X->roll, Y->pitch (single-
axis roles pass only 'x').

Scope: condition effects (spring/damper/friction) + constant force + spring
center — the LVAR-API critical path. Periodic->DDS vibration, inertia, and
envelopes are explicit TODOs (plan 26 §6 / Phase 6); they are counted and
logged, never silently dropped.
"""

from __future__ import annotations

import logging
import math
from dataclasses import dataclass, field
from typing import Dict, Optional, Tuple

import diy_ffb_protocol_pb2 as pb

log = logging.getLogger("diy_ffb_aggregator")

# Effect type ids — mirror ffb_rhino.py (EFFECT_*). Duplicated here so the
# aggregator has no dependency on the TelemFFB package.
EFFECT_CONSTANT = 1
EFFECT_SPRING = 8
EFFECT_DAMPER = 9
EFFECT_INERTIA = 10
EFFECT_FRICTION = 11
EFFECT_SPRING_ADJUSTER = 14
PERIODIC_TYPES = {3, 4, 5, 6, 7}  # square/sine/triangle/sawtooth up/down

X_BLOCK, Y_BLOCK = 0, 1  # SetCondition.parameterBlockOffset


@dataclass
class AxisScale:
    """Per-function conversion from normalized units to FlightFfbAction units."""
    mm_half_range: float = 1.0   # normalized cpOffset [-1..1] -> trim_offset (mm)
    max_force_n: float = 1.0     # normalized load [-1..1] -> load_force (N)


@dataclass
class _Condition:
    coef: float = 0.0        # positiveCoefficient / 4096  (gain)
    cp_offset: float = 0.0   # cpOffset / 4096  (normalized center, -1..1)


@dataclass
class EffectState:
    """One TelemFFB effect block, as parsed from the report stream."""
    effect_id: int
    type: int = 0
    started: bool = False
    # condition effects: per-axis parameter block (0=X, 1=Y)
    conditions: Dict[int, _Condition] = field(default_factory=dict)
    # constant/periodic
    magnitude: float = 0.0       # normalized [-1..1] (constant) / [0..1] (periodic)
    direction_deg: float = 0.0
    frequency_hz: float = 0.0


class EffectAggregator:
    """Holds the effect table and collapses it to per-function FlightFfbActions."""

    def __init__(self) -> None:
        self._effects: Dict[int, EffectState] = {}
        self.master_gain: float = 1.0            # 0..1
        self._unsupported_seen: set[int] = set()  # effect types logged once

    # --- table mutation (driven by parsed reports) ------------------------
    def ensure(self, effect_id: int, effect_type: int) -> EffectState:
        e = self._effects.get(effect_id)
        if e is None:
            e = EffectState(effect_id=effect_id, type=effect_type)
            self._effects[effect_id] = e
        else:
            e.type = effect_type
        return e

    def set_condition(self, effect_id: int, effect_type: int, block: int,
                      coef: float, cp_offset: float) -> None:
        e = self.ensure(effect_id, effect_type)
        e.conditions[block] = _Condition(coef=coef, cp_offset=cp_offset)

    def set_constant(self, effect_id: int, magnitude: float, direction_deg: float) -> None:
        e = self.ensure(effect_id, EFFECT_CONSTANT)
        e.magnitude = magnitude
        e.direction_deg = direction_deg

    def set_periodic(self, effect_id: int, effect_type: int, frequency_hz: float,
                     magnitude: float, direction_deg: float) -> None:
        e = self.ensure(effect_id, effect_type)
        e.frequency_hz = frequency_hz
        e.magnitude = magnitude
        e.direction_deg = direction_deg

    def start(self, effect_id: int) -> None:
        e = self._effects.get(effect_id)
        if e:
            e.started = True

    def stop(self, effect_id: int) -> None:
        e = self._effects.get(effect_id)
        if e:
            e.started = False

    def remove(self, effect_id: int) -> None:
        self._effects.pop(effect_id, None)

    def clear(self) -> None:
        self._effects.clear()

    # --- aggregation -------------------------------------------------------
    def aggregate(
        self, assignments: Dict[str, Tuple[int, AxisScale]]
    ) -> Dict[int, pb.FlightFfbAction]:
        """Collapse active effects into one FlightFfbAction per assigned function.

        assignments: {'x': (function_id, AxisScale), 'y': (function_id, AxisScale)}
        Single-axis roles pass only {'x': ...}.
        """
        out: Dict[int, pb.FlightFfbAction] = {}
        for axis_key, (function_id, scale) in assignments.items():
            block = X_BLOCK if axis_key == "x" else Y_BLOCK
            out[function_id] = self._aggregate_axis(axis_key, block, scale)
        return out

    def _aggregate_axis(self, axis_key: str, block: int, scale: AxisScale) -> pb.FlightFfbAction:
        k_spring = k_damper = k_friction = 0.0
        spring_cp_weighted = 0.0
        spring_coef_total = 0.0
        load_norm = 0.0

        for e in self._effects.values():
            if not e.started:
                continue
            if e.type in (EFFECT_SPRING, EFFECT_SPRING_ADJUSTER):
                c = e.conditions.get(block)
                if c:
                    k_spring += c.coef
                    spring_coef_total += abs(c.coef)
                    spring_cp_weighted += c.cp_offset * abs(c.coef)
            elif e.type == EFFECT_DAMPER:
                c = e.conditions.get(block)
                if c:
                    k_damper += c.coef
            elif e.type == EFFECT_FRICTION:
                c = e.conditions.get(block)
                if c:
                    k_friction += c.coef
            elif e.type == EFFECT_CONSTANT:
                load_norm += e.magnitude * self._axis_component(axis_key, e.direction_deg)
            elif e.type == EFFECT_INERTIA or e.type in PERIODIC_TYPES:
                # TODO plan 26 §6 / Phase 6: inertia has no FlightFfbAction term;
                # periodic -> DDS mapping is deferred. Log once per type.
                if e.type not in self._unsupported_seen:
                    self._unsupported_seen.add(e.type)
                    log.info("effect type %d not yet mapped (inertia/periodic) — ignored", e.type)

        # coefficient-weighted spring center (plan 26 §6 "combining springs")
        trim_norm = (spring_cp_weighted / spring_coef_total) if spring_coef_total > 0 else 0.0

        g = self.master_gain
        act = pb.FlightFfbAction()
        act.k_spring = k_spring * g
        act.k_damper = k_damper * g
        act.k_friction = k_friction * g
        act.trim_offset = trim_norm * scale.mm_half_range
        act.load_force = load_norm * g * scale.max_force_n
        return act

    @staticmethod
    def _axis_component(axis_key: str, direction_deg: float) -> float:
        """Resolve a constant-force direction into this axis's component.

        Convention (verify against servo direction on bring-up): 0deg = +X
        (roll right), 90deg = +Y (pitch nose-up). Single-axis roles use 'x'.
        """
        rad = math.radians(direction_deg)
        return math.cos(rad) if axis_key == "x" else math.sin(rad)
