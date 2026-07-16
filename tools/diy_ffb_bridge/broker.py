"""DIY FFB serial broker.

Owns the single gateway COM port and multiplexes it across N local clients
(TelemFFB device instances). It is intentionally protocol-agnostic: it moves
opaque protobuf ``Message`` payloads and owns only the transport framing
(CRC16/Modbus + COBS, see :mod:`framing`).

  * Downstream (client -> port): no demux needed — every ``FFBAction`` already
    carries its ``function_id`` and the gateway dispatches on it. The broker
    just write-arbitrates: it serializes each client's payload onto the port
    under a lock, so frames never interleave.
  * Upstream (port -> clients): broadcast every decoded payload to all clients.
    Each client (``DiyFfbDevice``) filters the ``AxisState`` it owns by
    axis/function. Broadcast + client-side filter is the simplest correct form
    of the upstream routing.

Local IPC (loopback TCP) framing is a plain 4-byte little-endian length prefix
followed by the raw protobuf ``Message`` bytes — TCP is reliable, so no
CRC/COBS there.
"""

from __future__ import annotations

import logging
import socket
import struct
import threading
import time
from typing import Optional, Protocol

from framing import StreamDecoder, encode_frame

LEN = struct.Struct("<I")
MAX_PAYLOAD = 64 * 1024
DEFAULT_LISTEN = ("127.0.0.1", 45111)
SERIAL_BAUD = 3_000_000

log = logging.getLogger("ffb_broker")


class SerialLike(Protocol):
    def read(self, size: int) -> bytes: ...
    def write(self, data: bytes) -> int: ...
    def close(self) -> None: ...


def recv_exact(sock: socket.socket, n: int) -> Optional[bytes]:
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)


def recv_payload(sock: socket.socket) -> Optional[bytes]:
    hdr = recv_exact(sock, LEN.size)
    if hdr is None:
        return None
    (length,) = LEN.unpack(hdr)
    if length > MAX_PAYLOAD:
        raise ValueError(f"client payload too large: {length}")
    return recv_exact(sock, length) if length else b""


def frame_payload(payload: bytes) -> bytes:
    return LEN.pack(len(payload)) + payload


class SerialBridge:
    def __init__(self, ser: SerialLike, listen=DEFAULT_LISTEN):
        self._ser = ser
        self._listen = listen
        self._clients: set[socket.socket] = set()
        self._clients_lock = threading.Lock()
        self._write_lock = threading.Lock()
        self._stop = threading.Event()
        self._decoder = StreamDecoder()
        self._srv: Optional[socket.socket] = None
        self._threads: list[threading.Thread] = []

    # --- lifecycle ---------------------------------------------------------
    def start(self):
        self._srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._srv.bind(self._listen)
        self._srv.listen(8)
        self._srv.settimeout(0.5)
        self._spawn(self._serial_reader, "serial-reader")
        self._spawn(self._accept_loop, "accept-loop")
        log.info("broker listening on %s:%d", *self._listen)

    def stop(self):
        self._stop.set()
        if self._srv:
            self._srv.close()
        with self._clients_lock:
            for c in list(self._clients):
                c.close()
            self._clients.clear()
        try:
            self._ser.close()
        except Exception:
            pass
        for t in self._threads:
            t.join(timeout=1.0)

    def _spawn(self, target, name):
        t = threading.Thread(target=target, name=name, daemon=True)
        t.start()
        self._threads.append(t)

    # --- serial -> clients (broadcast) ------------------------------------
    def _serial_reader(self):
        while not self._stop.is_set():
            try:
                chunk = self._ser.read(4096)
            except Exception as e:  # noqa: BLE001
                if not self._stop.is_set():
                    log.error("serial read failed: %s", e)
                break
            if not chunk:
                continue
            for payload in self._decoder.feed(chunk):
                self._broadcast(payload)

    def _broadcast(self, payload: bytes):
        framed = frame_payload(payload)
        with self._clients_lock:
            dead = []
            for c in self._clients:
                try:
                    c.sendall(framed)
                except Exception:
                    dead.append(c)
            for c in dead:
                self._clients.discard(c)
                c.close()

    # --- clients -> serial (write-arbitrated) -----------------------------
    def _accept_loop(self):
        while not self._stop.is_set():
            try:
                conn, addr = self._srv.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            with self._clients_lock:
                self._clients.add(conn)
            log.info("client connected: %s (total %d)", addr, len(self._clients))
            self._spawn(lambda c=conn, a=addr: self._client_reader(c, a),
                        f"client-{addr[1]}")

    def _client_reader(self, conn: socket.socket, addr):
        try:
            while not self._stop.is_set():
                payload = recv_payload(conn)
                if payload is None:
                    break
                frame = encode_frame(payload)
                with self._write_lock:
                    self._ser.write(frame)
        except Exception as e:  # noqa: BLE001
            log.warning("client %s error: %s", addr, e)
        finally:
            with self._clients_lock:
                self._clients.discard(conn)
            conn.close()
            log.info("client disconnected: %s", addr)


def open_serial(port: str, baud: int = SERIAL_BAUD):
    import serial  # local import so tests without pyserial still run

    ser = serial.Serial(port, baud, timeout=0.05)
    ser.reset_input_buffer()
    ser.write(b"\x00\x00\x00")  # COBS sync, matches firmware SerialManager
    return ser


def main(argv=None):
    import argparse

    ap = argparse.ArgumentParser(description="DIY FFB serial broker")
    ap.add_argument("--port", required=True, help="gateway COM port, e.g. COM7")
    ap.add_argument("--baud", type=int, default=SERIAL_BAUD)
    ap.add_argument("--host", default=DEFAULT_LISTEN[0])
    ap.add_argument("--listen-port", type=int, default=DEFAULT_LISTEN[1])
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    ser = open_serial(args.port, args.baud)
    bridge = SerialBridge(ser, (args.host, args.listen_port))
    bridge.start()
    try:
        while True:
            time.sleep(1.0)
    except KeyboardInterrupt:
        log.info("shutting down")
    finally:
        bridge.stop()


if __name__ == "__main__":
    main()
