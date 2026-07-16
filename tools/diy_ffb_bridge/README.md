# DIY FFB serial broker

A small, protocol-agnostic process that **owns the gateway COM port** and
multiplexes it across multiple local clients. Built to let several
[VPforce-TelemFFB](https://github.com/walmis/VPforce-TelemFFB) device instances
(cyclic / collective / pedals — each a separate OS process) share our single
serial link, which is otherwise single-open.

See [`docs/plans/26_telemffb_python_backend.md`](../../docs/plans/26_telemffb_python_backend.md)
§5.1 for the rationale.

## What it does

```
 TelemFFB "joystick" instance ─┐                        ┌─ FFB_STICK_ROLL
 TelemFFB "collective" inst. ──┤   loopback TCP         │  FFB_STICK_PITCH
 TelemFFB "pedals" instance ───┼──► broker ──COBS/CRC──►│  (gateway dispatches
 (sniffer / monitor) ──────────┘   (owns COM port)   3M │   by function_id)
```

- **Downstream (client → port):** no demux — every `FFBAction` already carries
  its `function_id`, so the broker just write-arbitrates each client's payload
  onto the port (frames never interleave).
- **Upstream (port → clients):** every decoded `Message` is broadcast to all
  clients; each client filters the `AxisState` it owns. Simplest correct form
  of the upstream routing.

The broker only owns the **transport framing** (CRC16/Modbus + COBS, matching
the SimHub plugin and ESP32 firmware). It never parses protobuf — it relays
opaque `Message` payload bytes. Local IPC is a 4-byte LE length prefix + payload
over loopback TCP (reliable, so no CRC/COBS there).

## Files

| File | Purpose |
|---|---|
| `framing.py` | COBS + CRC16/Modbus codec; `StreamDecoder`. The wire core. |
| `broker.py` | The broker (port owner + TCP fanout). Also the CLI. |
| `client.py` | Broker-client foundation: `BrokerClient`, `Topology` (discovery), `AxisStateTracker` (per-function position/force), `DiyFfbLink`. What the TelemFFB `DiyFfbDevice` builds on. |
| `monitor.py` | Client that decodes/prints the uplink; first hardware smoke test. |
| `diy_ffb_protocol_pb2.py` | Generated from `proto/diy_ffb_protocol.proto`. Regenerate with `./regen_proto.sh`. |
| `test_framing.py`, `test_broker.py`, `test_client.py` | Offline tests (no hardware). |

## Requirements

Python 3.11+, `pyserial` (broker runtime), `protobuf` (monitor/clients).
`framing.py`/`broker.py` core logic has no third-party deps.

## Run

```bash
# 1. Ensure SimHub is NOT holding the gateway COM port (single-open).
# 2. Start the broker on the gateway port:
python broker.py --port COM7 -v

# 3. In another terminal, watch decoded uplink traffic:
python monitor.py                 # rate-summarised
python monitor.py --filter axis_state    # every AxisState line
python monitor.py --request-info  # also ask the gateway for DeviceInfo
```

## Test (offline)

```bash
python test_framing.py && python test_broker.py
```

## Status

Phase 1 of plan 26: transport + broker complete and tested offline. Next: the
TelemFFB `DiyFfbDevice` client (effect aggregation → per-function `FlightFfbAction`).
