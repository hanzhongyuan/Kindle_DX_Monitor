#!/usr/bin/env python3
"""Read one complete framebuffer from an unauthenticated loopback RFB server.

Uses the Python standard library only. It never sends keyboard, pointer, or
clipboard events. The default target is the Kindle's dedicated virtual screen.
"""

from __future__ import annotations

import argparse
import ipaddress
import json
from pathlib import Path
import socket
import struct
import sys
import time
import zlib


MAX_DIMENSION = 8192
MAX_PIXELS = 16_777_216
MAX_TEXT_BYTES = 65_536
MAX_RECTANGLES = 4096
MAX_MESSAGES = 1024
MAX_RECEIVED_BYTES = 128 * 1024 * 1024
RAW = 0
DESKTOP_SIZE = -223


class ProbeError(Exception):
    """The server did not provide a safe, compatible complete framebuffer."""


class Wire:
    def __init__(self, sock: socket.socket, deadline: float) -> None:
        self.sock = sock
        self.deadline = deadline
        self.received = 0

    def _remaining(self) -> float:
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise ProbeError("Timed out before receiving a complete framebuffer")
        return remaining

    def read(self, count: int) -> bytes:
        if count < 0 or count > MAX_RECEIVED_BYTES - self.received:
            raise ProbeError("Server exceeded the receive byte limit")
        data = bytearray(count)
        view = memoryview(data)
        offset = 0
        while offset < count:
            self.sock.settimeout(self._remaining())
            received = self.sock.recv_into(view[offset:])
            if not received:
                raise ProbeError("Server closed the connection before the frame was complete")
            offset += received
            self.received += received
        return bytes(data)

    def send(self, data: bytes) -> None:
        self.sock.settimeout(self._remaining())
        self.sock.sendall(data)

    def reason(self) -> str:
        length = struct.unpack("!I", self.read(4))[0]
        if length > MAX_TEXT_BYTES:
            raise ProbeError("Server failure message exceeds the text length limit")
        return self.read(length).decode("utf-8", errors="replace")


