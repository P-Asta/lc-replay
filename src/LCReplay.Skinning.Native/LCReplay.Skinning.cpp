#include <cmath>
#include <cstddef>
#include <cstdint>

// Numeric ABI: numeric arrays only. No Unity objects, native allocations,
// persistent pointers, worker creation, or mesh uploads cross this boundary.
namespace
{
constexpr std::uint32_t Abi = 0x3901;
struct Vec3 { float x, y, z; };
struct Vec4 { float x, y, z, w; };
struct Matrix { float m[16]; }; // Unity column-major Matrix4x4 field layout.
struct Weight { float weights[4]; std::int32_t bones[4]; };
struct KernelData
{
    std::uint32_t abi, size;
    std::int32_t vertexCount, normalCount, tangentCount, boneCount, weightCount, pointCount;
    std::int32_t pointOffsetsCount, pointVerticesCount, weightOffsetsCount, weightPointsCount;
    std::uint32_t flags, reserved; // bit 0: orthographic, bit 1: combined outline.
    Matrix localToWorld;
    float depthX, depthY, depthZ, depthOffset, orthographicWidth, pixelScale;
    const Matrix* matrices;
    Matrix* skinMatrices;
    const Weight* matrixWeights;
    const Vec3* vertices;
    const Vec3* normals;
    Vec3* positions;
    Vec3* directions;
    const Vec3* outlineNormals;
    Vec3* outlineDirections;
    Vec3* outlinePositions;
    const Vec4* tangents;
    Vec4* tangentOutput;
    const std::int32_t* pointRepresentatives;
    const std::int32_t* pointOffsets;
    const std::int32_t* pointVertices;
    const std::int32_t* weightOffsets;
    const std::int32_t* weightPoints;
};
static_assert(sizeof(void*) == 8, "x64 only");
static_assert(sizeof(Vec3) == 12 && sizeof(Vec4) == 16 && sizeof(Matrix) == 64 && sizeof(Weight) == 32);
static_assert(sizeof(KernelData) == 280 && offsetof(KernelData, matrices) == 144);

int Header(const KernelData* d)
{
    if (!d || d->abi != Abi || d->size != sizeof(KernelData) || d->reserved || (d->flags & ~3u)) return 1;
    if (d->vertexCount < 0 || d->vertexCount > 10000000 || d->boneCount <= 0 || d->boneCount > 65536 ||
        d->weightCount < 0 || d->weightCount > d->vertexCount || d->pointCount < 0 || d->pointCount > d->vertexCount) return 2;
    if ((d->normalCount != 0 && d->normalCount != d->vertexCount) ||
        (d->tangentCount != 0 && d->tangentCount != d->vertexCount) ||
        d->pointOffsetsCount != d->pointCount + 1 || d->pointVerticesCount != d->vertexCount ||
        d->weightOffsetsCount != d->weightCount + 1 || d->weightPointsCount != d->pointCount) return 3;
    if (!d->matrices || !d->weightOffsets || !d->pointOffsets) return 4;
    if (d->vertexCount && (!d->skinMatrices || !d->matrixWeights || !d->vertices || !d->positions ||
        !d->outlineNormals || !d->outlineDirections || !d->outlinePositions || !d->pointRepresentatives ||
        !d->pointVertices || !d->weightPoints)) return 5;
    if ((d->normalCount && (!d->normals || !d->directions)) || (d->tangentCount && (!d->tangents || !d->tangentOutput))) return 6;
    return 0;
}

inline Vec3 Vector(const Matrix& m, Vec3 v)
{
    return { m.m[0] * v.x + m.m[4] * v.y + m.m[8] * v.z,
        m.m[1] * v.x + m.m[5] * v.y + m.m[9] * v.z,
        m.m[2] * v.x + m.m[6] * v.y + m.m[10] * v.z };
}
inline Vec3 Point(const Matrix& m, Vec3 v)
{
    auto r = Vector(m, v);
    r.x += m.m[12]; r.y += m.m[13]; r.z += m.m[14];
    return r;
}
inline float Magnitude(Vec3 v)
{
    // Mathf.Sqrt casts the result of System.Math.Sqrt back to float.
    const float squared = v.x * v.x + v.y * v.y + v.z * v.z;
    return static_cast<float>(std::sqrt(static_cast<double>(squared)));
}
inline Vec3 Normalize(Vec3 v)
{
    const float length = Magnitude(v);
    if (length > .00001f) return { v.x / length, v.y / length, v.z / length };
    return {};
}
inline Vec3 Outline(const KernelData& d, Vec3 p, Vec3 direction)
{
    const float depth = p.x * d.depthX + p.y * d.depthY + p.z * d.depthZ + d.depthOffset;
    float width = (d.flags & 1) ? d.orthographicWidth : depth * d.pixelScale;
    if (width < .001f) width = .001f;
    else if (width > .014f) width = .014f;
    const float length = Magnitude(Vector(d.localToWorld, direction));
    const float offset = length > .00001f ? width / length : 0.f;
    return { p.x + direction.x * offset, p.y + direction.y * offset, p.z + direction.z * offset };
}
}

