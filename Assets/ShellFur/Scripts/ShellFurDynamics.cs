using UnityEngine;

/// <summary>
/// Guide-strand dynamics (HTML follow spring):
///   force = tension * (target - pos)
///   a = force/mass + gravity
///   v = (v + a) * velocityDamping
///   pos += v
/// Target for free node i = previous node (root for i=1), same as HTML leader.
/// With gravity=0, free nodes settle onto the root (coincident).
/// Shell: base extrude + δ, δ = p(h) - root (follow lag / hang).
/// </summary>
[System.Serializable]
public class ShellFurDynamics
{
    public enum Mode
    {
        /// <summary>HTML-style follow spring toward previous node / root.</summary>
        Spring = 0,
        /// <summary>Position Verlet + distance constraints.</summary>
        Verlet = 1
    }

    public const int MaxNodes = 17;
    public const int MinNodes = 2;

    const float MinDt = 1e-5f;
    const float MaxFrameDt = 0.1f;
    const float FixedStep = 1f / 60f;
    const int MaxSubstepsVerlet = 8;

    [Tooltip("Enable guide-strand dynamics.")]
    public bool enabled;

    [Tooltip("Spring = HTML follow. Verlet = rope constraints.")]
    public Mode mode = Mode.Spring;

    [Header("Chain")]
    [Tooltip("Nodes including root. 2 = one follower (closest to HTML demo).")]
    [Range(MinNodes, MaxNodes)]
    public int nodeCount = 2;

    [Tooltip("Used for Verlet rest length / init spacing. Spring HTML chases root (not this length).")]
    [Min(0.1f)]
    public float lengthScale = 1.15f;

    [Tooltip("Gravity m/s² on free nodes (world units). 0 = no droop.")]
    [Min(0f)]
    public float particleGravity = 2f;

    [Min(0f)]
    public float teleportDistance = 1.5f;

    [Header("Spring (HTML follow, time-scaled)")]
    [Tooltip("Tension at the root / inner free nodes (1/s²). Higher = tighter near the base.")]
    [Min(0.01f)]
    public float followTension = 8f;

    [Tooltip("Tension at the tip / outer nodes (1/s²). Must be ≤ Follow Tension. Lower = more lag at tip.")]
    [Min(0f)]
    public float followTensionMin = 2f;

    [Tooltip("Velocity keep at the tip / outer nodes (per 1/60s, like HTML 0.85). Higher = less swing at tip. Must be < 1.")]
    [Range(0.5f, 0.99f)]
    public float velocityDamping = 0.9f;

    [Tooltip("Velocity keep at the root / inner free nodes. Must be ≤ Velocity Damping. Lower = more responsive / more swing near base.")]
    [Range(0.5f, 0.99f)]
    public float velocityDampingMin = 0.8f;

    [Tooltip("a = F/m + g. Keep 1 unless you want heavier feel.")]
    [Min(0.01f)]
    public float nodeMass = 1f;

    [Tooltip("Max distance from each node to its target (previous node / root). 0 = no limit. Prevents infinite stretch when pulling hard.")]
    [Min(0f)]
    public float maxStretchLength = 0.12f;

    [Header("Verlet (optional)")]
    [Min(0f)]
    public float bendStiffness = 0f;
    [Range(0f, 1f)]
    public float verletDamping = 0.1f;
    [Range(1, 16)]
    public int verletIterations = 8;

    [Header("Debug")]
    public bool showGuideChain;
    public Color guideChainColor = new Color(0.2f, 1f, 1f, 1f);
    [Min(0.0001f)]
    public float guideNodeRadius = 0.008f;

    readonly Vector3[] _pos = new Vector3[MaxNodes];
    readonly Vector3[] _prev = new Vector3[MaxNodes];
    readonly Vector3[] _vel = new Vector3[MaxNodes];
    readonly Vector4[] _samples = new Vector4[MaxNodes];

