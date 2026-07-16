"""Integration tests for the broker using a fake serial + real localhost TCP.

Run: python test_broker.py   (no hardware needed)

Proves: (1) client payloads are CRC/COBS-framed correctly onto the port and
never interleave under concurrency; (2) port bytes are decoded and broadcast to
every connected client.
"""

import os
import queue
import socket
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from broker import SerialBridge, frame_payload, recv_payload  # noqa: E402
from framing import StreamDecoder, encode_frame  # noqa: E402


class FakeSerial:
    """Bidirectional in-memory serial. Host writes land in `written`;
    inject bytes toward the broker via `inject()`."""

    def __init__(self):
        self._rx = queue.Queue()  # bytes chunks heading to the broker
        self.written = bytearray()  # bytes the broker wrote "to the wire"
        self._wlock = threading.Lock()
        self._closed = False

    def inject(self, data: bytes):
        self._rx.put(data)

    def read(self, size: int) -> bytes:
        try:
            return self._rx.get(timeout=0.05)
        except queue.Empty:
            return b""

    def write(self, data: bytes) -> int:
        with self._wlock:
            self.written.extend(data)
        return len(data)

    def close(self):
        self._closed = True


def _connect(addr):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.connect(addr)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    return s


def _make_bridge():
    ser = FakeSerial()
    bridge = SerialBridge(ser, ("127.0.0.1", 0))
    # bind to an ephemeral port; start() uses the resolved sockname
    bridge.start()
    addr = bridge._srv.getsockname()
    return bridge, ser, addr


def test_downstream_framing_and_no_interleave():
    bridge, ser, addr = _make_bridge()
    try:
        payloads = [os.urandom(50 + i) for i in range(20)]
        clients = [_connect(addr) for _ in range(4)]
        time.sleep(0.1)
        # Fire from multiple clients concurrently to stress the write lock.
        def sender(c, ps):
            for p in ps:
                c.sendall(frame_payload(p))
        threads = [threading.Thread(target=sender, args=(clients[i % 4], [p]))
                   for i, p in enumerate(payloads)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        time.sleep(0.2)
        # Decode what landed on the "wire" and confirm every payload arrived intact.
        dec = StreamDecoder()
        got = list(dec.feed(bytes(ser.written)))
        assert dec.error_count == 0, "frame corruption / interleave detected"
        assert sorted(got) == sorted(payloads), (len(got), len(payloads))
        for c in clients:
            c.close()
    finally:
        bridge.stop()


def test_upstream_broadcast_to_all_clients():
    bridge, ser, addr = _make_bridge()
    try:
        clients = [_connect(addr) for _ in range(3)]
        time.sleep(0.1)
        payloads = [b"axisstate-1", os.urandom(120), b""]
        for p in payloads:
            ser.inject(encode_frame(p))
        time.sleep(0.2)
        for c in clients:
            for expected in payloads:
                got = recv_payload(c)
                assert got == expected, (got, expected)
            c.close()
    finally:
        bridge.stop()


def test_upstream_resync_across_read_chunks():
    bridge, ser, addr = _make_bridge()
    try:
        c = _connect(addr)
        time.sleep(0.1)
        stream = b"\x00" + encode_frame(b"first") + encode_frame(b"second")
        # Split mid-frame to exercise the decoder's cross-chunk buffering.
        ser.inject(stream[:5])
        ser.inject(stream[5:])
        time.sleep(0.2)
        assert recv_payload(c) == b"first"
        assert recv_payload(c) == b"second"
        c.close()
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
