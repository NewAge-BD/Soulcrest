"""Shared offline validation of the original gaming.tools GTPK v2 tile bytes."""
from pathlib import Path
import struct

import cv2
import numpy as np


def decode_tile_pack(blob):
    """Validate the container and decoded tile dimensions before accepting any bytes."""
    if len(blob) < 48:
        raise ValueError('Truncated GTPK header')
    magic, version, count, tile_size, minimum, maximum, width, height, entry_size, offset, _, _ = struct.unpack_from('<12I', blob)
    if magic != 0x4b505447 or version != 2 or entry_size != 24 or tile_size != 512:
        raise ValueError('Unsupported GTPK header')
    if offset < 48 or offset + count * entry_size > len(blob):
        raise ValueError('Invalid GTPK index')
    entries = []
    keys = set()
    for index in range(count):
        z, x, y, start, length = struct.unpack_from('<B3xIIQI', blob, offset + index * entry_size)
        if (z > maximum or start < offset + count * entry_size or length == 0 or start + length > len(blob)
                or (z, x, y) in keys or x >= 2**z or y >= 2**z):
            raise ValueError('Invalid GTPK tile entry')
        keys.add((z, x, y))
        image = cv2.imdecode(np.frombuffer(blob[start:start+length], dtype=np.uint8), cv2.IMREAD_COLOR)
        if image is None or image.shape[:2] != (512, 512):
            raise ValueError('Invalid GTPK image')
        entries.append((z, x, y, blob[start:start+length]))
    return {'width': width, 'height': height, 'maxZoom': maximum, 'minZoom': minimum}, entries


def read_tile_pack(path):
    return decode_tile_pack(Path(path).read_bytes())
