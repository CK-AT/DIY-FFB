"""Wire framing for the DIY FFB serial protocol.

The on-wire frame (identical on both ends) is:

    payload = protobuf-serialized ``Message`` bytes
    body    = payload || CRC16/Modbus(payload)   # CRC is 2 bytes, little-endian
    frame   = COBS(body) || 0x00                 # 0x00 is the packet delimiter

This matches the SimHub plugin (``FrameSerial.cs``: CRC16/Modbus then
``COBS.Encode``) and the ESP32 firmware (``PacketSerial_<COBS, 0, ...>`` +
``FastCRC16.modbus``, delimiter byte 0x00).

CRC and COBS are transport-layer concerns: this module deals only in opaque
``payload`` bytes, so the broker can relay any protobuf ``Message`` without
parsing it.
"""

from __future__ import annotations

DELIMITER = 0x00


# --- CRC16/Modbus -----------------------------------------------------------
# poly 0x8005 reflected (0xA001), init 0xFFFF, refin/refout=true, xorout=0x0000.
# Check value for b"123456789" is 0x4B37.
def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for byte in data:
        crc ^= byte
        for _ in range(8):
            if crc & 1:
                crc = (crc >> 1) ^ 0xA001
            else:
                crc >>= 1
    return crc & 0xFFFF


# --- COBS -------------------------------------------------------------------
# Consistent Overhead Byte Stuffing. Encodes a block so it contains no 0x00,
# leaving 0x00 free as a frame delimiter. Does NOT append the delimiter.
def cobs_encode(data: bytes) -> bytes:
    out = bytearray()
    code_idx = 0
    out.append(0)  # placeholder for first code byte
    code = 1
    for byte in data:
        if byte != 0:
            out.append(byte)
            code += 1
            if code != 0xFF:
                continue
        # emit run: either we hit a zero, or the run reached 254 non-zero bytes
        out[code_idx] = code
        code_idx = len(out)
        out.append(0)  # placeholder for next code byte
        code = 1
    out[code_idx] = code
    return bytes(out)


def cobs_decode(data: bytes) -> bytes:
    out = bytearray()
    i = 0
    n = len(data)
    while i < n:
        code = data[i]
        if code == 0:
            raise ValueError("unexpected zero byte in COBS data")
        i += 1
        end = i + code - 1
        if end > n:
            raise ValueError("COBS code overruns buffer")
        out.extend(data[i:end])
        i = end
        if code != 0xFF and i < n:
            out.append(0)
    return bytes(out)


# --- Frame encode / decode --------------------------------------------------
def encode_frame(payload: bytes) -> bytes:
    """payload (protobuf Message bytes) -> full wire frame incl. delimiter."""
    body = payload + crc16_modbus(payload).to_bytes(2, "little")
    return cobs_encode(body) + bytes([DELIMITER])


def decode_frame(cobs_block: bytes) -> bytes:
    """A single COBS block (delimiter already stripped) -> payload bytes.

    Raises ValueError on COBS error, short frame, or CRC mismatch.
    """
    body = cobs_decode(cobs_block)
    if len(body) < 2:
        raise ValueError("frame shorter than CRC")
    payload, crc_rx = body[:-2], int.from_bytes(body[-2:], "little")
    if crc16_modbus(payload) != crc_rx:
        raise ValueError("CRC mismatch")
    return payload


class StreamDecoder:
    """Accumulates a byte stream and yields decoded payloads on each 0x00.

    Malformed frames (CRC/COBS errors) are counted and skipped, not raised, so
    a single bad frame never stalls the stream.
    """

    def __init__(self) -> None:
        self._buf = bytearray()
        self.error_count = 0

    def feed(self, chunk: bytes):
        """Feed received bytes; yields each valid payload (bytes)."""
        self._buf.extend(chunk)
        while True:
            idx = self._buf.find(DELIMITER)
            if idx < 0:
                return
            block = bytes(self._buf[:idx])
            del self._buf[: idx + 1]
            if not block:
                continue  # empty frame (e.g. leading sync zeros)
            try:
                yield decode_frame(block)
            except ValueError:
                self.error_count += 1
