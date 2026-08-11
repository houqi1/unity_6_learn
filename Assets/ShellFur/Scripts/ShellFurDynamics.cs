using UnityEngine;

/// <summary>
/// Guide-strand dynamics for shell fur.
/// Spring / Verlet / Grass / Bone = different chain sims.
/// Sim uses real chain length (displacement sensitivity only).
/// Shell pack: δ̂ = (chainPos − root) / chainLen, then δ = δ̂ * guideOffsetScale
/// (shape normalized so length no longer couples into shell amplitude).
/// Shader: final = pure extrude + δ; GravityBend off while UseFurChain.
/// </summary>
[System.Serializable]
public class ShellFurDynamics
{
    public enum Mode
    {
        /// <summary>HTML-style follow spring toward previous node / root.</summary>
        Spring = 0,
        /// <summary>Position Verlet + distance constraints.</summary>
        Verlet = 1,
        /// <summary>
        /// Interactive Grass: fixed segment length + positional stiffness toward hang ideal.
        /// Shell base = pure extrude; hang/sway only from this chain (no static GravityBend rest).
        /// </summary>
        Grass = 2,
        /// <summary>
        /// MaxScript bone chain: rigid FK rest tip (gravity-aligned, chain_rest=0) as spring
        /// target, tip velocity spring-damper, hard length constraint (rotate only).
        /// Shell pack same as Grass (pure extrude + chain δ).
        /// </summary>
        Bone = 3
    }

    public const int MaxNodes = 17;
    public const int MinNodes = 2;

    const float MinDt = 1e-5f;
    const float MaxFrameDt = 0.1f;
    const float FixedStep = 1f / 60f;
    const int MaxSubstepsVerlet = 8;
    const int MaxSubstepsBone = 8;

    [Tooltip("Enable guide-strand dynamics.")]
    public bool enabled;

    [Tooltip("Spring = follow spring. Verlet = rope. Grass = HTML hang. Bone = MaxScript tip spring-damper (gravity rest).")]
    public Mode mode = Mode.Spring;

    [Header("Chain")]
    [Tooltip("Nodes including root. Segment length = guide chain length / (nodeCount - 1).")]
    [Range(MinNodes, MaxNodes)]
    public int nodeCount = 2;

    [Tooltip(
        "Simulation chain length in world units (root → tip). Affects segment length and " +
        "how strongly the chain lags / responds to anchor motion — not shell amplitude.\n" +
        "0 = auto: Fur Length × Length Scale. >0 overrides.")]
    [Min(0f)]
    public float guideChainLength = 0f;

    [Tooltip("When Guide Chain Length is 0: sim chain length = furLength × this scale.")]
    [Min(0.01f)]
    public float lengthScale = 1.15f;

    [Header("Shell ↔ chain mapping")]
    [Tooltip(
        "Shell response to the guide shape (world units at full normalized extent).\n" +
        "Pack: δ = ((chainPos − root) / simChainLen) × this scale.\n" +
        "Independent of sim chain length. 0 = no chain influence.\n" +
        "Old coupled look ≈ previous scale × previous chain length (e.g. ~furLength).")]
    [Min(0f)]
    public float guideOffsetScale = 1f;

    [Tooltip("Gravity m/s² in force integration (Spring/Verlet only; ignored in Grass/Bone).")]
    [Min(0f)]
    public float particleGravity = 2f;

    [Tooltip(
        "Spring/Verlet only. ON: gravity is NOT added to forces; rest targets are static gravity shell offsets.\n" +
        "OFF: live g in forces; rest target is previous node / root.\n" +
        "Ignored in Grass/Bone (Bone rest = gravity-aligned rigid FK; shell base pure extrude).")]
    public bool gravityAsRestPose;

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

    [Header("Grass (HTML fixed-length hang)")]
    [Tooltip("Per 1/60s positional blend toward hang ideal (HTML speed slider ~0.01–0.25). Higher = faster recover.")]
    [Range(0.01f, 0.5f)]
    public float grassStiffness = 0.05f;

    [Tooltip("How much tip softens vs root: segmentStiffness *= (1 - i/n * this). HTML uses 0.5.")]
    [Range(0f, 1f)]
    public float grassTipSoftness = 0.5f;

    [Tooltip("Optional lateral wind on hang ideal (world units, like HTML *2). 0 = off.")]
    [Min(0f)]
    public float grassWindStrength = 0f;

    [Tooltip("Wind phase speed (rad-ish per second scaled). HTML uses ~0.02 per frame.")]
    [Min(0f)]
    public float grassWindSpeed = 1.2f;