    int _nodeCount;
    bool _init;
    bool _hasHistory;
    Vector3 _prevAnchor;
    Vector3 _erectDir = Vector3.up;
    Mode _lastMode;
    bool _lastEnabled;

    public int SampleCount => _nodeCount;
    public Vector4[] BendSamples => _samples;
    public bool HasSamples => enabled && _init && _nodeCount >= 2;
    public Vector3 ErectDirection => _erectDir;
    public int NodeCount => _init ? _nodeCount : 0;

    public bool TryGetNodeWorldPosition(int index, out Vector3 worldPos)
    {
        if (!_init || index < 0 || index >= _nodeCount)
        {
            worldPos = default;
            return false;
        }
        worldPos = _pos[index];
        return true;
    }

    public int CopyNodeWorldPositions(Vector3[] dst)
    {
        if (!_init || dst == null || _nodeCount <= 0)
            return 0;
        int n = Mathf.Min(_nodeCount, dst.Length);
        for (int i = 0; i < n; i++)
            dst[i] = _pos[i];
        return n;
    }

    public void DrawGuideChainGizmos()
    {
        if (!showGuideChain || !_init || _nodeCount < 2)
            return;

        Color seg = guideChainColor;
        Color rootCol = new Color(1f, 0.85f, 0.15f, 1f);
        float r = Mathf.Max(guideNodeRadius, 0.0001f);

        for (int i = 0; i < _nodeCount; i++)
        {
            Gizmos.color = i == 0 ? rootCol : seg;
            Gizmos.DrawSphere(_pos[i], r);
            if (i > 0)
            {
                Gizmos.color = seg;
                Gizmos.DrawLine(_pos[i - 1], _pos[i]);
            }
        }
    }

    public void DrawGuideChainDebugLines(float duration = 0f)
    {
        if (!showGuideChain || !_init || _nodeCount < 2)
            return;
        for (int i = 1; i < _nodeCount; i++)
            Debug.DrawLine(_pos[i - 1], _pos[i], guideChainColor, duration, false);
    }

