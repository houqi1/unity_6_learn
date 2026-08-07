using UnityEngine;

/// <summary>
/// Guide-strand secondary motion: root is pinned to the object (strand base),
/// free nodes are integrated with Spring (mass-spring) or Verlet (rope constraints).
/// Each shell/fin height samples the chain for world-space bend offset.
/// </summary>
[System.Serializable]
public class ShellFurDynamics
{
    public enum Mode
    {
        /// <summary>Distance springs + damping between nodes (root fixed).</summary>
        Spring = 0,
        /// <summary>Position Verlet + distance constraints (root fixed).</summary>
        Verlet = 1
    }

    public const int MaxNodes = 9; // root + up to 8 segments

    const float MinDt = 1e-5f;
    const float MaxFrameDt = 0.1f;
    const float FixedStep = 1f / 60f;
    const int MaxSubsteps = 8;

    [Tooltip("Enable guide-strand dynamics (root = object, free tip).")]
    public bool enabled;

    [Tooltip("Spring = mass-spring chain. Verlet = rope with distance constraints.")]
    public Mode mode = Mode.Spring;

    [Header("Chain")]
    [Tooltip("Number of free segments (nodes = segments + 1, root fixed).")]
    [Range(1, 8)]
    public int segments = 4;

    [Tooltip("Total chain rest length = Fur Length × this scale.")]
    [Min(0.1f)]
    public float lengthScale = 1.25f;

    [Tooltip("World gravity on free particles (along gravityDir).")]
    [Min(0f)]
    public float particleGravity = 9.81f;

    [Tooltip("Soft-reset if anchor jumps farther than this.")]
    [Min(0f)]
    public float teleportDistance = 1.5f;

    [Header("Spring")]
    [Tooltip("Distance spring stiffness between neighboring nodes.")]
    [Min(0f)]
    public float springStiffness = 80f;

    [Tooltip("Velocity damping on free nodes.")]
    [Min(0f)]
    public float springDamping = 6f;

    [Tooltip("Mass per free node.")]
    [Min(0.01f)]
    public float nodeMass = 0.15f;

    [Header("Verlet")]
    [Tooltip("Velocity retention loss (higher = less bounce).")]
    [Range(0f, 1f)]
    public float verletDamping = 0.12f;

    [Tooltip("Distance-constraint iterations per substep.")]
    [Range(1, 16)]
    public int verletIterations = 8;

    // Runtime chain (world space)
    readonly Vector3[] _pos = new Vector3[MaxNodes];
    readonly Vector3[] _prev = new Vector3[MaxNodes]; // verlet prev / spring not used
    readonly Vector3[] _vel = new Vector3[MaxNodes];  // spring velocities
    int _nodeCount;
    bool _init;
    bool _hasHistory;
    Vector3 _prevAnchor;
    Mode _lastMode;
    bool _lastEnabled;

    // Packed bend samples: world-space bend offset at each node (for shader lerp by height).
    readonly Vector4[] _bendSamples = new Vector4[MaxNodes];

    public int SampleCount => _nodeCount;
    public Vector4[] BendSamples => _bendSamples;
    public bool HasSamples => _init && _nodeCount >= 2;

    /// <summary>
    /// Step chain once. Packs BendSamples for MPB.
    /// rest gravity is baked into samples so stationary fur matches static gravity look.
    /// </summary>
    public void Evaluate(
        Vector3 anchorPosition,
        Vector3 gravityDirection,
        float gravityStrength,
        float furLength,
        float deltaTime)
    {
        Vector3 gDir = gravityDirection.sqrMagnitude > 1e-8f
            ? gravityDirection.normalized
            : Vector3.down;

        int segs = Mathf.Clamp(segments, 1, MaxNodes - 1);
        int nodes = segs + 1;
        float chainLen = Mathf.Max(furLength * lengthScale, 0.001f);
        float segLen = chainLen / segs;

        if (!enabled)
        {
            if (_lastEnabled)
                ResetState();
            _lastEnabled = false;
            PackStaticGravityOnly(gDir, gravityStrength, furLength, nodes);
            return;
        }

        if (_lastEnabled && (_lastMode != mode || _nodeCount != nodes))
            _init = false;
        _lastEnabled = true;
        _lastMode = mode;

        float frameDt = deltaTime;
        if (frameDt <= MinDt)
            frameDt = FixedStep;
        frameDt = Mathf.Min(frameDt, MaxFrameDt);

        if (!_hasHistory)
        {
            _prevAnchor = anchorPosition;
            _hasHistory = true;
            InitChain(anchorPosition, gDir, nodes, segLen);
            PackBendSamples(anchorPosition, gDir, gravityStrength, furLength, chainLen);
            return;
        }

        Vector3 deltaPos = anchorPosition - _prevAnchor;
        if (teleportDistance > 0f && deltaPos.sqrMagnitude > teleportDistance * teleportDistance)
        {
            InitChain(anchorPosition, gDir, nodes, segLen);
            _prevAnchor = anchorPosition;
            PackBendSamples(anchorPosition, gDir, gravityStrength, furLength, chainLen);
            return;
        }

        if (!_init || _nodeCount != nodes)
            InitChain(anchorPosition, gDir, nodes, segLen);

        // Root follows object (strand base pinned). Free nodes keep world inertia.
        float acc = frameDt;
        int steps = 0;
        while (acc > MinDt && steps < MaxSubsteps)
        {
            float h = Mathf.Min(FixedStep, acc);
            if (mode == Mode.Spring)
                SubstepSpring(anchorPosition, gDir, segLen, h);
            else
                SubstepVerlet(anchorPosition, gDir, segLen, h);
            acc -= h;
            steps++;
        }

        // Ensure root exactly at anchor after integration.
        _pos[0] = anchorPosition;
        _prev[0] = anchorPosition;
        _vel[0] = Vector3.zero;

        _prevAnchor = anchorPosition;
        PackBendSamples(anchorPosition, gDir, gravityStrength, furLength, chainLen);
    }

