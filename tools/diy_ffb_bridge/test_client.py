"""Tests for the broker-client foundation. Run: python test_client.py

Covers the pure combination logic (topology + axis-state tracker, matching the
SimHub plugin rules incl. subtractive negation) offline, and one discovery +
FFB round-trip through a real broker with a fake serial.
"""

import os
import socket
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import diy_ffb_protocol_pb2 as pb  # noqa: E402
from broker import SerialBridge  # noqa: E402
from client import AxisStateTracker, DiyFfbLink, Topology  # noqa: E402
from framing import StreamDecoder, encode_frame  # noqa: E402
from test_broker import FakeSerial  # noqa: E402

CYCLIC_ROLL = pb.FUNCTION_ID_FLIGHT_STICK_ROLL
CYCLIC_PITCH = pb.FUNCTION_ID_FLIGHT_STICK_PITCH
PEDALS = pb.FUNCTION_ID_FLIGHT_PEDALS


def _function_config(function_id, linked_axes):
    fc = pb.FunctionConfig()
    fc.base.function_id = function_id
    fc.base.linked_axes.extend(linked_axes)
    return fc


# --- topology ---------------------------------------------------------------
def test_gateway_state_present_axes():
    topo = Topology()
    gs = pb.GatewayState(axes_present=0b00000101)  # axes 1 and 3
    assert topo.note_gateway_state(gs) == {1, 3}


def test_topology_axis_function_maps():
    topo = Topology()
    topo.note_function_config(_function_config(CYCLIC_ROLL, [pb.AXIS_ID_1]))
    topo.note_function_config(_function_config(CYCLIC_PITCH, [pb.AXIS_ID_2]))
    assert topo.primary_axis(CYCLIC_ROLL) == 1
    assert topo.primary_axis(CYCLIC_PITCH) == 2
    assert topo.function_of_axis(1) == CYCLIC_ROLL
    assert topo.function_of_axis(2) == CYCLIC_PITCH
    assert topo.function_of_axis(5) is None


def test_topology_captures_flight_control_range():
    topo = Topology()
    fc = _function_config(CYCLIC_PITCH, [pb.AXIS_ID_2])
    fc.flight_control.pos_min = -40
    fc.flight_control.pos_max = 40
    topo.note_function_config(fc)
    assert topo.pos_range(CYCLIC_PITCH) == (-40, 40)
    # a function with no flight_control config -> None
    topo.note_function_config(_function_config(CYCLIC_ROLL, [pb.AXIS_ID_1]))
    assert topo.pos_range(CYCLIC_ROLL) is None


# --- single-axis (cyclic) combination ---------------------------------------
def test_single_axis_position_and_force():
    topo = Topology()
    topo.note_function_config(_function_config(CYCLIC_ROLL, [pb.AXIS_ID_1]))
    st = AxisStateTracker(topo)
    st.update(pb.AxisState(axis_id=pb.AXIS_ID_1, position=0.42, force=3.5))
    # position/force are protobuf float32 -> use float32-appropriate tolerance.
    assert abs(st.function_position(CYCLIC_ROLL) - 0.42) < 1e-6
    assert abs(st.function_force(CYCLIC_ROLL) - 3.5) < 1e-6


# --- differential pedals: the subtractive-negation case (plugin 96ce7abf) ---
def test_differential_force_negates_subtractive():
    # right pedal = axis 3 additive, left pedal = axis 4 subtractive.
    topo = Topology()
    left = pb.AXIS_ID_4 | pb.AXIS_SUBTRACTIVE
    topo.note_function_config(_function_config(PEDALS, [pb.AXIS_ID_3, left]))
    st = AxisStateTracker(topo)
    st.update(pb.AxisState(axis_id=pb.AXIS_ID_3, position=0.10, force=5.0))   # right
    st.update(pb.AxisState(axis_id=pb.AXIS_ID_4, position=0.10, force=2.0))   # left

    # NET force = right - left (subtractive negated), NOT right + left.
    assert abs(st.function_force(PEDALS) - (5.0 - 2.0)) < 1e-6
    # per-side helper stays unsigned magnitude:
    assert abs(st.pedal_force(PEDALS, left=True) - 2.0) < 1e-6
    assert abs(st.pedal_force(PEDALS, left=False) - 5.0) < 1e-6
    # position uses the primary axis only (mirrored), not a sum:
    assert abs(st.function_position(PEDALS) - 0.10) < 1e-6