    [Header("Bone (MaxScript tip spring-damper)")]
    [Tooltip("Per 1/60s: vel += (rigidTip - tip) * stiffness. HTML _stiffness ~0.01–0.5, default 0.1.")]
    [Range(0.01f, 0.5f)]
    public float boneStiffness = 0.1f;

    [Tooltip("Per 1/60s velocity keep: vel *= damping. HTML _damping ~0.5–0.99, default 0.7.")]
    [Range(0.5f, 0.99f)]
    public float boneDamping = 0.7f;

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
    float _chainLen;
    float _windTime;
    Mode _lastMode;
    bool _lastEnabled;
    bool _lastGravityAsRestPose;

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

    /// <param name="shellGravityStrength">Renderer gravity strength (for rest-pose baking).</param>
    /// <param name="shellGravityPower">Renderer gravity falloff power (for rest-pose baking).</param>
    public void Evaluate(
        Vector3 anchorPosition,
        Vector3 gravityDirection,
        float furLength,
        float deltaTime,
        float shellGravityStrength = 0.35f,
        float shellGravityPower = 2f)
    {
        Vector3 gDir = gravityDirection.sqrMagnitude > 1e-8f
            ? gravityDirection.normalized
            : Vector3.down;
        _erectDir = (-gDir).normalized;

        int nodes = Mathf.Clamp(nodeCount, MinNodes, MaxNodes);
        int segs = Mathf.Max(nodes - 1, 1);
        float chainLen = ResolveChainLength(furLength);
        float segLen = chainLen / segs;
        float gStr = Mathf.Max(0f, shellGravityStrength);
        float gPow = Mathf.Max(0.01f, shellGravityPower);

        if (!enabled)
        {
            if (_lastEnabled)
                ResetState();
            _lastEnabled = false;
            _nodeCount = 0;
            _init = false;
            return;
        }

        // Re-init when mode/nodes/rest policy/chain length change so spacing matches.
        bool chainLenChanged = _init && _chainLen > 1e-8f &&
            Mathf.Abs(chainLen - _chainLen) > Mathf.Max(_chainLen, chainLen) * 0.02f;
        if (_lastEnabled && (_lastMode != mode || _nodeCount != nodes ||
            _lastGravityAsRestPose != gravityAsRestPose || chainLenChanged))
            _init = false;
        _lastEnabled = true;
        _lastMode = mode;
        _lastGravityAsRestPose = gravityAsRestPose;
        _chainLen = chainLen;

        float frameDt = deltaTime;
        if (frameDt <= MinDt)
            frameDt = FixedStep;
        frameDt = Mathf.Min(frameDt, MaxFrameDt);

        if (!_hasHistory)
        {
            _prevAnchor = anchorPosition;
            _hasHistory = true;
            InitChainForMode(anchorPosition, gDir, nodes, segLen, furLength, gStr, gPow);
            PackSamples(anchorPosition);
            return;
        }

        Vector3 deltaPos = anchorPosition - _prevAnchor;
        if (teleportDistance > 0f && deltaPos.sqrMagnitude > teleportDistance * teleportDistance)
        {
            InitChainForMode(anchorPosition, gDir, nodes, segLen, furLength, gStr, gPow);
            _prevAnchor = anchorPosition;
            PackSamples(anchorPosition);
            return;
        }

        if (!_init || _nodeCount != nodes)
            InitChainForMode(anchorPosition, gDir, nodes, segLen, furLength, gStr, gPow);

        _pos[0] = anchorPosition;

        if (mode == Mode.Spring)
        {
            StepFollowSpringHtml(anchorPosition, gDir, frameDt, furLength, gStr, gPow);
        }
        else if (mode == Mode.Grass)
        {
            if (NeedsVerletSpacing(segLen))
                InitChainSpaced(anchorPosition, _erectDir, nodes, segLen);
            StepGrassConstraint(anchorPosition, gDir, segLen, frameDt);
        }
        else if (mode == Mode.Bone)
        {
            // Gravity rest: spaced along gDir (HTML chain points down).
            if (NeedsVerletSpacing(segLen))
                InitChainSpaced(anchorPosition, gDir, nodes, segLen);

            // HTML simulate_frame is per display frame (~60Hz). Substep at FixedStep for match.
            float acc = frameDt;
            int steps = 0;
            while (acc > MinDt && steps < MaxSubstepsBone)
            {
                SubstepMaxScriptBone(anchorPosition, gDir, segLen);
                acc -= FixedStep;
                steps++;
            }
        }
        else
        {
            if (NeedsVerletSpacing(segLen))
                InitChainSpaced(anchorPosition, _erectDir, nodes, segLen);

            float acc = frameDt;
            int steps = 0;
            while (acc > MinDt && steps < MaxSubstepsVerlet)
            {
                float h = Mathf.Min(FixedStep, acc);
                SubstepVerlet(anchorPosition, gDir, segLen, h, furLength, gStr, gPow);
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
        _windTime = 0f;
        _chainLen = 0f;
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
        grassStiffness = Mathf.Clamp(grassStiffness, 0.01f, 0.5f);
        grassTipSoftness = Mathf.Clamp01(grassTipSoftness);
        grassWindStrength = Mathf.Max(0f, grassWindStrength);
        grassWindSpeed = Mathf.Max(0f, grassWindSpeed);
        boneStiffness = Mathf.Clamp(boneStiffness, 0.01f, 0.5f);
        boneDamping = Mathf.Clamp(boneDamping, 0.5f, 0.99f);
        guideOffsetScale = Mathf.Max(0f, guideOffsetScale);
        guideChainLength = Mathf.Max(0f, guideChainLength);
        lengthScale = Mathf.Max(0.01f, lengthScale);
    }

    /// <summary>
    /// Effective root→tip chain length (world units).
    /// Absolute guideChainLength wins when &gt; 0; else furLength × lengthScale.
    /// </summary>
    public float ResolveChainLength(float furLength)
    {
        if (guideChainLength > 1e-8f)
            return Mathf.Max(guideChainLength, 0.001f);
        return Mathf.Max(furLength * Mathf.Max(lengthScale, 0.01f), 0.001f);
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

    void InitChainForMode(
        Vector3 anchor,
        Vector3 gDir,
        int nodes,
        float segLen,
        float furLength,
        float gStr,
        float gPow)
    {
        if (mode == Mode.Grass)
            InitChainSpaced(anchor, _erectDir, nodes, segLen);
        else if (mode == Mode.Bone)
            // HTML: chain points down along gravity (rest pose).
            InitChainSpaced(anchor, gDir, nodes, segLen);
        else if (mode == Mode.Spring && gravityAsRestPose)
            InitChainAtGravityRest(anchor, gDir, nodes, furLength, gStr, gPow);
        else if (mode == Mode.Spring)
            InitChainAtRoot(anchor, nodes);
        else
            InitChainSpaced(anchor, _erectDir, nodes, segLen);
    }

    /// <summary>HTML live-g: start on the leader (coincident with root).</summary>
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

    /// <summary>Start at static gravity shell offsets (distance-0 rest when gravityAsRestPose).</summary>
    void InitChainAtGravityRest(
        Vector3 anchor,
        Vector3 gDir,
        int nodes,
        float furLength,
        float gStr,
        float gPow)
    {
        _nodeCount = nodes;
        for (int i = 0; i < nodes; i++)
        {
            float h = nodes <= 1 ? 0f : (float)i / (nodes - 1);
            Vector3 p = anchor + StaticGravityOffset(gDir, h, furLength, gStr, gPow);
            _pos[i] = p;
            _prev[i] = p;
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

    /// <summary>Same world droop as static shell: gDir * strength * pow(h,power) * furLength.</summary>
    static Vector3 StaticGravityOffset(
        Vector3 gDir,
        float height01,
        float furLength,
        float strength,
        float power)
    {
        float h = Mathf.Clamp01(height01);
        float w = Mathf.Pow(h, Mathf.Max(power, 0.01f));
        return gDir * (strength * w * Mathf.Max(furLength, 0.001f));
    }

    bool NeedsVerletSpacing(float segLen)
    {
        if (_nodeCount < 2)
            return true;
        // If all piled on root, space out once for Verlet/Grass rope.
        return (_pos[1] - _pos[0]).sqrMagnitude < (segLen * 0.01f) * (segLen * 0.01f);
    }

    /// <summary>
    /// MaxScript bone chain (exact HTML port, one display frame @ 60Hz semantics):
    /// - Root bone kinematic: always aims gravity (gDir); tip = anchor + gDir * segLen
    /// - Child bones i≥1: rigid FK rest tip (chain_rest=0 ⇒ co-linear with parent)
    /// - vel += (rigidTip - tip) * stiffness; vel *= damping; tip += vel
    /// - base = parent tip; tip reprojected to segLen (rotate only, never stretch)
    /// - aim bone at simulated tip (parentDir for next getRigid)
    /// Shell pack: same PackSamples as Grass (pure extrude + δ).
    /// </summary>
    void SubstepMaxScriptBone(Vector3 anchor, Vector3 gDir, float segLen)
    {
        int n = _nodeCount;
        if (n < 2)
            return;

        segLen = Mathf.Max(segLen, 1e-5f);
        float stiffness = Mathf.Clamp(boneStiffness, 0.01f, 0.5f);
        float damping = Mathf.Clamp(boneDamping, 0.5f, 0.99f);

        // Root joint + root bone (kinematic gravity rest), like chain[0].rot = π/2 down.
        _pos[0] = anchor;
        _vel[0] = Vector3.zero;
        _prev[0] = anchor;

        Vector3 parentDir = gDir.sqrMagnitude > 1e-8f ? gDir.normalized : Vector3.down;
        _pos[1] = _pos[0] + parentDir * segLen;
        _vel[1] = Vector3.zero;
        _prev[1] = _pos[1];

        // Bones 1 .. (n-2): free tips write _pos[2] .. _pos[n-1]
        // (HTML: for i = 1; i < _bone_num; i++) with joint count = bone_num + final tip
        // mapped as nodeCount points and (nodeCount-1) segments.
        for (int bone = 1; bone < n - 1; bone++)
        {
            int baseIdx = bone;
            int tipIdx = bone + 1;

            // getRigid: base = parent tip, rigidRot = parent.rot + chain_rest (0)
            Vector3 rigidBase = _pos[baseIdx];
            Vector3 rigidTip = rigidBase + parentDir * segLen;

            // Spring-damper on tip (HTML simTips / simVels)
            Vector3 tip = _pos[tipIdx];
            Vector3 vel = _vel[tipIdx];

            vel += (rigidTip - tip) * stiffness;
            vel *= damping;
            tip += vel;

            // Length constraint: bone rotates, never stretches
            Vector3 delta = tip - rigidBase;
            float dist = delta.magnitude;
            if (dist > 0.001f)
                tip = rigidBase + delta * (segLen / dist);
            else
                tip = rigidTip;

            _pos[baseIdx] = rigidBase;
            _pos[tipIdx] = tip;
            _vel[tipIdx] = vel;
            _prev[tipIdx] = tip;

            // aim parent for next bone (tsQuatFromTo / atan2)
            Vector3 aim = tip - rigidBase;
            float aimLen = aim.magnitude;
            parentDir = aimLen > 1e-8f ? aim / aimLen : parentDir;
        }

        // Ensure root slot stays pinned after child writes (baseIdx never 0 here).
        _pos[0] = anchor;
        _prev[0] = anchor;
        _vel[0] = Vector3.zero;
    }

    /// <summary>
    /// HTML Interactive Grass resolveConstraints:
    /// 1) pin root to anchor
    /// 2) forward fixed-length projection
    /// 3) positional stiffness toward ideal hang (parent + gDir * segLen)
    /// 4) re-project length
    /// No per-node velocity; hang replaces static GravityBend for shell shape.
    /// </summary>
    void StepGrassConstraint(Vector3 anchor, Vector3 gDir, float segLen, float dt)
    {
        int n = _nodeCount;
        dt = Mathf.Clamp(dt, MinDt, MaxFrameDt);
        segLen = Mathf.Max(segLen, 1e-5f);

        _pos[0] = anchor;
        _vel[0] = Vector3.zero;

        // Forward pass: keep segment lengths (follow-the-leader).
        for (int i = 1; i < n; i++)
        {
            Vector3 delta = _pos[i] - _pos[i - 1];
            float dist = delta.magnitude;
            if (dist > 1e-8f)
                _pos[i] = _pos[i - 1] + delta * (segLen / dist);
            else
                _pos[i] = _pos[i - 1] + gDir * segLen;
        }

        _windTime += grassWindSpeed * dt;
        // Optional wind axis: world X as mild lateral sway (HTML adds to idealX).
        Vector3 windAxis = Vector3.Cross(gDir, Vector3.forward);
        if (windAxis.sqrMagnitude < 1e-6f)
            windAxis = Vector3.Cross(gDir, Vector3.right);
        windAxis.Normalize();

        float bodyStiffnessBase = Mathf.Clamp(grassStiffness, 0.01f, 0.5f) * 2f;
        float tipSoft = Mathf.Clamp01(grassTipSoftness);

        // Hang stiffness + length re-enforce (HTML second loop).
        for (int i = 1; i < n; i++)
        {
            Vector3 ideal = _pos[i - 1] + gDir * segLen;
            if (grassWindStrength > 1e-8f)
            {
                float wind = Mathf.Sin(_windTime + i * 0.1f) * grassWindStrength;
                ideal += windAxis * wind;
            }

            // HTML: segmentStiffness = bodyStiffnessBase * (1 - (i/numSegments)*0.5)
            float segmentStiffness = bodyStiffnessBase * (1f - (i / (float)n) * tipSoft);
            // Time-scale: HTML values are per-frame @ ~60fps.
            float alpha = 1f - Mathf.Pow(1f - Mathf.Clamp01(segmentStiffness), dt * 60f);

            _pos[i] += (ideal - _pos[i]) * alpha;

            Vector3 delta = _pos[i] - _pos[i - 1];
            float dist = delta.magnitude;
            if (dist > 1e-8f)
                _pos[i] = _pos[i - 1] + delta * (segLen / dist);
            else
                _pos[i] = _pos[i - 1] + gDir * segLen;

            _vel[i] = Vector3.zero;
            _prev[i] = _pos[i];
        }
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
    void StepFollowSpringHtml(
        Vector3 anchor,
        Vector3 gDir,
        float dt,
        float furLength,
        float gStr,
        float gPow)
    {
        int n = _nodeCount;
        float mass = Mathf.Max(nodeMass, 0.01f);
        // Live gravity only when NOT baking gravity into rest targets.
        Vector3 g = gravityAsRestPose ? Vector3.zero : gDir * particleGravity;
        dt = Mathf.Clamp(dt, MinDt, MaxFrameDt);

        _pos[0] = anchor;
        _vel[0] = Vector3.zero;

        float maxStretch = maxStretchLength; // 0 = disabled

        for (int i = 1; i < n; i++)
        {
            float h = n <= 1 ? 0f : (float)i / (n - 1);

            // Rest target (distance 0):
            // - gravityAsRestPose: static gravity shell offset (no g in forces)
            // - else: previous node / root (classic HTML)
            Vector3 target = gravityAsRestPose
                ? anchor + StaticGravityOffset(gDir, h, furLength, gStr, gPow)
                : _pos[i - 1];

            float k = TensionAtFreeNode(i, n);
            float damp = VelocityDampingAtFreeNode(i, n);
            float dampStep = Mathf.Pow(damp, dt * 60f);

            // a = k/m * (target - p) [+ g only if live gravity]
            Vector3 a = (target - _pos[i]) * (k / mass) + g;

            _vel[i] += a * dt;
            _vel[i] *= dampStep;
            _pos[i] += _vel[i] * dt;

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

    void SubstepVerlet(
        Vector3 anchor,
        Vector3 gDir,
        float segLen,
        float dt,
        float furLength,
        float gStr,
        float gPow)
    {
        int n = _nodeCount;
        float dt2 = dt * dt;
        float keep = 1f - Mathf.Clamp01(verletDamping);
        keep = Mathf.Pow(Mathf.Clamp01(keep), dt * 60f);
        // Live gravity only when rest pose is not already gravity-baked.
        Vector3 grav = gravityAsRestPose ? Vector3.zero : gDir * particleGravity;

        for (int i = 1; i < n; i++)
        {
            Vector3 p = _pos[i];
            Vector3 v = (p - _prev[i]) * keep;
            Vector3 accel = grav;
            if (bendStiffness > 0f || gravityAsRestPose)
            {
                float h = n <= 1 ? 0f : (float)i / (n - 1);
                Vector3 rest = gravityAsRestPose
                    ? anchor + StaticGravityOffset(gDir, h, furLength, gStr, gPow)
                    : anchor + _erectDir * (segLen * i);
                float kb = bendStiffness > 0f ? bendStiffness : (gravityAsRestPose ? 20f : 0f);
                if (kb > 0f)
                    accel += (rest - p) * (kb / Mathf.Max(nodeMass, 0.01f));
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
    /// Pack additive shell offsets for GPU lookup by layer h ∈ [0,1].
    /// Normalize by sim chain length so shell amplitude is not coupled to it:
    ///   δ̂(h) = (chainWorldPos(h) - root) / chainLen
    ///   δ(h)  = δ̂(h) * guideOffsetScale
    /// Shader samples by shell layer and adds to pure normal extrude.
    /// </summary>
    void PackSamples(Vector3 anchor)
    {
        int n = _nodeCount;
        float invLen = 1f / Mathf.Max(_chainLen, 0.001f);
        float scale = Mathf.Max(0f, guideOffsetScale) * invLen;

        for (int i = 0; i < n; i++)
        {
            float h = n <= 1 ? 0f : (float)i / (n - 1);
            // δ = ((pos - root) / chainLen) * guideOffsetScale
            Vector3 delta = (_pos[i] - anchor) * scale;
            _samples[i] = new Vector4(delta.x, delta.y, delta.z, h);
        }
        for (int i = n; i < MaxNodes; i++)
            _samples[i] = Vector4.zero;
    }
}
