"""Broker-client foundation for the TelemFFB DIY backend.

Repo-side, transport-and-topology layer that the TelemFFB ``DiyFfbDevice`` glue
builds on. Three concerns, kept separate so the pure logic is unit-testable
without a socket:

* :class:`Topology` — the axis<->function map learned via discovery
  (``GatewayState.axes_present`` + per-axis ``return_function_config``), plus the
  linked-axis combination rules that MUST match the SimHub plugin
  (``DiyFfbPlugin.cs``: position = primary axis, force = signed sum with
  subtractive axes negated — see plugin commit 96ce7abf).
* :class:`AxisStateTracker` — caches per-physical-axis position/force and
  answers per-function position/force using :class:`Topology`.
* :class:`BrokerClient` — connects to the broker, dispatches decoded ``Message``
  payloads to handlers, and sends ``Message`` frames.

:class:`DiyFfbLink` wires them together and runs discovery.
"""

from __future__ import annotations

import logging
import socket
import threading
from typing import Callable, Dict, List, Optional, Set

import diy_ffb_protocol_pb2 as pb
from broker import DEFAULT_LISTEN, frame_payload, recv_payload

log = logging.getLogger("diy_ffb_client")

AXIS_MASK = pb.AXIS_ID_MASK          # 15
AXIS_SUBTRACTIVE = pb.AXIS_SUBTRACTIVE  # 128


def physical(axis: int) -> int:
    """Strip direction flags -> physical axis id (the state-cache key)."""
    return axis & AXIS_MASK


def is_subtractive(axis: int) -> bool:
    return (axis & AXIS_SUBTRACTIVE) != 0


class Topology:
    """axis<->function maps built from discovery replies."""

    def __init__(self) -> None:
        # function_id -> raw linked_axes entries (flags preserved), primary first
        self._linked: Dict[int, List[int]] = {}
        self.present_axes: Set[int] = set()

    # --- discovery inputs --------------------------------------------------
    def note_gateway_state(self, gs: pb.GatewayState) -> Set[int]:
        """Record which physical axes are live from the axes_present bitmask
        (bit 0 == axis 1). Returns the set of present physical axis ids."""
        present = {i + 1 for i in range(8) if gs.axes_present & (1 << i)}
        self.present_axes = present
        return present

    def note_function_config(self, fc: pb.FunctionConfig) -> None:
        fn = fc.base.function_id
        linked = [a for a in fc.base.linked_axes if physical(a) != pb.AXIS_UNDEFINED]
        if fn != pb.FUNCTION_ID_UNDEFINED and linked:
            self._linked[fn] = linked

    # --- queries -----------------------------------------------------------
    def functions(self) -> List[int]:
        return list(self._linked.keys())

    def linked_axes(self, function_id: int) -> List[int]:
        return self._linked.get(function_id, [])

    def primary_axis(self, function_id: int) -> Optional[int]:
        linked = self._linked.get(function_id)
        return physical(linked[0]) if linked else None

    def function_of_axis(self, phys_axis: int) -> Optional[int]:
        for fn, linked in self._linked.items():
            if any(physical(a) == phys_axis for a in linked):
                return fn
        return None


class AxisStateTracker:
    """Per-physical-axis position/force cache + per-function combination.

    Combination rules mirror ``DiyFfbPlugin.cs`` exactly:
      * position -> primary linked axis only (mirrored, not summed);
      * force    -> signed sum over linked axes, subtractive negated;
      * per-side force -> unsigned sum of one side (subtractive == left).
    """

    def __init__(self, topo: Topology) -> None:
        self._topo = topo
        self._pos: Dict[int, float] = {}
        self._force: Dict[int, float] = {}

    def update(self, axis_state: pb.AxisState) -> None:
        key = physical(axis_state.axis_id)
        self._pos[key] = axis_state.position
        self._force[key] = axis_state.force

    def axis_position(self, phys_axis: int) -> float:
        return self._pos.get(phys_axis, 0.0)

    def axis_force(self, phys_axis: int) -> float:
        return self._force.get(phys_axis, 0.0)

    def function_position(self, function_id: int) -> float:
        primary = self._topo.primary_axis(function_id)
        return self._pos.get(primary, 0.0) if primary is not None else 0.0

    def function_force(self, function_id: int) -> float:
        """Net directional force: signed sum, subtractive axes negated."""
        total = 0.0
        for a in self._topo.linked_axes(function_id):
            f = self._force.get(physical(a), 0.0)
            total += -f if is_subtractive(a) else f
        return total

    def pedal_force(self, function_id: int, left: bool) -> float:
        """Unsigned sum of one side's linked axes (subtractive == left)."""
        return sum(
            self._force.get(physical(a), 0.0)
            for a in self._topo.linked_axes(function_id)
            if is_subtractive(a) == left
        )


