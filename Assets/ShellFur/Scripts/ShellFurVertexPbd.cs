using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Per-vertex PBD guide hairs (shell-fur HTML).
/// Each mesh vertex owns a short chain (root pinned to the surface).
/// Packed as cubic Bezier handles d1,d2,d3 relative to the world root
/// so shells replace normal extrude with the bent strand.
/// </summary>
public sealed class ShellFurVertexPbd : IDisposable
{
    public const int MinParticles = 2;
    public const int MaxParticles = 8;
    public const int BezierHandles = 3;
    public const int DefaultParticles = 4;

    const float MinDt = 1e-5f;
    const float MaxFrameDt = 0.1f;
    const float MaxSpeed = 30f;
    const float SpringScale = 250f;
    const int SettleSteps = 32;

    static readonly int VertexPbdBezierId = Shader.PropertyToID("_VertexPbdBezier");
    static readonly int UseVertexPbdId = Shader.PropertyToID("_UseVertexPbd");
    static readonly int VertexPbdCountId = Shader.PropertyToID("_VertexPbdCount");

    static GraphicsBuffer s_dummyBezier;

    float[] _pos;
    float[] _prev;
    float[] _vel;
    float[] _rest;
    float[] _phase;
    Vector3[] _localPos;
    Vector3[] _localN;

    Vector4[] _bezier;
    GraphicsBuffer _bezierBuffer;

    int _vCount;
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

    public bool IsReady => _ready && _vCount > 0 && _bezierBuffer != null;
    public int VertexCount => _vCount;
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
        _ready = false;
        _hasHistory = false;
        _simTime = 0f;
        _vCount = 0;
        _pCount = 0;
        _builtFrom = null;

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

        Vector3[] verts = mesh.vertices;
        if (verts == null || verts.Length == 0)
            return false;

        _builtFrom = mesh;
        _vCount = verts.Length;
        _pCount = Mathf.Clamp(nodeCount, MinParticles, MaxParticles);
        int np = _vCount * _pCount;

        _localPos = verts;
        _localN = ReadNormals(mesh, useSmoothNormalsVC, _vCount);
        _pos = new float[np * 3];
        _prev = new float[np * 3];
        _vel = new float[np * 3];
        _rest = new float[_vCount];
        _phase = new float[_vCount];
        _bezier = new Vector4[_vCount * BezierHandles];

        for (int v = 0; v < _vCount; v++)
            _phase[v] = Hash01(v) * (Mathf.PI * 2f);

        Bounds b = mesh.bounds;
        _localCenter = b.center;
        Vector3 e = b.extents;
        float minE = Mathf.Min(e.x, Mathf.Min(e.y, e.z));
        float maxE = Mathf.Max(e.x, Mathf.Max(e.y, e.z));
        _useSmoothNormalsVC = useSmoothNormalsVC;
        _useSphereCollider = minE > 1e-5f && maxE <= minE * 1.2f;
        _localRadius = (e.x + e.y + e.z) * (1f / 3f);

