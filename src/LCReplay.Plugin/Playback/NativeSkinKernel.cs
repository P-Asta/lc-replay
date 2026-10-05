using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace LCReplay.Plugin.Playback
{
    // Optional Windows x64 numeric backend. The managed baker owns the arrays,
    // bone transforms, and mesh uploads; this helper lends pinned arrays only
    // for one joined numeric pass and never passes Unity objects across the ABI.
    internal sealed class NativeSkinKernel
    {
        private const uint Abi = 0x3901;
        private readonly NativeApi api;
        private readonly Array[] arrays;
        private readonly GCHandle[] pins;
        private KernelData data;
        private bool validated;
        private volatile bool ready, disabled;
        private string? failure;

        [StructLayout(LayoutKind.Sequential)]
        private struct KernelData
        {
            internal uint Abi, Size;
            internal int VertexCount, NormalCount, TangentCount, BoneCount, WeightCount, PointCount;
            internal int PointOffsetsCount, PointVerticesCount, WeightOffsetsCount, WeightPointsCount;
            internal uint Flags, Reserved;
            internal Matrix4x4 LocalToWorld;
            internal float DepthX, DepthY, DepthZ, DepthOffset, OrthographicWidth, PixelScale;
            internal IntPtr Matrices, SkinMatrices, MatrixWeights, Vertices, Normals, Positions, Directions;
            internal IntPtr OutlineNormals, OutlineDirections, OutlinePositions, Tangents, TangentOutput;
            internal IntPtr PointRepresentatives, PointOffsets, PointVertices, WeightOffsets, WeightPoints;
        }

        internal static NativeSkinKernel? Create(Matrix4x4[] matrices, Matrix4x4[] skinMatrices, BoneWeight[] weights,
            Vector3[] vertices, Vector3[] normals, Vector3[] positions, Vector3[] directions, Vector3[] outlineNormals,
            Vector3[] outlineDirections, Vector3[] outlinePositions, Vector4[] tangents, Vector4[] tangentOutput,
            int[] pointRepresentatives, int[] pointOffsets, int[] pointVertices, int[] weightOffsets, int[] weightPoints)
        {
            var api = NativeApi.Instance.Value;
            if (api == null) return null;
            var count = vertices.Length;
            if (positions.Length != count || outlineNormals.Length != count || outlineDirections.Length != count || outlinePositions.Length != count ||
                skinMatrices.Length != weights.Length || directions.Length != (normals.Length == count ? count : 0) ||
                tangentOutput.Length != (tangents.Length == count ? count : 0)) return null;
            return new NativeSkinKernel(api, new Array[] { matrices, skinMatrices, weights, vertices, normals, positions, directions,
                outlineNormals, outlineDirections, outlinePositions, tangents, tangentOutput,
                pointRepresentatives, pointOffsets, pointVertices, weightOffsets, weightPoints }, new KernelData
                {
                    Abi = Abi, Size = 280, VertexCount = count,
                    NormalCount = directions.Length, TangentCount = tangentOutput.Length, BoneCount = matrices.Length,
                    WeightCount = weights.Length, PointCount = pointRepresentatives.Length,
                    PointOffsetsCount = pointOffsets.Length, PointVerticesCount = pointVertices.Length,
                    WeightOffsetsCount = weightOffsets.Length, WeightPointsCount = weightPoints.Length
                });
        }

        private NativeSkinKernel(NativeApi api, Array[] arrays, KernelData data)
        {
            this.api = api; this.arrays = arrays; this.data = data;
            pins = new GCHandle[arrays.Length];
        }

        internal bool Begin(bool outline, Matrix4x4 localToWorld, float depthX, float depthY, float depthZ,
            float depthOffset, bool orthographic, float orthographicWidth, float pixelScale)
        {
            if (disabled) return false;
            data.LocalToWorld = localToWorld;
            data.DepthX = depthX; data.DepthY = depthY; data.DepthZ = depthZ; data.DepthOffset = depthOffset;
            data.OrthographicWidth = orthographicWidth; data.PixelScale = pixelScale;
            data.Flags = (orthographic ? 1u : 0u) | (outline ? 2u : 0u);
            try
            {
                for (var i = 0; i < arrays.Length; i++) pins[i] = GCHandle.Alloc(arrays[i], GCHandleType.Pinned);
                data.Matrices = pins[0].AddrOfPinnedObject(); data.SkinMatrices = pins[1].AddrOfPinnedObject();
                data.MatrixWeights = pins[2].AddrOfPinnedObject(); data.Vertices = pins[3].AddrOfPinnedObject();
                data.Normals = pins[4].AddrOfPinnedObject(); data.Positions = pins[5].AddrOfPinnedObject();
                data.Directions = pins[6].AddrOfPinnedObject(); data.OutlineNormals = pins[7].AddrOfPinnedObject();
                data.OutlineDirections = pins[8].AddrOfPinnedObject(); data.OutlinePositions = pins[9].AddrOfPinnedObject();
                data.Tangents = pins[10].AddrOfPinnedObject(); data.TangentOutput = pins[11].AddrOfPinnedObject();
                data.PointRepresentatives = pins[12].AddrOfPinnedObject(); data.PointOffsets = pins[13].AddrOfPinnedObject();
                data.PointVertices = pins[14].AddrOfPinnedObject(); data.WeightOffsets = pins[15].AddrOfPinnedObject();
                data.WeightPoints = pins[16].AddrOfPinnedObject();
                // Topology and weights are immutable for this baker; validate
                // their complete index ranges once before the first native run.
                if (!validated)
                {
                    var validation = api.Validate(ref data);
                    if (validation != 0) throw new InvalidOperationException("validation status " + validation);
                    validated = true;
                }
                ready = true;
                return true;
            }
            catch (Exception error)
            {
                Disable(error.Message);
                End(); // Also frees a partially completed pin sequence.
                return false;
            }
        }

        internal bool TryBakeRange(int start, int end)
        {
            if (!ready || disabled) return false;
            try
            {
                var status = api.Bake(ref data, start, end);
                return status == 0 || Disable("evaluation status " + status);
            }
            catch (Exception error) { return Disable(error.Message); }
        }

        // The caller joins every worker before ending the pin scope. Error
        // reporting stays here on the Unity thread, never inside a worker.
        internal void End()
        {
            ready = false;
            for (var i = pins.Length - 1; i >= 0; i--) if (pins[i].IsAllocated) pins[i].Free();
            data.Matrices = data.SkinMatrices = data.MatrixWeights = data.Vertices = data.Normals = data.Positions =
                data.Directions = data.OutlineNormals = data.OutlineDirections = data.OutlinePositions = data.Tangents =
                data.TangentOutput = data.PointRepresentatives = data.PointOffsets = data.PointVertices =
                data.WeightOffsets = data.WeightPoints = IntPtr.Zero;
            var reason = Interlocked.Exchange(ref failure, null);
            if (reason != null)
                Debug.LogWarning("LC Replay: native skinning failed (" + reason + "); using managed skinning for this mesh.");
        }

        private bool Disable(string reason)
        {
            disabled = true;
            Interlocked.CompareExchange(ref failure, reason, null);
            return false;
        }

        private sealed class NativeApi
        {
            internal static readonly Lazy<NativeApi?> Instance = new Lazy<NativeApi?>(Load);
            internal readonly ValidateDelegate Validate;
            internal readonly BakeDelegate Bake;
            // Keep the DLL loaded for the process lifetime, as with an ordinary
            // P/Invoke module; unloading while cached delegates exist is unsafe.
            private readonly IntPtr library;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiDelegate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ValidateDelegate(ref KernelData data);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int BakeDelegate(ref KernelData data, int start, int end);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
            private static extern IntPtr GetProcAddress(IntPtr module, string name);
            [DllImport("kernel32.dll", ExactSpelling = true)]
            [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);

            private NativeApi(IntPtr library, ValidateDelegate validate, BakeDelegate bake)
            { this.library = library; Validate = validate; Bake = bake; }

            private static T Export<T>(IntPtr library, string name) where T : Delegate
            {
                var address = GetProcAddress(library, name);
                if (address == IntPtr.Zero) throw new MissingMethodException(name);
                return (T)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
            }

            private static NativeApi? Load()
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8) return null;
                IntPtr library = IntPtr.Zero;
                try
                {
                    if (Marshal.SizeOf(typeof(Vector3)) != 12 || Marshal.SizeOf(typeof(Vector4)) != 16 ||
                        Marshal.SizeOf(typeof(Matrix4x4)) != 64 || Marshal.SizeOf(typeof(BoneWeight)) != 32 ||
                        Marshal.SizeOf(typeof(KernelData)) != 280 || Marshal.OffsetOf(typeof(KernelData), "Matrices").ToInt32() != 144)
                        throw new InvalidOperationException("numeric ABI layout mismatch");
                    for (var i = 0; i < 4; i++)
                        if (Marshal.OffsetOf(typeof(BoneWeight), "m_Weight" + i).ToInt32() != i * 4 ||
                            Marshal.OffsetOf(typeof(BoneWeight), "m_BoneIndex" + i).ToInt32() != 16 + i * 4)
                            throw new InvalidOperationException("bone-weight ABI layout mismatch");
                    for (var column = 0; column < 4; column++)
                        for (var row = 0; row < 4; row++)
                            if (Marshal.OffsetOf(typeof(Matrix4x4), "m" + row + column).ToInt32() != (column * 4 + row) * 4)
                                throw new InvalidOperationException("matrix ABI layout mismatch");
                    var directory = Path.GetDirectoryName(typeof(NativeSkinKernel).Assembly.Location);
                    if (string.IsNullOrEmpty(directory)) return null;
                    var path = Path.GetFullPath(Path.Combine(directory, "LCReplay.Skinning.dll"));
                    if (!File.Exists(path)) return null;
                    library = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x1000);
                    if (library == IntPtr.Zero) throw new InvalidOperationException("DLL load error " + Marshal.GetLastWin32Error());
                    if (Export<AbiDelegate>(library, "lc_skin_abi")() != Abi)
                        throw new InvalidOperationException("native ABI version mismatch");
                    var api = new NativeApi(library, Export<ValidateDelegate>(library, "lc_skin_validate"),
                        Export<BakeDelegate>(library, "lc_skin_weight_range"));
                    Debug.Log("LC Replay: native x64 skinning enabled.");
                    return api;
                }
                catch (Exception error)
                {
                    if (library != IntPtr.Zero) FreeLibrary(library);
                    Debug.LogWarning("LC Replay: optional native skinning unavailable (" + error.Message + "); using managed skinning.");
                    return null;
                }
            }
        }
    }
}
