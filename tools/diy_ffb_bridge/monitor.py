"""Broker client that decodes and prints the uplink Message stream.

Doubles as (a) the first hardware smoke test — run the broker against a real
gateway, then run this to confirm decoded traffic — and (b) the reference
skeleton for the TelemFFB ``DiyFfbDevice`` client (connect, length-prefix
framing, parse ``Message``).

Usage:
    python broker.py --port COM7            # in one terminal
    python monitor.py                       # in another; prints uplink
    python monitor.py --request-info        # also asks the gateway for DeviceInfo
"""

from __future__ import annotations

import argparse
import socket
import sys
import time

import diy_ffb_protocol_pb2 as pb
from broker import DEFAULT_LISTEN, frame_payload, recv_payload


def connect(host, port) -> socket.socket:
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.connect((host, port))
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    return s


def send(sock: socket.socket, msg: pb.Message):
    sock.sendall(frame_payload(msg.SerializeToString()))


def describe(msg: pb.Message) -> str:
    which = msg.WhichOneof("payload")
    if which == "axis_state":
        a = msg.axis_state
        return f"axis_state  axis={a.axis_id}  pos={a.position:+.4f}  force={a.force:+.3f}"
    if which == "gateway_state":
        g = msg.gateway_state
        return (f"gateway_state  gw={g.gateway_id}  rssi={g.rssi}  "
                f"axes_present=0b{g.axes_present:08b}")
    if which == "device_info":
        d = msg.device_info
        return (f"device_info  fw={d.fw_version}  board={d.board}  "
                f"git={d.git_hash}  uid={d.device_uid}")
    if which == "active_function":
        f = msg.active_function
        return f"active_function  axis={f.axis_id} -> function={f.function_id}"
    if which in ("axis_log_message", "gateway_log_message"):
        return f"{which}: {getattr(msg, which).message!r}"
    return which or "<empty>"


def main(argv=None):
    ap = argparse.ArgumentParser(description="DIY FFB broker uplink monitor")
    ap.add_argument("--host", default=DEFAULT_LISTEN[0])
    ap.add_argument("--port", type=int, default=DEFAULT_LISTEN[1])
    ap.add_argument("--request-info", action="store_true",
                    help="send DeviceInfoRequest for each gateway/axis on start")
    ap.add_argument("--filter", default="",
                    help="only print payload types containing this substring")
    args = ap.parse_args(argv)

    sock = connect(args.host, args.port)
    print(f"connected to broker {args.host}:{args.port}", file=sys.stderr)

    if args.request_info:
        req = pb.Message()
        req.device_info_request.gateway_id = pb.GATEWAY_ID_1
        send(sock, req)
        print("sent DeviceInfoRequest(gateway_id=1)", file=sys.stderr)

    count = 0
    last = time.monotonic()
    rate = 0
    while True:
        payload = recv_payload(sock)
        if payload is None:
            print("broker closed connection", file=sys.stderr)
            break
        msg = pb.Message()
        try:
            msg.ParseFromString(payload)
        except Exception as e:  # noqa: BLE001
            print(f"parse error ({len(payload)} B): {e}", file=sys.stderr)
            continue
        count += 1
        line = describe(msg)
        if args.filter and args.filter not in line:
            continue
        rate += 1
        now = time.monotonic()
        if now - last >= 1.0:
            print(f"[{rate:5d}/s] {line}")
            last, rate = now, 0
        elif args.filter:
            print(line)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