def test_unknown_function_and_axis_default_zero():
    st = AxisStateTracker(Topology())
    assert st.function_force(CYCLIC_ROLL) == 0.0
    assert st.function_position(CYCLIC_ROLL) == 0.0
    assert st.axis_force(7) == 0.0


# --- integration: discovery + FFB round-trip via a real broker --------------
def test_link_discovery_and_ffb_roundtrip():
    ser = FakeSerial()
    bridge = SerialBridge(ser, ("127.0.0.1", 0))
    bridge.start()
    addr = bridge._srv.getsockname()
    try:
        link = DiyFfbLink()
        link.client._addr = addr
        link.start()
        time.sleep(0.1)

        # Gateway announces axes 1 & 2 (a cyclic).
        gs = pb.Message()
        gs.gateway_state.gateway_id = pb.GATEWAY_ID_1
        gs.gateway_state.axes_present = 0b11
        ser.inject(encode_frame(gs.SerializeToString()))
        time.sleep(0.1)
        assert link.topo.present_axes == {1, 2}

        # Discovery probe -> broker forwarded two AxisActions onto the wire.
        link.request_discovery()
        time.sleep(0.1)
        dec = StreamDecoder()
        probes = [pb.Message.FromString(p) for p in dec.feed(bytes(ser.written))]
        probed_axes = {m.axis_action.axis_id for m in probes
                       if m.WhichOneof("payload") == "axis_action"
                       and m.axis_action.return_function_config}
        assert probed_axes == {1, 2}

        # Gateway replies with the two FunctionConfigs -> topology fills in.
        for fn, ax in [(CYCLIC_ROLL, pb.AXIS_ID_1), (CYCLIC_PITCH, pb.AXIS_ID_2)]:
            m = pb.Message()
            m.function_config.CopyFrom(_function_config(fn, [ax]))
            ser.inject(encode_frame(m.SerializeToString()))
        time.sleep(0.1)
        assert link.topo.primary_axis(CYCLIC_ROLL) == 1
        assert link.topo.primary_axis(CYCLIC_PITCH) == 2

        # AxisState uplink is tracked and attributed to the right function.
        axs = pb.Message()
        axs.axis_state.axis_id = pb.AXIS_ID_2
        axs.axis_state.position = -0.3
        axs.axis_state.force = 1.75
        ser.inject(encode_frame(axs.SerializeToString()))
        time.sleep(0.1)
        assert abs(link.state.function_force(CYCLIC_PITCH) - 1.75) < 1e-6
        assert abs(link.state.function_position(CYCLIC_PITCH) - (-0.3)) < 1e-6

        # Downlink: a FlightFfbAction is framed onto the wire, tagged by function.
        prev = len(ser.written)
        act = pb.FlightFfbAction(k_spring=0.5, k_damper=0.2, trim_offset=1.5)
        link.send_flight_ffb(CYCLIC_ROLL, act)
        time.sleep(0.1)
        dec2 = StreamDecoder()
        sent = [pb.Message.FromString(p) for p in dec2.feed(bytes(ser.written[prev:]))]
        ffb = [m for m in sent if m.WhichOneof("payload") == "ffb_action"]
        assert len(ffb) == 1
        assert ffb[0].ffb_action.function_id == CYCLIC_ROLL
        assert abs(ffb[0].ffb_action.flight_ffb.k_spring - 0.5) < 1e-6

        link.client.close()
    finally:
        bridge.stop()


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