        _bezierBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            Mathf.Max(1, _vCount * BezierHandles),
            sizeof(float) * 4);

        _ready = true;
        return true;
    }

    public void RefreshBindPose(Mesh mesh, bool useSmoothNormalsVC)
    {
        if (!IsReady || mesh == null || mesh.vertexCount != _vCount)
            return;
        if (!mesh.isReadable)
            return;

        _localPos = mesh.vertices;
        _localN = ReadNormals(mesh, useSmoothNormalsVC, _vCount);
    }

    public void ResetChains()
    {
        _hasHistory = false;
        _simTime = 0f;
        if (_vel != null)
            Array.Clear(_vel, 0, _vel.Length);
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

        if (!_hasHistory)
        {
            InitAlongNormals(localToWorld, chainLen);
            _hasHistory = true;
            float sdt = 1f / 60f;
            for (int i = 0; i < SettleSteps; i++)
            {
                _simTime += sdt;
                Substep(localToWorld, dyn, chainLen, gDir, sdt, _simTime);
            }
            Array.Clear(_vel, 0, _vel.Length);
            PackBezier();
            return;
        }

        int sub = Mathf.Clamp(dyn.pbdSubsteps, 1, 4);
        float sdt2 = frameDt / sub;
        for (int i = 0; i < sub; i++)
        {
            _simTime += sdt2;
            Substep(localToWorld, dyn, chainLen, gDir, sdt2, _simTime);
        }

        PackBezier();
    }

    public void BindMpb(MaterialPropertyBlock mpb)
    {
        if (mpb == null || !IsReady)
            return;
        mpb.SetBuffer(VertexPbdBezierId, _bezierBuffer);
        mpb.SetFloat(UseVertexPbdId, 1f);
        mpb.SetFloat(VertexPbdCountId, _vCount);
    }

    public static void BindDisabledMpb(MaterialPropertyBlock mpb)
    {
        if (mpb == null)
            return;
        EnsureDummy();
        mpb.SetBuffer(VertexPbdBezierId, s_dummyBezier);
        mpb.SetFloat(UseVertexPbdId, 0f);
        mpb.SetFloat(VertexPbdCountId, 1f);
    }

    public void DrawGuideChainGizmos(int maxChains = 64)
    {
        if (!IsReady)
            return;
        int step = Mathf.Max(1, _vCount / Mathf.Max(1, maxChains));
        for (int v = 0; v < _vCount; v += step)
            DrawChainGizmo(v);
    }

    public void DrawGuideChainDebugLines(float duration = 0f, int maxChains = 64)
    {
        if (!IsReady)
            return;
        int step = Mathf.Max(1, _vCount / Mathf.Max(1, maxChains));
        Color col = new Color(0.2f, 1f, 1f, 1f);
        for (int v = 0; v < _vCount; v += step)
        {
            for (int k = 1; k < _pCount; k++)
            {
                int i0 = (v * _pCount + k - 1) * 3;
                int i1 = (v * _pCount + k) * 3;
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
        _pos = _prev = _vel = _rest = _phase = null;
        _localPos = _localN = null;
        _bezier = null;
        _vCount = 0;
        _pCount = 0;
        _ready = false;
        _hasHistory = false;
        _builtFrom = null;
    }

    void InitAlongNormals(Matrix4x4 m, float chainLen)
    {
        int p = _pCount;
        for (int v = 0; v < _vCount; v++)
        {
            Vector3 root = m.MultiplyPoint3x4(_localPos[v]);
            Vector3 n = m.MultiplyVector(_localN[v]);
            float nLen = n.magnitude;
            if (nLen < 1e-8f)
                n = Vector3.up;
            else
                n /= nLen;

            float worldLen = (m.MultiplyPoint3x4(_localPos[v] + _localN[v] * chainLen) - root).magnitude;
            if (worldLen < 1e-6f)
                worldLen = chainLen;
            float rest = worldLen / Mathf.Max(p - 1, 1);
            _rest[v] = rest;

            for (int k = 0; k < p; k++)
            {
                int i = (v * p + k) * 3;
                Vector3 q = root + n * (rest * k);
                _pos[i] = _prev[i] = q.x;
                _pos[i + 1] = _prev[i + 1] = q.y;
                _pos[i + 2] = _prev[i + 2] = q.z;
            }
        }
        Array.Clear(_vel, 0, _vel.Length);
    }

    void Substep(
        Matrix4x4 m,
        ShellFurDynamics dyn,
        float chainLen,
        Vector3 gDir,
        float dt,
        float time)
    {
        dt = Mathf.Clamp(dt, MinDt, MaxFrameDt);
        int p = _pCount;
        int segs = Mathf.Max(p - 1, 1);

        float dampMul = Mathf.Exp(-Mathf.Max(0f, dyn.pbdDamping) * dt);
        float kSpring = Mathf.Clamp(dyn.pbdStiffness, 0.02f, 0.95f) * SpringScale;
        float gScale = Mathf.Max(0f, dyn.pbdGravity);

        float wDir = dyn.pbdWindDirection * Mathf.Deg2Rad;
        float wxd = Mathf.Cos(wDir);
        float wzd = -Mathf.Sin(wDir);
        float wStr = Mathf.Max(0f, dyn.pbdWindStrength);
        float wTurb = Mathf.Max(0f, dyn.pbdWindTurbulence) * wStr * 0.45f;

        float teleport = Mathf.Max(0f, dyn.teleportDistance);
        float teleportSq = teleport * teleport;

        Vector3 bodyC = m.MultiplyPoint3x4(_localCenter);
        float bodyR = 0f;
        if (_useSphereCollider)
        {
            Vector3 ax = m.MultiplyVector(new Vector3(_localRadius, 0f, 0f));
            Vector3 ay = m.MultiplyVector(new Vector3(0f, _localRadius, 0f));
            Vector3 az = m.MultiplyVector(new Vector3(0f, 0f, _localRadius));
            bodyR = Mathf.Max(ax.magnitude, Mathf.Max(ay.magnitude, az.magnitude)) * 0.99f;
        }
        float keepOutSq = bodyR * bodyR;

        for (int v = 0; v < _vCount; v++)
        {
            Vector3 root = m.MultiplyPoint3x4(_localPos[v]);
            Vector3 n = m.MultiplyVector(_localN[v]);
            float nLen = n.magnitude;
            if (nLen < 1e-8f)
                n = Vector3.up;
            else
                n /= nLen;

            float worldLen = (m.MultiplyPoint3x4(_localPos[v] + _localN[v] * chainLen) - root).magnitude;
            if (worldLen < 1e-6f)
                worldLen = chainLen;
            float rest = worldLen / segs;
            _rest[v] = rest;

            int r = v * p * 3;
            float oldx = _pos[r], oldy = _pos[r + 1], oldz = _pos[r + 2];
            float dxr = root.x - oldx, dyr = root.y - oldy, dzr = root.z - oldz;
            if (teleportSq > 0f && dxr * dxr + dyr * dyr + dzr * dzr > teleportSq)
            {
                for (int k = 0; k < p; k++)
                {
                    int i = (v * p + k) * 3;
                    Vector3 q = root + n * (rest * k);
                    _pos[i] = _prev[i] = q.x;
                    _pos[i + 1] = _prev[i + 1] = q.y;
                    _pos[i + 2] = _prev[i + 2] = q.z;
                    _vel[i] = _vel[i + 1] = _vel[i + 2] = 0f;
                }
                continue;
            }

            _pos[r] = root.x;
            _pos[r + 1] = root.y;
            _pos[r + 2] = root.z;

            float phase = _phase[v];

            for (int k = 1; k < p; k++)
            {
                int i = (v * p + k) * 3;
                float t = k / (float)segs;
                float px = _pos[i], py = _pos[i + 1], pz = _pos[i + 2];

                float gust = 0.55f + 0.45f * Mathf.Sin(time * 2.1f + px * 0.9f + py * 0.7f + phase);
                float ax = wxd * wStr * gust * t + Mathf.Sin(time * 1.9f + py * 2.3f + phase) * wTurb * t;
                float ay = Mathf.Sin(time * 1.3f + px * 1.9f + pz * 1.4f) * wTurb * 0.4f * t;
                float az = wzd * wStr * gust * t + Mathf.Cos(time * 1.6f + px * 2.1f + phase) * wTurb * t;
                ax += gDir.x * (gScale * t);
                ay += gDir.y * (gScale * t);
                az += gDir.z * (gScale * t);

                float rx = root.x + n.x * rest * k;
                float ry = root.y + n.y * rest * k;
                float rz = root.z + n.z * rest * k;
                ax += (rx - px) * kSpring;
                ay += (ry - py) * kSpring;
                az += (rz - pz) * kSpring;

                float vx = (_vel[i] + ax * dt) * dampMul;
                float vy = (_vel[i + 1] + ay * dt) * dampMul;
                float vz = (_vel[i + 2] + az * dt) * dampMul;
                float sp = Mathf.Sqrt(vx * vx + vy * vy + vz * vz);
                if (sp > MaxSpeed)
                {
                    float s = MaxSpeed / sp;
                    vx *= s;
                    vy *= s;
                    vz *= s;
                }

                _vel[i] = vx;
                _vel[i + 1] = vy;
                _vel[i + 2] = vz;
                _prev[i] = px;
                _prev[i + 1] = py;
                _prev[i + 2] = pz;
                _pos[i] = px + vx * dt;
                _pos[i + 1] = py + vy * dt;
                _pos[i + 2] = pz + vz * dt;
            }
        }

        int iters = Mathf.Clamp(dyn.pbdIterations, 2, 20);
        for (int it = 0; it < iters; it++)
        {
            for (int v = 0; v < _vCount; v++)
            {
                float rest = _rest[v];
                int baseI = v * p;
                int r = baseI * 3;
                float rx = _pos[r], ry = _pos[r + 1], rz = _pos[r + 2];

                Vector3 n = m.MultiplyVector(_localN[v]);
                float nLen = n.magnitude;
                if (nLen > 1e-8f)
                    n /= nLen;
                else
                    n = Vector3.up;

                for (int k = 1; k < p; k++)
                {
                    int i = (baseI + k) * 3;
                    int j = i - 3;
                    float wa = k == 1 ? 0f : 1f;

                    float dx = _pos[i] - _pos[j];
                    float dy = _pos[i + 1] - _pos[j + 1];
                    float dz = _pos[i + 2] - _pos[j + 2];
                    float d = Mathf.Sqrt(dx * dx + dy * dy + dz * dz) + 1e-9f;
                    float corr = (d - rest) / (d * (wa + 1f));
                    if (wa > 0f)
                    {
                        _pos[j] += dx * corr;
                        _pos[j + 1] += dy * corr;
                        _pos[j + 2] += dz * corr;
                    }
                    _pos[i] -= dx * corr;
                    _pos[i + 1] -= dy * corr;
                    _pos[i + 2] -= dz * corr;

                    dx = _pos[i] - rx;
                    dy = _pos[i + 1] - ry;
                    dz = _pos[i + 2] - rz;
                    float dr = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                    float maxD = rest * k;
                    if (dr > maxD && dr > 1e-12f)
                    {
                        float s = maxD / dr;
                        _pos[i] = rx + dx * s;
                        _pos[i + 1] = ry + dy * s;
                        _pos[i + 2] = rz + dz * s;
                    }

                    if (_useSphereCollider && keepOutSq > 1e-12f)
                    {
                        dx = _pos[i] - bodyC.x;
                        dy = _pos[i + 1] - bodyC.y;
                        dz = _pos[i + 2] - bodyC.z;
                        float db2 = dx * dx + dy * dy + dz * dz;
                        if (db2 < keepOutSq && db2 > 1e-12f)
                        {
                            float s = bodyR / Mathf.Sqrt(db2);
                            _pos[i] = bodyC.x + dx * s;
                            _pos[i + 1] = bodyC.y + dy * s;
                            _pos[i + 2] = bodyC.z + dz * s;
                        }
                    }

                    // Keep the strand on the outside of the local tangent plane.
                    dx = _pos[i] - rx;
                    dy = _pos[i + 1] - ry;
                    dz = _pos[i + 2] - rz;
                    float along = dx * n.x + dy * n.y + dz * n.z;
                    if (along < 1e-4f)
                    {
                        _pos[i] = rx + n.x * 1e-4f;
                        _pos[i + 1] = ry + n.y * 1e-4f;
                        _pos[i + 2] = rz + n.z * 1e-4f;
                    }
                }

                _pos[r] = rx;
                _pos[r + 1] = ry;
                _pos[r + 2] = rz;
            }
        }

        float invDt = 1f / dt;
        for (int v = 0; v < _vCount; v++)
        {
            for (int k = 1; k < p; k++)
            {
                int i = (v * p + k) * 3;
                _vel[i] = (_pos[i] - _prev[i]) * invDt;
                _vel[i + 1] = (_pos[i + 1] - _prev[i + 1]) * invDt;
                _vel[i + 2] = (_pos[i + 2] - _prev[i + 2]) * invDt;
            }
        }
    }

    void PackBezier()
    {
        int p = _pCount;
        int segs = Mathf.Max(p - 1, 1);
        for (int v = 0; v < _vCount; v++)
        {
            int r = v * p * 3;
            float rx = _pos[r], ry = _pos[r + 1], rz = _pos[r + 2];
            int o = v * BezierHandles;

            if (p == 4)
            {
                _bezier[o] = new Vector4(_pos[r + 3] - rx, _pos[r + 4] - ry, _pos[r + 5] - rz, 0f);
                _bezier[o + 1] = new Vector4(_pos[r + 6] - rx, _pos[r + 7] - ry, _pos[r + 8] - rz, 0f);
                _bezier[o + 2] = new Vector4(_pos[r + 9] - rx, _pos[r + 10] - ry, _pos[r + 11] - rz, 0f);
            }
            else
            {
                SampleRelative(v, 1f / 3f, segs, rx, ry, rz, out _bezier[o]);
                SampleRelative(v, 2f / 3f, segs, rx, ry, rz, out _bezier[o + 1]);
                SampleRelative(v, 1f, segs, rx, ry, rz, out _bezier[o + 2]);
            }
        }

        _bezierBuffer.SetData(_bezier);
    }

    void SampleRelative(int v, float t, int segs, float rx, float ry, float rz, out Vector4 d)
    {
        float u = Mathf.Clamp01(t) * segs;
        int i0 = Mathf.Min((int)u, segs);
        int i1 = Mathf.Min(i0 + 1, segs);
        float f = u - i0;
        int a = (v * _pCount + i0) * 3;
        int b = (v * _pCount + i1) * 3;
        float x = _pos[a] + (_pos[b] - _pos[a]) * f - rx;
        float y = _pos[a + 1] + (_pos[b + 1] - _pos[a + 1]) * f - ry;
        float z = _pos[a + 2] + (_pos[b + 2] - _pos[a + 2]) * f - rz;
        d = new Vector4(x, y, z, 0f);
    }

    void DrawChainGizmo(int v)
    {
        Color seg = new Color(0.2f, 1f, 1f, 0.9f);
        Color rootCol = new Color(1f, 0.85f, 0.15f, 1f);
        for (int k = 0; k < _pCount; k++)
        {
            int i = (v * _pCount + k) * 3;
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

    static Vector3[] ReadNormals(Mesh mesh, bool useSmoothNormalsVC, int vcount)
    {
        Vector3[] n = null;
        if (useSmoothNormalsVC)
        {
            Color[] cols = mesh.colors;
            if (cols != null && cols.Length == vcount)
            {
                n = new Vector3[vcount];
                for (int i = 0; i < vcount; i++)
                {
                    Color c = cols[i];
                    var v = new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
                    n[i] = v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.up;
                }
                return n;
            }
        }

        n = mesh.normals;
        if (n == null || n.Length != vcount)
        {
            n = new Vector3[vcount];
            for (int i = 0; i < vcount; i++)
                n[i] = Vector3.up;
        }
        return n;
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

    static void EnsureDummy()
    {
        if (s_dummyBezier != null)
            return;
        s_dummyBezier = new GraphicsBuffer(GraphicsBuffer.Target.Structured, BezierHandles, sizeof(float) * 4);
        s_dummyBezier.SetData(new Vector4[BezierHandles]);
    }

    void ReleaseGpu()
    {
        if (_bezierBuffer != null)
        {
            _bezierBuffer.Dispose();
            _bezierBuffer = null;
        }
    }
}