    public void ResetState()
    {
        _init = false;
        _hasHistory = false;
        _nodeCount = 0;
        _lastEnabled = false;
        for (int i = 0; i < MaxNodes; i++)
        {
            _pos[i] = Vector3.zero;
            _prev[i] = Vector3.zero;
            _vel[i] = Vector3.zero;
            _bendSamples[i] = Vector4.zero;
        }
    }

    void InitChain(Vector3 anchor, Vector3 gDir, int nodes, float segLen)
    {
        _nodeCount = nodes;
        for (int i = 0; i < nodes; i++)
        {
            Vector3 p = anchor + gDir * (segLen * i);
            _pos[i] = p;
            _prev[i] = p;
            _vel[i] = Vector3.zero;
        }
        _init = true;
    }

    void SubstepSpring(Vector3 anchor, Vector3 gDir, float segLen, float dt)
    {
        int n = _nodeCount;
        float mass = Mathf.Max(nodeMass, 0.01f);
        float k = Mathf.Max(springStiffness, 0f);
        float damp = Mathf.Max(springDamping, 0f);
        Vector3 grav = gDir * particleGravity;

        _pos[0] = anchor;
        _vel[0] = Vector3.zero;

        // Accumulate forces on free nodes
        for (int i = 1; i < n; i++)
        {
            Vector3 f = grav * mass;

            // Spring to previous
            f += DistanceSpringForce(_pos[i], _pos[i - 1], segLen, k);
            // Spring to next
            if (i + 1 < n)
                f += DistanceSpringForce(_pos[i], _pos[i + 1], segLen, k);

            f += -damp * _vel[i];

            Vector3 a = f / mass;
            _vel[i] += a * dt;
            _pos[i] += _vel[i] * dt;
        }

        // Re-pin root (neighbor spring may have been applied only to free side)
        _pos[0] = anchor;
        _vel[0] = Vector3.zero;
    }

    static Vector3 DistanceSpringForce(Vector3 self, Vector3 other, float rest, float k)
    {
        Vector3 d = self - other;
        float dist = d.magnitude;
        if (dist < 1e-8f)
            return Vector3.zero;
        // Hooke: pull toward rest length
        float x = dist - rest;
        return -k * x * (d / dist);
    }

    void SubstepVerlet(Vector3 anchor, Vector3 gDir, float segLen, float dt)
    {
        int n = _nodeCount;
        float dt2 = dt * dt;
        float keep = 1f - Mathf.Clamp01(verletDamping);
        keep = Mathf.Pow(Mathf.Clamp01(keep), dt * 60f);
        Vector3 grav = gDir * particleGravity;

        // Integrate free nodes (root will be overwritten)
        for (int i = 1; i < n; i++)
        {
            Vector3 p = _pos[i];
            Vector3 v = (p - _prev[i]) * keep;
            Vector3 next = p + v + grav * dt2;
            _prev[i] = p;
            _pos[i] = next;
        }

        _pos[0] = anchor;
        _prev[0] = anchor;

        int iters = Mathf.Clamp(verletIterations, 1, 16);
        for (int it = 0; it < iters; it++)
        {
            _pos[0] = anchor;
            for (int i = 1; i < n; i++)
            {
                Vector3 delta = _pos[i] - _pos[i - 1];
                float dist = delta.magnitude;
                if (dist < 1e-8f)
                {
                    delta = gDir * 1e-4f;
                    dist = 1e-4f;
                }

                float err = dist - segLen;
                Vector3 corr = (delta / dist) * err;

                if (i == 1)
                {
                    // Root fixed: only move free node
                    _pos[i] -= corr;
                }
                else
                {
                    _pos[i] -= corr * 0.5f;
                    _pos[i - 1] += corr * 0.5f;
                }
            }
            _pos[0] = anchor;
        }
    }

    /// <summary>
    /// bend(h) = (chain - restHang) + staticGravity(h)
    /// restHang = straight hang along gDir; staticGravity matches legacy h² droop.
    /// </summary>
    void PackBendSamples(
        Vector3 anchor,
        Vector3 gDir,
        float gravityStrength,
        float furLength,
        float chainLen)
    {
        int n = _nodeCount;
        float inv = 1f / Mathf.Max(n - 1, 1);
        for (int i = 0; i < n; i++)
        {
            float h = i * inv;
            Vector3 fromRoot = _pos[i] - anchor;
            Vector3 restHang = gDir * (h * chainLen);
            Vector3 dynamicDev = fromRoot - restHang;
            Vector3 staticGrav = gDir * (gravityStrength * h * h * furLength);
            Vector3 bend = dynamicDev + staticGrav;
            _bendSamples[i] = new Vector4(bend.x, bend.y, bend.z, h);
        }
        // Clear unused
        for (int i = n; i < MaxNodes; i++)
            _bendSamples[i] = Vector4.zero;
    }

    void PackStaticGravityOnly(Vector3 gDir, float gravityStrength, float furLength, int nodes)
    {
        _nodeCount = Mathf.Clamp(nodes, 2, MaxNodes);
        float inv = 1f / Mathf.Max(_nodeCount - 1, 1);
        for (int i = 0; i < _nodeCount; i++)
        {
            float h = i * inv;
            Vector3 bend = gDir * (gravityStrength * h * h * furLength);
            _bendSamples[i] = new Vector4(bend.x, bend.y, bend.z, h);
        }
        for (int i = _nodeCount; i < MaxNodes; i++)
            _bendSamples[i] = Vector4.zero;
    }
}
