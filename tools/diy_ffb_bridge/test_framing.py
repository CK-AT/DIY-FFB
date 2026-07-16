"""Offline correctness tests for the wire framing. Run: python test_framing.py

No hardware required. Verifies CRC against a known vector, COBS against the
canonical RFC examples, and full frame + StreamDecoder round-trips.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from framing import (  # noqa: E402
    StreamDecoder,
    cobs_decode,
    cobs_encode,
    crc16_modbus,
    decode_frame,
    encode_frame,
)


def test_crc16_modbus_known_vector():
    # Canonical CRC16/MODBUS check value.
    assert crc16_modbus(b"123456789") == 0x4B37


def test_cobs_rfc_examples():
    # From the COBS paper (encoding, without trailing delimiter).
    cases = [
        (b"\x00", b"\x01\x01"),
        (b"\x00\x00", b"\x01\x01\x01"),
        (b"\x11\x22\x00\x33", b"\x03\x11\x22\x02\x33"),
        (b"\x11\x22\x33\x44", b"\x05\x11\x22\x33\x44"),
    ]
    for raw, enc in cases:
        assert cobs_encode(raw) == enc, (raw, cobs_encode(raw), enc)
        assert cobs_decode(enc) == raw


def test_cobs_roundtrip_including_long_runs():
    for data in [b"", b"\x00" * 300, bytes(range(256)) * 3, os.urandom(1000)]:
        enc = cobs_encode(data)
        assert 0x00 not in enc, "encoded block must contain no zero byte"
        assert cobs_decode(enc) == data


def test_frame_roundtrip():
    for payload in [b"", b"hello", os.urandom(500)]:
        frame = encode_frame(payload)
        assert frame[-1] == 0x00
        assert decode_frame(frame[:-1]) == payload


def test_crc_mismatch_detected():
    frame = bytearray(encode_frame(b"payload"))
    # Corrupt a middle byte of the COBS block (not the delimiter).
    frame[2] ^= 0xFF
    try:
        decode_frame(bytes(frame[:-1]))
    except ValueError:
        return
    raise AssertionError("expected CRC/COBS error on corrupted frame")


def test_stream_decoder_multi_frame_and_resync():
    payloads = [b"one", b"two", os.urandom(200), b"", b"four"]
    stream = b"\x00\x00"  # leading sync zeros the firmware emits
    for p in payloads:
        stream += encode_frame(p)
    # Feed in awkward chunks to exercise buffering across boundaries.
    dec = StreamDecoder()
    got = []
    for i in range(0, len(stream), 7):
        got.extend(dec.feed(stream[i : i + 7]))
    # Empty-payload frame decodes to b"" and is a valid frame here.
    assert got == payloads, (got, payloads)
    assert dec.error_count == 0


def test_stream_decoder_skips_garbage_frame():
    dec = StreamDecoder()
    good = encode_frame(b"good")
    garbage = b"\x05\xde\xad\xbe\xef\x00"  # valid COBS, bad CRC
    out = list(dec.feed(garbage + good))
    assert out == [b"good"]
    assert dec.error_count == 1


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
