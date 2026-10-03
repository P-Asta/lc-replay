"""Summarize replay actor, line, and particle data without writing a file."""
import gzip
import json
import struct
import sys
from collections import Counter


def inspect(path):
    counts = Counter()
    names = Counter()
    samples = {}
    emitter_names = {}
    with open(path, "rb") as source:
        if source.read(8) != b"LCREPL01":
            raise ValueError("Not an LCR file")
        while header := source.read(8):
            if len(header) < 8:
                break
            size, _ = struct.unpack("<ii", header)
            record = json.loads(gzip.decompress(source.read(size)))
            if record.get("Kind") == "world":
                world = record.get("World", {})
                for light in world.get("Lights", []):
                    name = light.get("Name", "")
                    counts[("light", name)] += 1
                    if any(token in name.lower() for token in ("laser", "turret", "light")):
                        samples.setdefault(("light", name), light)
                for geometry in world.get("Geometry", []):
                    name = geometry.get("Name", "")
                    if geometry.get("EntityId") == "e63":
                        samples.setdefault(("spawnMeshTime", "e63"), record.get("Time"))
                    if geometry.get("EntityId"):
                        counts[("actorGeometry", geometry.get("EntityId"))] += 1
                    position = geometry.get("Position", {})
                    if (position.get("Y", 0) < -205 and position.get("Y", 0) > -216 and
                            abs(position.get("X", 0) + 43.7) < 3 and abs(position.get("Z", 0) - 58.3) < 3):
                        counts[("nearTurret", name)] += 1
                    if any(token in name.lower() for token in ("laser", "turret", "gunbarrel", "bullet", "mount", "plane", "sphere", "rod", "gunbody")):
                        counts[("world", name)] += 1
                        samples.setdefault(("world", name), geometry)
                continue
            if record.get("Kind") == "event":
                event = record.get("Event", {})
                if event.get("Category") == "animation" and event.get("Name") == "state":
                    counts[("animation", event.get("Data", {}).get("clip", ""))] += 1
                    if "Vent" in event.get("Data", {}).get("clip", ""):
                        samples.setdefault(("spawnState", "first"), event)
                if event.get("Category") == "animation" and event.get("Name") == "track":
                    track = event.get("AnimationTrack") or {}
                    counts[("track", track.get("Clip", ""))] += 1
                continue
            if record.get("Kind") != "frame":
                continue
            frame = record.get("Frame", {})
            for entity in frame.get("Entities", []):
                if entity.get("Kind") in ("enemy", "hazard"):
                    key = (entity.get("Kind"), entity.get("Name"))
                    names[key] += 1
                    samples.setdefault(key, entity)
                    if "turret" in (entity.get("Name") or "").lower():
                        state = entity.get("State", {})
                        counts[("turretMode", state.get("turretMode", "?"))] += 1
                        if state.get("turretMode") == "Firing":
                            samples.setdefault(("firingTime", "first"), record.get("Time"))
            for key in ("Lines", "Particles", "ParticleStyles"):
                counts[key] += len(frame.get(key, []))
                for value in frame.get(key, []):
                    if key != "Particles":
                        names[(key, value.get("Name", value.get("MaterialName", "")))] += 1
                        samples.setdefault((key, value.get("Name", value.get("MaterialName", ""))), value)
                        if key == "ParticleStyles":
                            emitter_names[value.get("Id", "")] = value.get("Name", "")
                    else:
                        counts[("particle", emitter_names.get(value.get("EmitterId", ""), "unknown"))] += 1
                        if emitter_names.get(value.get("EmitterId", ""), "") == "BulletParticle":
                            samples.setdefault(("bulletPose", "BulletParticle"), value)
    print(path)
    print("counts", counts)
    print("names", names.most_common(30))
    for key, value in samples.items():
        if key[0] in ("firingTime", "spawnState", "spawnMeshTime"):
            print("sample", key, value)
            continue
        if key[0] == "hazard" and "turret" in key[1].lower() or key[0] in ("enemy", "Lines", "ParticleStyles", "world", "light", "bulletPose"):
            if key[0] == "world":
                print("sample", key, {name: value.get(name) for name in
                    ("Id", "Name", "EntityId", "AnchorId", "Active", "Dynamic", "MaterialId", "MaterialName", "Position")})
            else:
                print("sample", key, str(value)[:700])


for path in sys.argv[1:]:
    inspect(path)
