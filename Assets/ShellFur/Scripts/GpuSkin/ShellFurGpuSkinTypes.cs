using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// CPU/GPU shared layout for GPU-skin shell fur (must match HLSL / compute exactly).
/// Use explicit floats — Vector3 packing is unreliable for StructuredBuffer.
/// </summary>
public static class ShellFurGpuSkinTypes
{
    [StructLayout(LayoutKind.Sequential)]
    public struct BindVertex
    {
        public float px, py, pz, pad0;
        public float nx, ny, nz, pad1;
        public float sx, sy, sz, pad2;
        public float u, v, pad3a, pad3b;
        public float w0, w1, w2, w3;
        public float i0, i1, i2, i3;

        public const int Stride = sizeof(float) * 24; // 96

        public static BindVertex From(
            Vector3 position, Vector3 normal, Vector3 smooth, Vector2 uv,
            Vector4 weights, Vector4 indices)
        {
            return new BindVertex
            {
                px = position.x, py = position.y, pz = position.z, pad0 = 0,
                nx = normal.x, ny = normal.y, nz = normal.z, pad1 = 0,
                sx = smooth.x, sy = smooth.y, sz = smooth.z, pad2 = 0,
                u = uv.x, v = uv.y, pad3a = 0, pad3b = 0,
                w0 = weights.x, w1 = weights.y, w2 = weights.z, w3 = weights.w,
                i0 = indices.x, i1 = indices.y, i2 = indices.z, i3 = indices.w
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SkinnedVertex
    {
        public float px, py, pz, pad0;
        public float nx, ny, nz, pad1;
        public float sx, sy, sz, pad2;
        public float u, v, pad3a, pad3b;

        public const int Stride = sizeof(float) * 16; // 64
    }

    /// <summary>
    /// Manifold edge for CS fin generation (indices into skinned / bind vertex buffer).
    /// Must match HLSL FinEdge in ShellFurGpuFin.compute.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FinEdge
    {
        public uint v0, v1;
        public uint a0, a1, a2;
        public uint b0, b1, b2;
        /// <summary>bit0 = has face B (manifold). Boundary edges: hasB=0, b* unused.</summary>
        public uint flags;
        public uint pad;

        public const int Stride = sizeof(uint) * 10; // 40
        public const uint FlagHasB = 1u;
    }

    /// <summary>
    /// World-space fin vertex written by CS (B2). Drawn via SV_VertexID + StructuredBuffer.
    /// Must match HLSL FinVertex.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FinVertex
    {
        public float px, py, pz, pad0;
        public float nx, ny, nz, pad1;
        public float u, v, height01, silhouette;

        public const int Stride = sizeof(float) * 12; // 48
    }
}
