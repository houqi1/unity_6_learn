using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Per-vertex PBD guide hairs (shell-fur HTML).
/// Position-welded: unique locations share a chain; render verts look up a guide id.
/// Simulation is a Burst IJobParallelFor over guides.
/// </summary>
public sealed class ShellFurVertexPbd : IDisposable
{
    public const int MinParticles = 2;
    public const int MaxParticles = 8;
    public const int BezierHandles = 3;
    public const int DefaultParticles = 4;
    public const float WeldEpsilon = 1e-5f;

    const float MinDt = 1e-5f;
    const float MaxFrameDt = 0.1f;
    const float MaxSpeed = 30f;
    const float SpringScale = 250f;
    const int SettleSteps = 32;
    const int JobBatch = 16;

    static readonly int VertexPbdBezierId = Shader.PropertyToID("_VertexPbdBezier");
    static readonly int VertexToGuideId = Shader.PropertyToID("_VertexToGuide");
    static readonly int UseVertexPbdId = Shader.PropertyToID("_UseVertexPbd");
    static readonly int VertexPbdCountId = Shader.PropertyToID("_VertexPbdCount");
    static readonly int VertexPbdVertexCountId = Shader.PropertyToID("_VertexPbdVertexCount");

    static GraphicsBuffer s_dummyBezier;
    static GraphicsBuffer s_dummyGuideMap;

    NativeArray<float> _pos;
    NativeArray<float> _prev;
    NativeArray<float> _vel;
    NativeArray<float> _rest;
    NativeArray<float> _phase;
    NativeArray<float3> _localP;
    NativeArray<float3> _localN;
    NativeArray<float3> _rootW;
    NativeArray<float3> _nW;
    NativeArray<float4> _bezier;

    int[] _repVid;
    uint[] _vertexToGuide;

    List<Vector3> _scratchPos;
    List<Vector3> _scratchNrm;
    List<Color> _scratchCol;

    GraphicsBuffer _bezierBuffer;
    GraphicsBuffer _guideMapBuffer;

    int _vCount;
    int _gCount;
    int _pCount;
    bool _ready;
    bool _hasHistory;
    float _simTime;

    Vector3 _localCenter;
    float _localRadius;
    bool _useSphereCollider;
    bool _loggedUnreadable;
    bool _useSmoothNormalsVC;
    Mesh _builtFrom;

    public bool IsReady => _ready && _gCount > 0 && _bezierBuffer != null && _guideMapBuffer != null;
    public int VertexCount => _vCount;
    public int GuideCount => _gCount;
    public int ParticleCount => _pCount;

    public bool Matches(Mesh mesh, int nodeCount, bool useSmoothNormalsVC)
    {
        if (!IsReady || mesh == null || _builtFrom != mesh)
            return false;
        if (_vCount != mesh.vertexCount)
            return false;
        int p = Mathf.Clamp(nodeCount, MinParticles, MaxParticles);
        return _pCount == p && _useSmoothNormalsVC == useSmoothNormalsVC;
    }