extern "C" __declspec(dllexport) std::uint32_t __cdecl lc_skin_abi() { return Abi; }

extern "C" __declspec(dllexport) int __cdecl lc_skin_validate(const KernelData* d)
{
    const int status = Header(d);
    if (status) return status;
    if (d->weightOffsets[0] || d->weightOffsets[d->weightCount] != d->pointCount ||
        d->pointOffsets[0] || d->pointOffsets[d->pointCount] != d->vertexCount) return 7;
    for (int group = 0; group < d->weightCount; ++group)
    {
        const auto& w = d->matrixWeights[group];
        for (int slot = 0; slot < 4; ++slot)
            if ((slot == 0 || w.weights[slot] != 0.f) && (w.bones[slot] < 0 || w.bones[slot] >= d->boneCount)) return 8;
        if (d->weightOffsets[group] < 0 || d->weightOffsets[group + 1] < d->weightOffsets[group] ||
            d->weightOffsets[group + 1] > d->pointCount) return 9;
    }
    for (int point = 0; point < d->pointCount; ++point)
        if (d->pointRepresentatives[point] < 0 || d->pointRepresentatives[point] >= d->vertexCount ||
            d->weightPoints[point] < 0 || d->weightPoints[point] >= d->pointCount ||
            d->pointOffsets[point] < 0 || d->pointOffsets[point + 1] < d->pointOffsets[point] ||
            d->pointOffsets[point + 1] > d->vertexCount) return 10;
    for (int i = 0; i < d->vertexCount; ++i)
        if (d->pointVertices[i] < 0 || d->pointVertices[i] >= d->vertexCount) return 11;
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl lc_skin_weight_range(const KernelData* d, int start, int end)
{
    const int status = Header(d);
    if (status) return status;
    if (start < 0 || end < start || end > d->weightCount) return 12;
    for (int group = start; group < end; ++group)
    {
        const auto& w = d->matrixWeights[group];
        Matrix matrix = d->matrices[w.bones[0]];
        if (w.weights[0] != 1.f)
            for (int column = 0; column < 4; ++column)
                for (int row = 0; row < 3; ++row) matrix.m[column * 4 + row] *= w.weights[0];
        for (int slot = 1; slot < 4; ++slot)
            if (w.weights[slot] != 0.f)
            {
                const auto& other = d->matrices[w.bones[slot]];
                for (int column = 0; column < 4; ++column)
                    for (int row = 0; row < 3; ++row) matrix.m[column * 4 + row] += other.m[column * 4 + row] * w.weights[slot];
            }
        d->skinMatrices[group] = matrix;
        for (int pi = d->weightOffsets[group]; pi < d->weightOffsets[group + 1]; ++pi)
        {
            const int point = d->weightPoints[pi];
            const int representative = d->pointRepresentatives[point];
            const Vec3 position = Point(matrix, d->vertices[representative]);
            const Vec3 outlineDirection = Vector(matrix, d->outlineNormals[representative]);
            const Vec3 outlinePosition = (d->flags & 2) ? Outline(*d, position, outlineDirection) : Vec3{};
            for (int vi = d->pointOffsets[point]; vi < d->pointOffsets[point + 1]; ++vi)
            {
                const int i = d->pointVertices[vi];
                d->positions[i] = position;
                d->outlineDirections[i] = outlineDirection;
                if (d->flags & 2) d->outlinePositions[i] = outlinePosition;
                if (d->normalCount) d->directions[i] = Normalize(Vector(matrix, d->normals[i]));
                if (d->tangentCount)
                {
                    const auto t = d->tangents[i];
                    const auto direction = Normalize(Vector(matrix, { t.x, t.y, t.z }));
                    d->tangentOutput[i] = { direction.x, direction.y, direction.z, t.w };
                }
            }
        }
    }
    return 0;
}
