"""Tests for the effect aggregator. Run: python test_aggregator.py (no hardware).

Covers the LVAR-API critical path: spring/damper/friction coefficients,
spring-center (trim), constant-force load, master gain, and the cyclic X->roll /
Y->pitch fan-out.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import diy_ffb_protocol_pb2 as pb  # noqa: E402
from aggregator import (  # noqa: E402
    EFFECT_CONSTANT,
    EFFECT_DAMPER,
    EFFECT_FRICTION,
    EFFECT_SPRING,
    X_BLOCK,
    Y_BLOCK,
    AxisScale,
    EffectAggregator,
)

ROLL = pb.FUNCTION_ID_FLIGHT_STICK_ROLL
PITCH = pb.FUNCTION_ID_FLIGHT_STICK_PITCH
PEDALS = pb.FUNCTION_ID_FLIGHT_PEDALS
UNIT = AxisScale(mm_half_range=10.0, max_force_n=50.0)


def test_single_axis_spring_damper_friction():
    ag = EffectAggregator()
    ag.set_condition(1, EFFECT_SPRING, X_BLOCK, coef=0.5, cp_offset=0.2)
    ag.set_condition(2, EFFECT_DAMPER, X_BLOCK, coef=0.3, cp_offset=0.0)
    ag.set_condition(3, EFFECT_FRICTION, X_BLOCK, coef=0.1, cp_offset=0.0)
    for i in (1, 2, 3):
        ag.start(i)
    act = ag.aggregate({"x": (PEDALS, UNIT)})[PEDALS]
    assert abs(act.k_spring - 0.5) < 1e-6
    assert abs(act.k_damper - 0.3) < 1e-6
    assert abs(act.k_friction - 0.1) < 1e-6
    assert abs(act.trim_offset - 0.2 * 10.0) < 1e-4   # cp_offset * mm_half_range


def test_stopped_effects_excluded():
    ag = EffectAggregator()
    ag.set_condition(1, EFFECT_SPRING, X_BLOCK, coef=0.7, cp_offset=0.0)
    # not started
    act = ag.aggregate({"x": (PEDALS, UNIT)})[PEDALS]
    assert act.k_spring == 0.0
    ag.start(1)
    assert abs(ag.aggregate({"x": (PEDALS, UNIT)})[PEDALS].k_spring - 0.7) < 1e-6
    ag.stop(1)
    assert ag.aggregate({"x": (PEDALS, UNIT)})[PEDALS].k_spring == 0.0


def test_coefficient_weighted_spring_center():
    # dominant spring at +0.4 (coef 0.8), small adjuster at -0.4 (coef 0.2)
    ag = EffectAggregator()
    ag.set_condition(1, EFFECT_SPRING, X_BLOCK, coef=0.8, cp_offset=0.4)
    ag.set_condition(2, EFFECT_SPRING, X_BLOCK, coef=0.2, cp_offset=-0.4)
    ag.start(1); ag.start(2)
    act = ag.aggregate({"x": (PEDALS, UNIT)})[PEDALS]
    # weighted center = (0.4*0.8 + -0.4*0.2)/(0.8+0.2) = 0.24
    assert abs(act.trim_offset - 0.24 * 10.0) < 1e-4
    assert abs(act.k_spring - 1.0) < 1e-6


def test_master_gain_scales_forces():
    ag = EffectAggregator()
    ag.set_condition(1, EFFECT_SPRING, X_BLOCK, coef=0.6, cp_offset=0.0)
    ag.start(1)
    ag.master_gain = 0.5
    act = ag.aggregate({"x": (PEDALS, UNIT)})[PEDALS]
    assert abs(act.k_spring - 0.3) < 1e-6


def test_cyclic_fanout_x_roll_y_pitch():
    ag = EffectAggregator()
    # one TelemFFB spring effect with both axis blocks (cyclic)
    ag.set_condition(1, EFFECT_SPRING, X_BLOCK, coef=0.5, cp_offset=0.1)   # roll
    ag.set_condition(1, EFFECT_SPRING, Y_BLOCK, coef=0.9, cp_offset=-0.2)  # pitch
    ag.start(1)
    out = ag.aggregate({"x": (ROLL, UNIT), "y": (PITCH, UNIT)})
    assert set(out) == {ROLL, PITCH}
    assert abs(out[ROLL].k_spring - 0.5) < 1e-6
    assert abs(out[ROLL].trim_offset - 0.1 * 10.0) < 1e-4
    assert abs(out[PITCH].k_spring - 0.9) < 1e-6
    assert abs(out[PITCH].trim_offset - (-0.2) * 10.0) < 1e-4


def test_constant_force_direction_decomposition():
    ag = EffectAggregator()
    # magnitude 0.8 at 0deg -> full +X (roll), zero Y
    ag.set_constant(1, magnitude=0.8, direction_deg=0.0)
    ag.start(1)
    out = ag.aggregate({"x": (ROLL, UNIT), "y": (PITCH, UNIT)})
    assert abs(out[ROLL].load_force - 0.8 * 50.0) < 1e-3
    assert abs(out[PITCH].load_force) < 1e-3
    # 90deg -> full +Y (pitch)
    ag.set_constant(1, magnitude=0.8, direction_deg=90.0)
    out = ag.aggregate({"x": (ROLL, UNIT), "y": (PITCH, UNIT)})
    assert abs(out[ROLL].load_force) < 1e-3
    assert abs(out[PITCH].load_force - 0.8 * 50.0) < 1e-3


def test_remove_and_clear():
    ag = EffectAggregator()
    ag.set_condition(1, EFFECT_SPRING, X_BLOCK, coef=0.5, cp_offset=0.0)
    ag.start(1)
    ag.remove(1)
    assert ag.aggregate({"x": (PEDALS, UNIT)})[PEDALS].k_spring == 0.0


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"PASS  {t.__name__}")
        except Exception as e:  # noqa: BLE001
            failed += 1
            print(f"FAIL  {t.__name__}: {e!r}")
    print(f"\n{len(tests) - failed}/{len(tests)} passed")
    sys.exit(1 if failed else 0)