    public bool Build(Mesh mesh, int nodeCount, bool useSmoothNormalsVC)
    {
        ReleaseGpu();
        DisposeNative();
        _ready = false;
        _hasHistory = false;
        _simTime = 0f;
        _vCount = 0;
        _gCount = 0;
        _pCount = 0;
        _builtFrom = null;
        _repVid = null;
        _vertexToGuide = null;

        if (mesh == null)
            return false;

        if (!mesh.isReadable)
        {
            if (!_loggedUnreadable)
            {
                Debug.LogWarning(
                    $"[ShellFurVertexPbd] Mesh '{mesh.name}' is not readable. Enable Read/Write for per-vertex PBD.");
                _loggedUnreadable = true;
            }
            return false;
        }

        if (!FillScratch(mesh, useSmoothNormalsVC))
            return false;

        int vcount = _scratchPos.Count;
        _pCount = Mathf.Clamp(nodeCount, MinParticles, MaxParticles);
        _useSmoothNormalsVC = useSmoothNormalsVC;

        WeldByPosition(vcount, out int gcount);
        if (gcount <= 0)
            return false;

        _builtFrom = mesh;
        _vCount = vcount;
        _gCount = gcount;
        int np = gcount * _pCount;

        _pos = new NativeArray<float>(np * 3, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        _prev = new NativeArray<float>(np * 3, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        _vel = new NativeArray<float>(np * 3, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        _rest = new NativeArray<float>(gcount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _phase = new NativeArray<float>(gcount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _rootW = new NativeArray<float3>(gcount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _nW = new NativeArray<float3>(gcount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _bezier = new NativeArray<float4>(gcount * BezierHandles, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        for (int g = 0; g < gcount; g++)
            _phase[g] = Hash01(g) * (math.PI * 2f);

        Bounds b = mesh.bounds;
        _localCenter = b.center;
        Vector3 e = b.extents;
        float minE = Mathf.Min(e.x, Mathf.Min(e.y, e.z));
        float maxE = Mathf.Max(e.x, Mathf.Max(e.y, e.z));
        _useSphereCollider = minE > 1e-5f && maxE <= minE * 1.2f;
        _localRadius = (e.x + e.y + e.z) * (1f / 3f);

        _bezierBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            Mathf.Max(1, gcount * BezierHandles),
            sizeof(float) * 4);
        _guideMapBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            Mathf.Max(1, vcount),
            sizeof(uint));
        _guideMapBuffer.SetData(_vertexToGuide);

        _ready = true;
        return true;
    }

    public void RefreshBindPose(Mesh mesh, bool useSmoothNormalsVC)
    {
        if (!IsReady || mesh == null || mesh.vertexCount != _vCount)
            return;
        if (!mesh.isReadable)
            return;
        if (!FillScratch(mesh, useSmoothNormalsVC))
            return;

        for (int g = 0; g < _gCount; g++)
        {
            int vid = _repVid[g];
            Vector3 p = _scratchPos[vid];
            _localP[g] = new float3(p.x, p.y, p.z);
            Vector3 n = _scratchNrm[vid];
            if (n.sqrMagnitude < 1e-8f)
                n = Vector3.up;
            else
                n.Normalize();
            _localN[g] = new float3(n.x, n.y, n.z);
        }
    }

    public void ResetChains()
    {
        _hasHistory = false;
        _simTime = 0f;
        ClearVel();
    }

    public void Step(
        Matrix4x4 localToWorld,
        ShellFurDynamics dyn,
        float furLength,
        float deltaTime,
        Vector3 gravityDir)
    {
        if (!IsReady || dyn == null)
            return;

        Vector3 gDir = gravityDir.sqrMagnitude > 1e-8f ? gravityDir.normalized : Vector3.down;
        float chainLen = dyn.guideChainLength > 1e-8f
            ? dyn.guideChainLength
            : Mathf.Max(furLength, 0.001f);

        float frameDt = deltaTime;
        if (frameDt <= MinDt)
            frameDt = 1f / 60f;
        frameDt = Mathf.Min(frameDt, MaxFrameDt);

        float4x4 m = ToF4x4(localToWorld);
        var pbd = PbdJobParams.From(dyn, gDir, _localCenter, _localRadius, _useSphereCollider, localToWorld);

        if (!_hasHistory)
        {
            SchedulePrepare(m, chainLen).Complete();
            new InitAlongNormalsJob
            {
                pCount = _pCount,
                rootW = _rootW,
                nW = _nW,
                rest = _rest,
                pos = _pos,
                prev = _prev,
                vel = _vel
            }.Schedule(_gCount, JobBatch).Complete();

            _hasHistory = true;
            float sdt = 1f / 60f;
            for (int i = 0; i < SettleSteps; i++)
            {
                _simTime += sdt;
                pbd.SetDt(sdt);
                pbd.time = _simTime;
                SchedulePrepare(m, chainLen).Complete();
                ScheduleSimulate(pbd).Complete();
            }

            ClearVel();
            SchedulePack().Complete();
            _bezierBuffer.SetData(_bezier);
            return;
        }

        int sub = Mathf.Clamp(dyn.pbdSubsteps, 1, 4);
        float sdt2 = frameDt / sub;
        JobHandle handle = default;
        bool chained = false;
        for (int i = 0; i < sub; i++)
        {
            _simTime += sdt2;
            pbd.SetDt(sdt2);
            pbd.time = _simTime;
            JobHandle prep = SchedulePrepare(m, chainLen, chained ? handle : default);
            handle = ScheduleSimulate(pbd, prep);
            chained = true;
        }

        handle = SchedulePack(handle);
        handle.Complete();
        _bezierBuffer.SetData(_bezier);
    }

    public void BindMpb(MaterialPropertyBlock mpb)
    {
        if (mpb == null || !IsReady)
            return;
        mpb.SetBuffer(VertexPbdBezierId, _bezierBuffer);
        mpb.SetBuffer(VertexToGuideId, _guideMapBuffer);
        mpb.SetFloat(UseVertexPbdId, 1f);
        mpb.SetFloat(VertexPbdCountId, _gCount);
        mpb.SetFloat(VertexPbdVertexCountId, _vCount);
    }

    public static void BindDisabledMpb(MaterialPropertyBlock mpb)
    {
        if (mpb == null)
            return;
        EnsureDummy();
        mpb.SetBuffer(VertexPbdBezierId, s_dummyBezier);
        mpb.SetBuffer(VertexToGuideId, s_dummyGuideMap);
        mpb.SetFloat(UseVertexPbdId, 0f);
        mpb.SetFloat(VertexPbdCountId, 1f);
        mpb.SetFloat(VertexPbdVertexCountId, 1f);
    }

    public void DrawGuideChainGizmos(int maxChains = 64)
    {
        if (!IsReady)
            return;
        int step = Mathf.Max(1, _gCount / Mathf.Max(1, maxChains));
        for (int g = 0; g < _gCount; g += step)
            DrawChainGizmo(g);
    }

    public void DrawGuideChainDebugLines(float duration = 0f, int maxChains = 64)
    {
        if (!IsReady)
            return;
        int step = Mathf.Max(1, _gCount / Mathf.Max(1, maxChains));
        Color col = new Color(0.2f, 1f, 1f, 1f);
        for (int g = 0; g < _gCount; g += step)
        {
            for (int k = 1; k < _pCount; k++)
            {
                int i0 = (g * _pCount + k - 1) * 3;
                int i1 = (g * _pCount + k) * 3;
                Debug.DrawLine(
                    new Vector3(_pos[i0], _pos[i0 + 1], _pos[i0 + 2]),
                    new Vector3(_pos[i1], _pos[i1 + 1], _pos[i1 + 2]),
                    col, duration, false);
            }
        }
    }

    public void Dispose()
    {
        ReleaseGpu();
        DisposeNative();
        _repVid = null;
        _vertexToGuide = null;
        _scratchPos = null;
        _scratchNrm = null;
        _scratchCol = null;
        _vCount = 0;
        _gCount = 0;
        _pCount = 0;
        _ready = false;
        _hasHistory = false;
        _builtFrom = null;
    }

    JobHandle SchedulePrepare(float4x4 m, float chainLen, JobHandle dependsOn = default)
    {
        return new PrepareWorldJob
        {
            m = m,
            chainLen = chainLen,
            segs = math.max(_pCount - 1, 1),
            localP = _localP,
            localN = _localN,
            rootW = _rootW,
            nW = _nW,
            rest = _rest
        }.Schedule(_gCount, JobBatch, dependsOn);
    }

    JobHandle ScheduleSimulate(PbdJobParams pbd, JobHandle dependsOn = default)
    {
        return new SimulateGuideJob
        {
            p = pbd,
            pCount = _pCount,
            pos = _pos,
            prev = _prev,
            vel = _vel,
            rest = _rest,
            phase = _phase,
            rootW = _rootW,
            nW = _nW
        }.Schedule(_gCount, JobBatch, dependsOn);
    }

    JobHandle SchedulePack(JobHandle dependsOn = default)
    {
        return new PackBezierJob
        {
            pCount = _pCount,
            pos = _pos,
            bezier = _bezier
        }.Schedule(_gCount, JobBatch, dependsOn);
    }

    bool FillScratch(Mesh mesh, bool useSmoothNormalsVC)
    {
        int n = mesh.vertexCount;
        if (n <= 0)
            return false;

        EnsureList(ref _scratchPos, n);
        mesh.GetVertices(_scratchPos);
        if (_scratchPos.Count != n)
            return false;

        EnsureList(ref _scratchNrm, n);
        if (useSmoothNormalsVC)
        {
            EnsureList(ref _scratchCol, n);
            mesh.GetColors(_scratchCol);
            if (_scratchCol.Count == n)
            {
                _scratchNrm.Clear();
                for (int i = 0; i < n; i++)
                {
                    Color c = _scratchCol[i];
                    var v = new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
                    _scratchNrm.Add(v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.up);
                }
                return true;
            }
        }

        mesh.GetNormals(_scratchNrm);
        if (_scratchNrm.Count != n)
        {
            _scratchNrm.Clear();
            for (int i = 0; i < n; i++)
                _scratchNrm.Add(Vector3.up);
        }
        return true;
    }

    void WeldByPosition(int vcount, out int gcount)
    {
        var map = new Dictionary<(int, int, int), int>(vcount);
        _vertexToGuide = new uint[vcount];
        var rep = new List<int>(vcount / 2 + 8);
        var accN = new List<Vector3>(vcount / 2 + 8);

        for (int vid = 0; vid < vcount; vid++)
        {
            Vector3 p = _scratchPos[vid];
            var key = Quantize(p);
            if (!map.TryGetValue(key, out int gid))
            {
                gid = map.Count;
                map[key] = gid;
                rep.Add(vid);
                accN.Add(_scratchNrm[vid]);
            }
            else
            {
                accN[gid] += _scratchNrm[vid];
            }
            _vertexToGuide[vid] = (uint)gid;
        }

        gcount = map.Count;
        _repVid = rep.ToArray();

        if (_localP.IsCreated)
            _localP.Dispose();
        if (_localN.IsCreated)
            _localN.Dispose();
        _localP = new NativeArray<float3>(gcount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _localN = new NativeArray<float3>(gcount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        for (int g = 0; g < gcount; g++)
        {
            Vector3 p = _scratchPos[_repVid[g]];
            _localP[g] = new float3(p.x, p.y, p.z);
            Vector3 n = accN[g];
            if (n.sqrMagnitude < 1e-8f)
                n = Vector3.up;
            else
                n.Normalize();
            _localN[g] = new float3(n.x, n.y, n.z);
        }
    }

    static (int, int, int) Quantize(Vector3 p)
    {
        const float inv = 1f / WeldEpsilon;
        return (
            Mathf.RoundToInt(p.x * inv),
            Mathf.RoundToInt(p.y * inv),
            Mathf.RoundToInt(p.z * inv));
    }

    static void EnsureList<T>(ref List<T> list, int n)
    {
        if (list == null)
            list = new List<T>(n);
        else if (list.Capacity < n)
            list.Capacity = n;
    }

    static float Hash01(int i)
    {
        uint a = (uint)i;
        a = (a ^ 61u) ^ (a >> 16);
        a *= 9u;
        a = a ^ (a >> 4);
        a *= 0x27d4eb2du;
        a = a ^ (a >> 15);
        return a / 4294967296f;
    }

    static float4x4 ToF4x4(Matrix4x4 m)
    {
        return new float4x4(
            m.m00, m.m01, m.m02, m.m03,
            m.m10, m.m11, m.m12, m.m13,
            m.m20, m.m21, m.m22, m.m23,
            m.m30, m.m31, m.m32, m.m33);
    }

    void DrawChainGizmo(int g)
    {
        Color seg = new Color(0.2f, 1f, 1f, 0.9f);
        Color rootCol = new Color(1f, 0.85f, 0.15f, 1f);
        for (int k = 0; k < _pCount; k++)
        {
            int i = (g * _pCount + k) * 3;
            var p = new Vector3(_pos[i], _pos[i + 1], _pos[i + 2]);
            Gizmos.color = k == 0 ? rootCol : seg;
            Gizmos.DrawSphere(p, 0.006f);
            if (k > 0)
            {
                int j = i - 3;
                Gizmos.color = seg;
                Gizmos.DrawLine(new Vector3(_pos[j], _pos[j + 1], _pos[j + 2]), p);
            }
        }
    }

    static void EnsureDummy()
    {
        if (s_dummyBezier == null)
        {
            s_dummyBezier = new GraphicsBuffer(GraphicsBuffer.Target.Structured, BezierHandles, sizeof(float) * 4);
            s_dummyBezier.SetData(new Vector4[BezierHandles]);
        }
        if (s_dummyGuideMap == null)
        {
            s_dummyGuideMap = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            s_dummyGuideMap.SetData(new uint[1]);
        }
    }

    void ReleaseGpu()
    {
        if (_bezierBuffer != null)
        {
            _bezierBuffer.Dispose();
            _bezierBuffer = null;
        }
        if (_guideMapBuffer != null)
        {
            _guideMapBuffer.Dispose();
            _guideMapBuffer = null;
        }
    }

    void DisposeNative()
    {
        if (_pos.IsCreated) _pos.Dispose();
        if (_prev.IsCreated) _prev.Dispose();
        if (_vel.IsCreated) _vel.Dispose();
        if (_rest.IsCreated) _rest.Dispose();
        if (_phase.IsCreated) _phase.Dispose();
        if (_localP.IsCreated) _localP.Dispose();
        if (_localN.IsCreated) _localN.Dispose();
        if (_rootW.IsCreated) _rootW.Dispose();
        if (_nW.IsCreated) _nW.Dispose();
        if (_bezier.IsCreated) _bezier.Dispose();
    }

    void ClearVel()
    {
        if (!_vel.IsCreated)
            return;
        for (int i = 0; i < _vel.Length; i++)
            _vel[i] = 0f;
    }

    struct PbdJobParams
    {
        public float dt;
        public float time;
        public float damping;
        public float dampMul;
        public float kSpring;
        public float gScale;
        public float3 gDir;
        public float wxd, wzd, wStr, wTurb;
        public float teleportSq;
        public float3 bodyC;
        public float bodyR;
        public float keepOutSq;
        public int useSphere;
        public int iters;

        public void SetDt(float newDt)
        {
            dt = newDt;
            dampMul = math.exp(-math.max(0f, damping) * dt);
        }

        public static PbdJobParams From(
            ShellFurDynamics dyn,
            Vector3 gDir,
            Vector3 localCenter,
            float localRadius,
            bool useSphere,
            Matrix4x4 m)
        {
            var p = new PbdJobParams
            {
                time = 0f,
                damping = math.max(0f, dyn.pbdDamping),
                kSpring = math.clamp(dyn.pbdStiffness, 0.02f, 0.95f) * SpringScale,
                gScale = math.max(0f, dyn.pbdGravity),
                gDir = new float3(gDir.x, gDir.y, gDir.z),
                wxd = math.cos(dyn.pbdWindDirection * math.TORADIANS),
                wzd = -math.sin(dyn.pbdWindDirection * math.TORADIANS),
                wStr = math.max(0f, dyn.pbdWindStrength),
                wTurb = math.max(0f, dyn.pbdWindTurbulence) * math.max(0f, dyn.pbdWindStrength) * 0.45f,
                teleportSq = math.max(0f, dyn.teleportDistance),
                iters = math.clamp(dyn.pbdIterations, 2, 20),
                useSphere = useSphere ? 1 : 0
            };
            p.SetDt(1f / 60f);
            p.teleportSq *= p.teleportSq;
            p.bodyC = (float3)m.MultiplyPoint3x4(localCenter);
            if (useSphere)
            {
                Vector3 ax = m.MultiplyVector(new Vector3(localRadius, 0f, 0f));
                Vector3 ay = m.MultiplyVector(new Vector3(0f, localRadius, 0f));
                Vector3 az = m.MultiplyVector(new Vector3(0f, 0f, localRadius));
                p.bodyR = math.max(ax.magnitude, math.max(ay.magnitude, az.magnitude)) * 0.99f;
            }
            p.keepOutSq = p.bodyR * p.bodyR;
            return p;
        }
    }

    [BurstCompile]
    struct PrepareWorldJob : IJobParallelFor
    {
        public float4x4 m;
        public float chainLen;
        public int segs;
        [ReadOnly] public NativeArray<float3> localP;
        [ReadOnly] public NativeArray<float3> localN;
        public NativeArray<float3> rootW;
        public NativeArray<float3> nW;
        public NativeArray<float> rest;

        public void Execute(int g)
        {
            float3 p = localP[g];
            float3 n = localN[g];
            float3 root = math.transform(m, p);
            float3 nw = math.mul((float3x3)m, n);
            float nLen = math.length(nw);
            nw = nLen > 1e-8f ? nw / nLen : new float3(0f, 1f, 0f);

            float3 tip = math.transform(m, p + n * chainLen);
            float worldLen = math.length(tip - root);
            if (worldLen < 1e-6f)
                worldLen = chainLen;

            rootW[g] = root;
            nW[g] = nw;
            rest[g] = worldLen / math.max(segs, 1);
        }
    }

    [BurstCompile]
    struct InitAlongNormalsJob : IJobParallelFor
    {
        public int pCount;
        [ReadOnly] public NativeArray<float3> rootW;
        [ReadOnly] public NativeArray<float3> nW;
        [ReadOnly] public NativeArray<float> rest;
        [NativeDisableParallelForRestriction] public NativeArray<float> pos;
        [NativeDisableParallelForRestriction] public NativeArray<float> prev;
        [NativeDisableParallelForRestriction] public NativeArray<float> vel;

        public void Execute(int g)
        {
            float3 root = rootW[g];
            float3 n = nW[g];
            float r = rest[g];
            int baseI = g * pCount * 3;
            for (int k = 0; k < pCount; k++)
            {
                float3 q = root + n * (r * k);
                int i = baseI + k * 3;
                pos[i] = prev[i] = q.x;
                pos[i + 1] = prev[i + 1] = q.y;
                pos[i + 2] = prev[i + 2] = q.z;
                vel[i] = vel[i + 1] = vel[i + 2] = 0f;
            }
        }
    }

    [BurstCompile]
    struct SimulateGuideJob : IJobParallelFor
    {
        public PbdJobParams p;
        public int pCount;
        [NativeDisableParallelForRestriction] public NativeArray<float> pos;
        [NativeDisableParallelForRestriction] public NativeArray<float> prev;
        [NativeDisableParallelForRestriction] public NativeArray<float> vel;
        [ReadOnly] public NativeArray<float> rest;
        [ReadOnly] public NativeArray<float> phase;
        [ReadOnly] public NativeArray<float3> rootW;
        [ReadOnly] public NativeArray<float3> nW;

        public void Execute(int g)
        {
            int segs = math.max(pCount - 1, 1);
            float3 root = rootW[g];
            float3 n = nW[g];
            float restLen = rest[g];
            int r = g * pCount * 3;

            float dxr = root.x - pos[r];
            float dyr = root.y - pos[r + 1];
            float dzr = root.z - pos[r + 2];
            if (p.teleportSq > 0f && dxr * dxr + dyr * dyr + dzr * dzr > p.teleportSq)
            {
                for (int k = 0; k < pCount; k++)
                {
                    float3 q = root + n * (restLen * k);
                    int i = r + k * 3;
                    pos[i] = prev[i] = q.x;
                    pos[i + 1] = prev[i + 1] = q.y;
                    pos[i + 2] = prev[i + 2] = q.z;
                    vel[i] = vel[i + 1] = vel[i + 2] = 0f;
                }
                return;
            }

            pos[r] = root.x;
            pos[r + 1] = root.y;
            pos[r + 2] = root.z;

            float ph = phase[g];
            float dt = math.clamp(p.dt, MinDt, MaxFrameDt);

            for (int k = 1; k < pCount; k++)
            {
                int i = r + k * 3;
                float t = k / (float)segs;
                float px = pos[i], py = pos[i + 1], pz = pos[i + 2];

                float gust = 0.55f + 0.45f * math.sin(p.time * 2.1f + px * 0.9f + py * 0.7f + ph);
                float ax = p.wxd * p.wStr * gust * t + math.sin(p.time * 1.9f + py * 2.3f + ph) * p.wTurb * t;
                float ay = math.sin(p.time * 1.3f + px * 1.9f + pz * 1.4f) * p.wTurb * 0.4f * t;
                float az = p.wzd * p.wStr * gust * t + math.cos(p.time * 1.6f + px * 2.1f + ph) * p.wTurb * t;
                ax += p.gDir.x * (p.gScale * t);
                ay += p.gDir.y * (p.gScale * t);
                az += p.gDir.z * (p.gScale * t);

                float rx = root.x + n.x * restLen * k;
                float ry = root.y + n.y * restLen * k;
                float rz = root.z + n.z * restLen * k;
                ax += (rx - px) * p.kSpring;
                ay += (ry - py) * p.kSpring;
                az += (rz - pz) * p.kSpring;

                float vx = (vel[i] + ax * dt) * p.dampMul;
                float vy = (vel[i + 1] + ay * dt) * p.dampMul;
                float vz = (vel[i + 2] + az * dt) * p.dampMul;
                float sp = math.sqrt(vx * vx + vy * vy + vz * vz);
                if (sp > MaxSpeed)
                {
                    float s = MaxSpeed / sp;
                    vx *= s;
                    vy *= s;
                    vz *= s;
                }

                vel[i] = vx;
                vel[i + 1] = vy;
                vel[i + 2] = vz;
                prev[i] = px;
                prev[i + 1] = py;
                prev[i + 2] = pz;
                pos[i] = px + vx * dt;
                pos[i + 1] = py + vy * dt;
                pos[i + 2] = pz + vz * dt;
            }

            int iters = p.iters;
            float3 bodyC = p.bodyC;
            float bodyR = p.bodyR;
            float keepOutSq = p.keepOutSq;
            bool sphere = p.useSphere != 0 && keepOutSq > 1e-12f;

            for (int it = 0; it < iters; it++)
            {
                float rx = pos[r], ry = pos[r + 1], rz = pos[r + 2];
                for (int k = 1; k < pCount; k++)
                {
                    int i = r + k * 3;
                    int j = i - 3;
                    float wa = k == 1 ? 0f : 1f;

                    float dx = pos[i] - pos[j];
                    float dy = pos[i + 1] - pos[j + 1];
                    float dz = pos[i + 2] - pos[j + 2];
                    float d = math.sqrt(dx * dx + dy * dy + dz * dz) + 1e-9f;
                    float corr = (d - restLen) / (d * (wa + 1f));
                    if (wa > 0f)
                    {
                        pos[j] += dx * corr;
                        pos[j + 1] += dy * corr;
                        pos[j + 2] += dz * corr;
                    }
                    pos[i] -= dx * corr;
                    pos[i + 1] -= dy * corr;
                    pos[i + 2] -= dz * corr;

                    dx = pos[i] - rx;
                    dy = pos[i + 1] - ry;
                    dz = pos[i + 2] - rz;
                    float dr = math.sqrt(dx * dx + dy * dy + dz * dz);
                    float maxD = restLen * k;
                    if (dr > maxD && dr > 1e-12f)
                    {
                        float s = maxD / dr;
                        pos[i] = rx + dx * s;
                        pos[i + 1] = ry + dy * s;
                        pos[i + 2] = rz + dz * s;
                    }

                    if (sphere)
                    {
                        dx = pos[i] - bodyC.x;
                        dy = pos[i + 1] - bodyC.y;
                        dz = pos[i + 2] - bodyC.z;
                        float db2 = dx * dx + dy * dy + dz * dz;
                        if (db2 < keepOutSq && db2 > 1e-12f)
                        {
                            float s = bodyR / math.sqrt(db2);
                            pos[i] = bodyC.x + dx * s;
                            pos[i + 1] = bodyC.y + dy * s;
                            pos[i + 2] = bodyC.z + dz * s;
                        }
                    }

                    dx = pos[i] - rx;
                    dy = pos[i + 1] - ry;
                    dz = pos[i + 2] - rz;
                    float along = dx * n.x + dy * n.y + dz * n.z;
                    if (along < 1e-4f)
                    {
                        pos[i] = rx + n.x * 1e-4f;
                        pos[i + 1] = ry + n.y * 1e-4f;
                        pos[i + 2] = rz + n.z * 1e-4f;
                    }
                }
                pos[r] = rx;
                pos[r + 1] = ry;
                pos[r + 2] = rz;
            }

            float invDt = 1f / dt;
            for (int k = 1; k < pCount; k++)
            {
                int i = r + k * 3;
                vel[i] = (pos[i] - prev[i]) * invDt;
                vel[i + 1] = (pos[i + 1] - prev[i + 1]) * invDt;
                vel[i + 2] = (pos[i + 2] - prev[i + 2]) * invDt;
            }
        }
    }

    [BurstCompile]
    struct PackBezierJob : IJobParallelFor
    {
        public int pCount;
        [ReadOnly, NativeDisableParallelForRestriction] public NativeArray<float> pos;
        [NativeDisableParallelForRestriction] public NativeArray<float4> bezier;

        public void Execute(int g)
        {
            int r = g * pCount * 3;
            float rx = pos[r], ry = pos[r + 1], rz = pos[r + 2];
            int o = g * 3;
            int segs = math.max(pCount - 1, 1);

            if (pCount == 4)
            {
                bezier[o] = new float4(pos[r + 3] - rx, pos[r + 4] - ry, pos[r + 5] - rz, 0f);
                bezier[o + 1] = new float4(pos[r + 6] - rx, pos[r + 7] - ry, pos[r + 8] - rz, 0f);
                bezier[o + 2] = new float4(pos[r + 9] - rx, pos[r + 10] - ry, pos[r + 11] - rz, 0f);
                return;
            }

            bezier[o] = SampleRel(g, 1f / 3f, segs, rx, ry, rz);
            bezier[o + 1] = SampleRel(g, 2f / 3f, segs, rx, ry, rz);
            bezier[o + 2] = SampleRel(g, 1f, segs, rx, ry, rz);
        }

        float4 SampleRel(int g, float t, int segs, float rx, float ry, float rz)
        {
            float u = math.saturate(t) * segs;
            int i0 = math.min((int)u, segs);
            int i1 = math.min(i0 + 1, segs);
            float f = u - i0;
            int a = (g * pCount + i0) * 3;
            int b = (g * pCount + i1) * 3;
            return new float4(
                pos[a] + (pos[b] - pos[a]) * f - rx,
                pos[a + 1] + (pos[b + 1] - pos[a + 1]) * f - ry,
                pos[a + 2] + (pos[b + 2] - pos[a + 2]) * f - rz,
                0f);
        }
    }
}
