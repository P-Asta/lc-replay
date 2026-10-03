"""Print world-capture triggers and payload sizes from an LCR recording."""
import gzip
import json
import struct
import sys
from collections import Counter


def main(path):
    frame_sizes = Counter()
    entity_sizes = Counter()
    with open(path, "rb") as source:
        assert source.read(8) == b"LCREPL01"
        while header := source.read(8):
            compressed, _ = struct.unpack("<ii", header)
            payload = source.read(compressed)
            if len(payload) != compressed:
                break
            record = json.loads(gzip.decompress(payload))
            kind = record.get("Kind")
            if kind == "world":
                world = record["World"]
                print(f'{record["Time"]:7.2f} world {world.get("Layer", ""):8} '
                      f'{compressed / 1048576:6.2f} MiB {len(world.get("Geometry", [])):4} geometry '
                      f'{world.get("CaptureSetId", "")[:8]}')
            elif kind == "event":
                event = record.get("Event", {})
                name = event.get("Name", "")
                if event.get("Category") == "scene" or name.startswith((
                        "RoundManager.Generate", "RoundManager.Finish", "StartOfRound.SetShipReady",
                        "StartOfRound.StartGame")) or name == "world-captured":
                    print(f'{record["Time"]:7.2f} {event.get("Category", ""):7} {name} '
                          f'{event.get("Data", {}).get("scene", "")}')
            elif kind == "frame":
                frame = record["Frame"]
                for key, value in frame.items():
                    if key == "Entities":
                        for entity in value:
                            entity_sizes[entity.get("Kind", "")] += len(json.dumps(entity))
                    else:
                        frame_sizes[key] += len(json.dumps(value))
    print("frame payload estimates", frame_sizes)
    print("entity payload estimates", entity_sizes)


if __name__ == "__main__":
    main(sys.argv[1])