    public void Evaluate(
        Vector3 anchorPosition,
        Vector3 gravityDirection,
        float furLength,
        float deltaTime)
    {
        Vector3 gDir = gravityDirection.sqrMagnitude > 1e-8f
            ? gravityDirection.normalized
            : Vector3.down;
        _erectDir = (-gDir).normalized;

        int nodes = Mathf.Clamp(nodeCount, MinNodes, MaxNodes);
        int segs = Mathf.Max(nodes - 1, 1);
        float chainLen = Mathf.Max(furLength * lengthScale, 0.001f);
        float segLen = chainLen / segs;

        if (!enabled)
        {
            if (_lastEnabled)
                ResetState();
            _lastEnabled = false;
            _nodeCount = 0;
            _init = false;
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
            // HTML: follower starts on the leader.
            InitChainAtRoot(anchorPosition, nodes);
            PackSamples(anchorPosition);
            return;
        }

        Vector3 deltaPos = anchorPosition - _prevAnchor;
        if (teleportDistance > 0f && deltaPos.sqrMagnitude > teleportDistance * teleportDistance)
        {
            InitChainAtRoot(anchorPosition, nodes);
            _prevAnchor = anchorPosition;
            PackSamples(anchorPosition);
            return;
        }

        if (!_init || _nodeCount != nodes)
            InitChainAtRoot(anchorPosition, nodes);

        _pos[0] = anchorPosition;

        if (mode == Mode.Spring)
            StepFollowSpringHtml(anchorPosition, gDir, frameDt);
        else
        {
            // Verlet still uses spaced rest along erect for rope length.
            if (NeedsVerletSpacing(segLen))
                InitChainSpaced(anchorPosition, _erectDir, nodes, segLen);

            float acc = frameDt;
            int steps = 0;
            while (acc > MinDt && steps < MaxSubstepsVerlet)
            {
                float h = Mathf.Min(FixedStep, acc);
                SubstepVerlet(anchorPosition, gDir, segLen, h);
                acc -= h;
                steps++;
            }
        }

        _pos[0] = anchorPosition;
        _prev[0] = anchorPosition;
        _prevAnchor = anchorPosition;
        PackSamples(anchorPosition);
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
            _samples[i] = Vector4.zero;
        }
    }

    public void ValidateNodeCount()
    {
        nodeCount = Mathf.Clamp(nodeCount, MinNodes, MaxNodes);
        followTension = Mathf.Max(0.01f, followTension);
        followTensionMin = Mathf.Clamp(followTensionMin, 0f, followTension);
        velocityDamping = Mathf.Clamp(velocityDamping, 0.5f, 0.99f);
        velocityDampingMin = Mathf.Clamp(velocityDampingMin, 0.5f, velocityDamping);
    }

    /// <summary>
    /// t=0 at first free node (inner), t=1 at tip (outer).
    /// </summary>
    static float FreeNodeT(int freeIndex, int nodeCountInclRoot)
    {
        int freeCount = Mathf.Max(nodeCountInclRoot - 1, 1);
        return freeCount <= 1 ? 1f : (freeIndex - 1) / (float)(freeCount - 1);
    }

    /// <summary>Tension: high at root → low at tip.</summary>
    float TensionAtFreeNode(int freeIndex, int nodeCountInclRoot)
    {
        float kMax = Mathf.Max(followTension, 0.01f);
        float kMin = Mathf.Clamp(followTensionMin, 0f, kMax);
        float t = FreeNodeT(freeIndex, nodeCountInclRoot);
        return Mathf.Lerp(kMax, kMin, t);
    }

    /// <summary>Velocity keep: low at root → high at tip (max = velocityDamping).</summary>
    float VelocityDampingAtFreeNode(int freeIndex, int nodeCountInclRoot)
    {
        float dMax = Mathf.Clamp(velocityDamping, 0.5f, 0.99f);
        float dMin = Mathf.Clamp(velocityDampingMin, 0.5f, dMax);
        float t = FreeNodeT(freeIndex, nodeCountInclRoot);
        return Mathf.Lerp(dMin, dMax, t);
    }

    /// <summary>HTML: start on the leader (coincident with root).</summary>
    void InitChainAtRoot(Vector3 anchor, int nodes)
    {
        _nodeCount = nodes;
        for (int i = 0; i < nodes; i++)
        {
            _pos[i] = anchor;
            _prev[i] = anchor;
            _vel[i] = Vector3.zero;
        }
        _init = true;
    }

    void InitChainSpaced(Vector3 anchor, Vector3 erect, int nodes, float segLen)
    {
        _nodeCount = nodes;
        for (int i = 0; i < nodes; i++)
        {
            Vector3 p = anchor + erect * (segLen * i);
            _pos[i] = p;
            _prev[i] = p;
            _vel[i] = Vector3.zero;
        }
        _init = true;
    }

    bool NeedsVerletSpacing(float segLen)
    {
        if (_nodeCount < 2)
            return true;
        // If all piled on root, space out once for Verlet rope.
        return (_pos[1] - _pos[0]).sqrMagnitude < (segLen * 0.01f) * (segLen * 0.01f);
    }

    /// <summary>
    /// HTML-style follow with correct time scale (world units / seconds):
    ///   a = (k/m) * (target - pos) + g
    ///   v += a * dt
    ///   v *= Pow(damping, dt * 60)   // damping is "keep factor per 1/60s" like HTML 0.85
    ///   pos += v * dt
    ///
    /// Previous bug: frame-based v=(v+a)*d; pos+=v treated a as per-frame, so the same
    /// HTML numbers were far too stiff in meter-scale / high-FPS Unity, and the slider
    /// floor could not go soft enough.
    ///
    /// k = followTension has unit 1/s² (soft fur often 1–20). Smaller k ⇒ more lag.
    /// Target = previous node (root for i=1). g=0 ⇒ settle on target (coincident with root).
    /// </summary>
    void StepFollowSpringHtml(Vector3 anchor, Vector3 gDir, float dt)
    {
        int n = _nodeCount;
        float mass = Mathf.Max(nodeMass, 0.01f);
        Vector3 g = gDir * particleGravity;
        dt = Mathf.Clamp(dt, MinDt, MaxFrameDt);

        _pos[0] = anchor;
        _vel[0] = Vector3.zero;

        float maxStretch = maxStretchLength; // 0 = disabled

        for (int i = 1; i < n; i++)
        {
            Vector3 target = _pos[i - 1]; // leader = previous node
            // Inner: higher tension, lower velocity-keep; tip: lower tension, higher keep (max).
            float k = TensionAtFreeNode(i, n);
            float damp = VelocityDampingAtFreeNode(i, n);
            // HTML keep factor per 1/60s → scale with dt.
            float dampStep = Mathf.Pow(damp, dt * 60f);

            // a = k/m * (target - p) + g   (k in 1/s²)
            Vector3 a = (target - _pos[i]) * (k / mass) + g;

            _vel[i] += a * dt;
            _vel[i] *= dampStep;
            _pos[i] += _vel[i] * dt;

            // Max stretch: clamp |p - target| ≤ maxStretch (hard length limit).
            if (maxStretch > 1e-8f)
                ClampToMaxStretch(ref _pos[i], ref _vel[i], target, maxStretch);
        }
    }

    /// <summary>
    /// Project position onto sphere of radius maxLen around target.
    /// Removes outward velocity along the stretch axis so it does not re-penetrate next frame.
    /// </summary>
    static void ClampToMaxStretch(ref Vector3 pos, ref Vector3 vel, Vector3 target, float maxLen)
    {
        Vector3 d = pos - target;
        float dist = d.magnitude;
        if (dist <= maxLen || dist < 1e-8f)
            return;

        Vector3 dir = d / dist;
        pos = target + dir * maxLen;

        // Kill component of velocity pointing further away from target.
        float vOut = Vector3.Dot(vel, dir);
        if (vOut > 0f)
            vel -= dir * vOut;
    }

    void SubstepVerlet(Vector3 anchor, Vector3 gDir, float segLen, float dt)
    {
        int n = _nodeCount;
        float dt2 = dt * dt;
        float keep = 1f - Mathf.Clamp01(verletDamping);
        keep = Mathf.Pow(Mathf.Clamp01(keep), dt * 60f);
        Vector3 grav = gDir * particleGravity;

        for (int i = 1; i < n; i++)
        {
            Vector3 p = _pos[i];
            Vector3 v = (p - _prev[i]) * keep;
            Vector3 accel = grav;
            if (bendStiffness > 0f)
            {
                Vector3 rest = anchor + _erectDir * (segLen * i);
                accel += (rest - p) * (bendStiffness / Mathf.Max(nodeMass, 0.01f));
            }
            Vector3 next = p + v + accel * dt2;
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
                    delta = _erectDir * 1e-4f;
                    dist = 1e-4f;
                }
                float err = dist - segLen;
                Vector3 corr = (delta / dist) * err;
                if (i == 1)
                    _pos[i] -= corr;
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
    /// Shell offset δ(h) = chainSample(h) - root.
    /// = follow lag / hang relative to leader. Zero when coincident with root.
    /// </summary>
    void PackSamples(Vector3 anchor)
    {
        int n = _nodeCount;
        for (int i = 0; i < n; i++)
        {
            float h = n <= 1 ? 0f : (float)i / (n - 1);
            Vector3 delta = _pos[i] - anchor;
            _samples[i] = new Vector4(delta.x, delta.y, delta.z, h);
        }
        for (int i = n; i < MaxNodes; i++)
            _samples[i] = Vector4.zero;
    }
}