def loopback_address(value: str) -> str:
    if value.lower() == "localhost":
        return "127.0.0.1"
    try:
        address = ipaddress.ip_address(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("Use a numeric loopback address or localhost") from error
    if not address.is_loopback:
        raise argparse.ArgumentTypeError("Only loopback addresses are allowed")
    return str(address)


def validate_size(width: int, height: int, expected: tuple[int, int]) -> None:
    if not (1 <= width <= MAX_DIMENSION and 1 <= height <= MAX_DIMENSION):
        raise ProbeError(f"Framebuffer dimensions are out of bounds: {width}x{height}")
    if width * height > MAX_PIXELS:
        raise ProbeError("Framebuffer exceeds the maximum pixel count")
    if (width, height) != expected:
        raise ProbeError(
            f"Expected the dedicated {expected[0]}x{expected[1]} display, "
            f"but the server advertised {width}x{height}"
        )


def negotiate(wire: Wire) -> tuple[str, str, int, int]:
    offered = wire.read(12)
    versions = {b"RFB 003.003\n": 3, b"RFB 003.007\n": 7, b"RFB 003.008\n": 8}
    if offered not in versions:
        raise ProbeError(f"Unsupported RFB version banner: {offered!r}")
    minor = versions[offered]
    wire.send(offered)

    if minor == 3:
        security = struct.unpack("!I", wire.read(4))[0]
        if security == 0:
            raise ProbeError(f"Server rejected connection: {wire.reason()}")
        if security != 1:
            raise ProbeError(f"Server requires authentication (security type {security})")
    else:
        count = wire.read(1)[0]
        if count == 0:
            raise ProbeError(f"Server rejected connection: {wire.reason()}")
        security_types = wire.read(count)
        if 1 not in security_types:
            raise ProbeError(f"Server does not offer no-auth security: {list(security_types)}")
        wire.send(b"\x01")
        # RFB 3.7 omits SecurityResult for None; RFB 3.8 includes it.
        if minor == 8:
            result = struct.unpack("!I", wire.read(4))[0]
            if result != 0:
                raise ProbeError(f"Server security negotiation failed: {wire.reason()}")

    wire.send(b"\x01")  # ClientInit: shared session; do not evict other viewers.
    server_init = wire.read(24)
    width, height = struct.unpack_from("!HH", server_init)
    name_length = struct.unpack_from("!I", server_init, 20)[0]
    if name_length > MAX_TEXT_BYTES:
        raise ProbeError("Server desktop name exceeds the text length limit")
    name = wire.read(name_length).decode("utf-8", errors="replace")
    return offered.decode("ascii").strip(), name, width, height


def read_frame(wire: Wire, width: int, height: int,
               expected: tuple[int, int]) -> tuple[int, int, bytes, int]:
    validate_size(width, height, expected)
    # Little-endian RGB888 in 32 bits: bytes on the wire are B, G, R, padding.
    pixel_format = struct.pack("!BBBBHHHBBB3x", 32, 24, 0, 1, 255, 255, 255, 16, 8, 0)
    wire.send(b"\x00\x00\x00\x00" + pixel_format)
    wire.send(struct.pack("!BBHii", 2, 0, 2, RAW, DESKTOP_SIZE))

    pixels = bytearray(width * height * 4)
    coverage = bytearray(width * height)
    covered = 0
    rectangles_seen = 0

    def request_full_frame() -> None:
        wire.send(struct.pack("!BBHHHH", 3, 0, 0, 0, width, height))

    request_full_frame()
    for _ in range(MAX_MESSAGES):
        message_type = wire.read(1)[0]
        if message_type == 2:  # Bell: no payload.
            continue
        if message_type == 3:  # ServerCutText: discard, never reply with clipboard data.
            header = wire.read(7)
            length = struct.unpack_from("!I", header, 3)[0]
            if length > MAX_TEXT_BYTES:
                raise ProbeError("Server clipboard message exceeds the text length limit")
            wire.read(length)
            continue
        if message_type != 0:
            raise ProbeError(f"Unsupported server message type {message_type}")

        header = wire.read(3)
        count = struct.unpack_from("!H", header, 1)[0]
        rectangles_seen += count
        if rectangles_seen > MAX_RECTANGLES:
            raise ProbeError("Server exceeded the rectangle count limit")
        for _ in range(count):
            x, y, rect_width, rect_height, encoding = struct.unpack("!HHHHi", wire.read(12))
            if encoding == DESKTOP_SIZE:
                validate_size(rect_width, rect_height, expected)
                if (rect_width, rect_height) != (width, height):
                    width, height = rect_width, rect_height
                    pixels = bytearray(width * height * 4)
                    coverage = bytearray(width * height)
                    covered = 0
                continue
            if encoding != RAW:
                raise ProbeError(f"Unsupported rectangle encoding {encoding}; requested raw only")
            if x + rect_width > width or y + rect_height > height:
                raise ProbeError("Server rectangle extends beyond the framebuffer")
            raw = wire.read(rect_width * rect_height * 4)
            row_bytes = rect_width * 4
            for row in range(rect_height):
                first_pixel = (y + row) * width + x
                last_pixel = first_pixel + rect_width
                start = first_pixel * 4
                pixels[start:start + row_bytes] = raw[row * row_bytes:(row + 1) * row_bytes]
                covered += coverage[first_pixel:last_pixel].count(0)
                coverage[first_pixel:last_pixel] = b"\x01" * rect_width

        if covered == width * height:
            return width, height, bytes(pixels), rectangles_seen
        # Some servers split their initial snapshot across separate updates.
        request_full_frame()
    raise ProbeError("Server exceeded the message count limit before completing a frame")


def save_png(path: Path, width: int, height: int, bgrx: bytes) -> None:
    """Encode RGB PNG directly; Pillow is not required."""
    def chunk(kind: bytes, payload: bytes) -> bytes:
        checksum = zlib.crc32(payload, zlib.crc32(kind)) & 0xFFFFFFFF
        return struct.pack("!I", len(payload)) + kind + payload + struct.pack("!I", checksum)

    compressor = zlib.compressobj(level=6)
    with path.open("wb") as output:
        output.write(b"\x89PNG\r\n\x1a\n")
        output.write(chunk(b"IHDR", struct.pack("!IIBBBBB", width, height, 8, 2, 0, 0, 0)))
        for y in range(height):
            row = bgrx[y * width * 4:(y + 1) * width * 4]
            rgb = bytearray(width * 3)
            rgb[0::3], rgb[1::3], rgb[2::3] = row[2::4], row[1::4], row[0::4]
            compressed = compressor.compress(b"\x00" + rgb)
            if compressed:
                output.write(chunk(b"IDAT", compressed))
        output.write(chunk(b"IDAT", compressor.flush()))
        output.write(chunk(b"IEND", b""))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1", type=loopback_address)
    parser.add_argument("--port", default=5905, type=int)
    parser.add_argument("--width", default=824, type=int, help="Expected framebuffer width")
    parser.add_argument("--height", default=1200, type=int, help="Expected framebuffer height")
    parser.add_argument("--timeout", default=20.0, type=float, help="Total session timeout in seconds")
    parser.add_argument("--output", type=Path, help="Write the complete first framebuffer as PNG")
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    if not 0 < args.timeout <= 120:
        parser.error("--timeout must be greater than 0 and at most 120 seconds")
    if args.output is not None and args.output.suffix.lower() != ".png":
        parser.error("--output must use the .png extension")

    started = time.monotonic()
    try:
        expected = (args.width, args.height)
        validate_size(*expected, expected)
        with socket.create_connection((args.host, args.port), timeout=args.timeout) as sock:
            if not ipaddress.ip_address(sock.getpeername()[0]).is_loopback:
                raise ProbeError("Connected peer is not loopback")
            wire = Wire(sock, started + args.timeout)
            version, name, width, height = negotiate(wire)
            width, height, pixels, rectangles = read_frame(wire, width, height, expected)
            received = wire.received
        output_path = None
        if args.output is not None:
            output_path = args.output.resolve()
            save_png(output_path, width, height, pixels)
        print(json.dumps({
            "ok": True, "protocol": version, "desktop_name": name,
            "width": width, "height": height, "rectangles": rectangles,
            "received_bytes": received, "seconds": round(time.monotonic() - started, 3),
            "output": str(output_path) if output_path else None,
        }, ensure_ascii=False))
        return 0
    except (ProbeError, OSError, ValueError) as error:
        print(json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
