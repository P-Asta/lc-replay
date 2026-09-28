"""Inspect independent world records without expanding a whole replay in memory."""
import gzip
import json
import struct
import sys

with open(sys.argv[1], "rb") as source:
    assert source.read(8) == b"LCREPL01"
    number = 0
    sets = {}
    totals = {}
    audio_blocks = 0
    audio_dropped = 0
    particle_frames = 0
    sample_gaps = []
    while header := source.read(8):
        compressed, expanded = struct.unpack("<ii", header)
        payload = source.read(compressed)
        number += 1
        if len(payload) != compressed:
            break
        if number > 1:
            data = gzip.decompress(payload)
            record = json.loads(data)
            kind = record.get("Kind", "unknown")
            count, size = totals.get(kind, (0, 0))
            totals[kind] = (count + 1, size + compressed + 8)
            if kind == "frame" and record.get("Frame", {}).get("Particles"):
                particle_frames += 1
            if kind == "event":
                event = record.get("Event", {})
                if event.get("Category") == "audio" and event.get("Name") == "source-block":
                    audio_blocks += 1
                elif event.get("Category") == "audio" and event.get("Name") == "dropped-blocks":
                    audio_dropped += int(event.get("Data", {}).get("count", 0))
                elif event.get("Category") == "capture" and event.get("Name") == "sample-gap":
                    sample_gaps.append((record.get("Time", 0), float(event.get("Data", {}).get("seconds", 0))))
            if kind == "world":
                world = record["World"]
                geometry = world.get("Geometry", [])
                refs = world.get("AssetRendererPaths", [])
                ground = [g["Name"] for g in geometry if any(
                    name in g["Name"].lower() for name in ("terrain", "rock", "stone", "ground"))]
                print(number, "time", record.get("Time"), world.get("Layer"), "geometry", len(geometry),
                      "compressed", compressed, "expanded", expanded,
                      "assetRefs", len(refs), "maxRefLength", max(map(len, refs), default=0),
                      "duplicateRefs", len(refs) - len(set(refs)),
                      "ground", ground[:12])
                sets.setdefault(world.get("CaptureSetId", ""), set()).update(
                    item["Id"] for item in geometry if not item.get("EntityId") and not item.get("AnchorId"))
    keys = list(sets)
    for previous, current in zip(keys, keys[1:]):
        a, b = sets[previous], sets[current]
        print("static overlap", len(a & b), "/", len(a), len(b), "changed", len(a ^ b))
    print("record totals", totals)
    print("media", {"audio_blocks": audio_blocks, "audio_dropped": audio_dropped,
                    "particle_frames": particle_frames, "sample_gaps": len(sample_gaps),
                    "largest_gap_seconds": max((gap for _, gap in sample_gaps), default=0)})
    print("sample gaps", sample_gaps[:20])