class BrokerClient:
    """Loopback-TCP connection to the broker. Background reader dispatches
    decoded ``Message`` payloads to per-payload-type handlers."""

    def __init__(self, host: str = DEFAULT_LISTEN[0], port: int = DEFAULT_LISTEN[1]):
        self._addr = (host, port)
        self._sock: Optional[socket.socket] = None
        self._handlers: Dict[str, List[Callable[[pb.Message], None]]] = {}
        self._stop = threading.Event()
        self._reader: Optional[threading.Thread] = None
        self._send_lock = threading.Lock()

    def on(self, payload_type: str, cb: Callable[[pb.Message], None]) -> None:
        """Register a handler for a Message oneof field name, e.g. 'axis_state'."""
        self._handlers.setdefault(payload_type, []).append(cb)

    def connect(self) -> None:
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.connect(self._addr)
        s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self._sock = s
        self._reader = threading.Thread(target=self._read_loop, name="broker-rx",
                                        daemon=True)
        self._reader.start()
        log.info("connected to broker %s:%d", *self._addr)

    def send(self, msg: pb.Message) -> None:
        data = frame_payload(msg.SerializeToString())
        with self._send_lock:
            if self._sock:
                self._sock.sendall(data)

    def close(self) -> None:
        self._stop.set()
        if self._sock:
            try:
                self._sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self._sock.close()
        if self._reader:
            self._reader.join(timeout=1.0)

    def _read_loop(self) -> None:
        while not self._stop.is_set():
            try:
                payload = recv_payload(self._sock)
            except OSError:
                break
            if payload is None:
                break
            msg = pb.Message()
            try:
                msg.ParseFromString(payload)
            except Exception:  # noqa: BLE001
                continue
            which = msg.WhichOneof("payload")
            for cb in self._handlers.get(which, ()):
                try:
                    cb(msg)
                except Exception as e:  # noqa: BLE001
                    log.warning("handler for %s raised: %s", which, e)


class DiyFfbLink:
    """High-level orchestration: connection + discovery + state tracking.

    Discovery is passive-plus-probe: watch ``GatewayState`` for the live axes,
    then probe each with ``return_function_config`` and absorb the replies. Call
    :meth:`request_discovery` after connecting; topology fills in asynchronously
    as replies arrive.
    """

    def __init__(self, client: Optional[BrokerClient] = None):
        self.client = client or BrokerClient()
        self.topo = Topology()
        self.state = AxisStateTracker(self.topo)
        self.client.on("gateway_state", self._on_gateway_state)
        self.client.on("function_config", self._on_function_config)
        self.client.on("axis_state", self._on_axis_state)

    def start(self) -> None:
        self.client.connect()

    def request_discovery(self) -> None:
        """Ask each present axis for its FunctionConfig (topology reply)."""
        for axis in sorted(self.topo.present_axes):
            msg = pb.Message()
            msg.axis_action.axis_id = axis
            msg.axis_action.return_function_config = True
            self.client.send(msg)

    def send_flight_ffb(self, function_id: int, action: pb.FlightFfbAction) -> None:
        msg = pb.Message()
        msg.ffb_action.function_id = function_id
        msg.ffb_action.flight_ffb.CopyFrom(action)
        self.client.send(msg)

    # --- handlers ----------------------------------------------------------
    def _on_gateway_state(self, msg: pb.Message) -> None:
        self.topo.note_gateway_state(msg.gateway_state)

    def _on_function_config(self, msg: pb.Message) -> None:
        self.topo.note_function_config(msg.function_config)

    def _on_axis_state(self, msg: pb.Message) -> None:
        self.state.update(msg.axis_state)
