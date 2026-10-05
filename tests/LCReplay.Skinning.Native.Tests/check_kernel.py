"""Small native ABI/validation check; Unity fixtures provide the full pose proof."""
import ctypes as c
import math
import sys
from pathlib import Path

F = c.c_float
I = c.c_int32


class V3(c.Structure):
    _fields_ = [(n, F) for n in ("x", "y", "z")]


class V4(c.Structure):
    _fields_ = [(n, F) for n in ("x", "y", "z", "w")]


class Matrix(c.Structure):
    _fields_ = [("m", F * 16)]


class Weight(c.Structure):
    _fields_ = [("weights", F * 4), ("bones", I * 4)]


class Data(c.Structure):
    _fields_ = [(n, c.c_uint32) for n in ("abi", "size")]
    _fields_ += [(n, I) for n in (
        "vertexCount", "normalCount", "tangentCount", "boneCount", "weightCount", "pointCount",
        "pointOffsetsCount", "pointVerticesCount", "weightOffsetsCount", "weightPointsCount")]
    _fields_ += [("flags", c.c_uint32), ("reserved", c.c_uint32), ("localToWorld", Matrix)]
    _fields_ += [(n, F) for n in ("depthX", "depthY", "depthZ", "depthOffset", "orthographicWidth", "pixelScale")]
    _fields_ += [(n, c.c_void_p) for n in (
        "matrices", "skinMatrices", "matrixWeights", "vertices", "normals", "positions", "directions",
        "outlineNormals", "outlineDirections", "outlinePositions", "tangents", "tangentOutput",
        "pointRepresentatives", "pointOffsets", "pointVertices", "weightOffsets", "weightPoints")]


def matrix(tx=0.0):
    return Matrix((F * 16)(1.1, .1, -.05, 0, -.3, .9, .2, 0, .12, -.13, 1.2, 0, tx, .2, -.4, 1))


def f(v):
    return F(v).value


def vector(m, v, point=False):
    out = [f(f(f(m[r] * v[0]) + f(m[r + 4] * v[1])) + f(m[r + 8] * v[2])) for r in range(3)]
    return [f(out[r] + m[r + 12]) for r in range(3)] if point else out


def magnitude(v):
    return f(math.sqrt(f(f(f(v[0] * v[0]) + f(v[1] * v[1])) + f(v[2] * v[2]))))


def normalize(v):
    length = magnitude(v)
    return [f(x / length) for x in v] if length > f(.00001) else [0, 0, 0]


def values(v):
    return [v.x, v.y, v.z]


assert c.sizeof(Data) == 280 and Data.matrices.offset == 144
dll_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / "src/LCReplay.Plugin/bin/Release/netstandard2.1/LCReplay.Skinning.dll"
dll = c.CDLL(str(dll_path.resolve()))
dll.lc_skin_abi.restype = c.c_uint32
dll.lc_skin_validate.argtypes = [c.POINTER(Data)]
dll.lc_skin_weight_range.argtypes = [c.POINTER(Data), I, I]
assert dll.lc_skin_abi() == 0x3901
arrays = {
    "matrices": (Matrix * 4)(*[matrix(i * .12) for i in range(4)]),
    "skinMatrices": (Matrix * 1)(),
    "matrixWeights": (Weight * 1)(Weight((F * 4)(.25, .125, .5, .125), (I * 4)(0, 1, 2, 3))),
    "vertices": (V3 * 3)(V3(.2, .3, .4), V3(.2, .3, .4), V3(-.5, .7, -.2)),
    "normals": (V3 * 3)(V3(0, 1, .1), V3(.4, .8, 0), V3(0, 0, 0)),
    "positions": (V3 * 3)(), "directions": (V3 * 3)(),
    "outlineNormals": (V3 * 3)(V3(.2, .9, .05), V3(.2, .9, .05), V3(1, .5, 0)),
    "outlineDirections": (V3 * 3)(), "outlinePositions": (V3 * 3)(),
    "tangents": (V4 * 3)(V4(1, .3, 0, 1), V4(.9, -.1, .2, -1), V4(0, 1, 0, 1)),
    "tangentOutput": (V4 * 3)(),
    "pointRepresentatives": (I * 2)(0, 2), "pointOffsets": (I * 3)(0, 2, 3),
    "pointVertices": (I * 3)(0, 1, 2), "weightOffsets": (I * 2)(0, 2), "weightPoints": (I * 2)(0, 1),
}
d = Data(abi=0x3901, size=280, vertexCount=3, normalCount=3, tangentCount=3, boneCount=4, weightCount=1,
         pointCount=2, pointOffsetsCount=3, pointVerticesCount=3, weightOffsetsCount=2, weightPointsCount=2,
         flags=2, localToWorld=matrix(), depthX=.1, depthY=.2, depthZ=.9, depthOffset=5, pixelScale=.0025)
for name, array in arrays.items():
    setattr(d, name, c.addressof(array))
assert dll.lc_skin_validate(c.byref(d)) == 0
d.size -= 1
assert dll.lc_skin_validate(c.byref(d)) == 1
d.size += 1
d.pointOffsetsCount = 1
assert dll.lc_skin_validate(c.byref(d)) == 3
d.pointOffsetsCount = 3
arrays["pointVertices"][0] = 3
assert dll.lc_skin_validate(c.byref(d)) == 11
arrays["pointVertices"][0] = 0
assert dll.lc_skin_weight_range(c.byref(d), -1, 1) == 12
assert dll.lc_skin_weight_range(c.byref(d), 0, 2) == 12
assert dll.lc_skin_weight_range(c.byref(d), 0, 1) == 0
expected_matrix = list(arrays["matrices"][0].m)
for j in range(16):
    if j % 4 == 3:
        continue
    expected_matrix[j] = f(expected_matrix[j] * .25)
    for slot, weight in [(1, .125), (2, .5), (3, .125)]:
        expected_matrix[j] = f(expected_matrix[j] + f(arrays["matrices"][slot].m[j] * weight))
maximum = 0.0
for i in range(3):
    position = vector(expected_matrix, values(arrays["vertices"][i]), True)
    direction = vector(expected_matrix, values(arrays["outlineNormals"][i]))
    depth = f(f(f(f(position[0] * d.depthX) + f(position[1] * d.depthY)) + f(position[2] * d.depthZ)) + d.depthOffset)
    width = max(f(.001), min(f(.014), f(depth * d.pixelScale)))
    length = magnitude(vector(d.localToWorld.m, direction))
    offset = f(width / length) if length > f(.00001) else 0
    outline = [f(position[r] + f(direction[r] * offset)) for r in range(3)]
    for name, expected in [
        ("positions", position), ("directions", normalize(vector(expected_matrix, values(arrays["normals"][i])))),
        ("outlineDirections", direction), ("outlinePositions", outline),
        ("tangentOutput", normalize(vector(expected_matrix, values(arrays["tangents"][i])))),
    ]:
        maximum = max(maximum, max(abs(a - b) for a, b in zip(expected, values(arrays[name][i]))))
    assert arrays["tangentOutput"][i].w == arrays["tangents"][i].w
assert maximum <= 0.000001, maximum
print(f"Native skinning ABI/validation PASS maximumError={maximum} invalidRequests=5")
