using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 指定体积光跟随的那一盏定向光。forward = 光线传播方向。
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class VolumetricLightSource : MonoBehaviour
{
    static readonly List<VolumetricLightSource> s_Active = new List<VolumetricLightSource>();
    static bool s_WarnedMultiple;

    [Tooltip("必须是 Directional Light。可与本物体上的 Light 相同。方向跟这盏灯走。")]
    public Light specifiedLight;

    [ColorUsage(false, true)]
    [Tooltip("体积光颜色，只影响空气光柱，不改场景灯对物体的照明。")]
    public Color color = new Color(1f, 0.96f, 0.85f, 1f);

    [Range(0f, 8f)]
    [Tooltip("体积光强度。")]
    public float intensity = 0.6f;

    [Header("Cylinder Volume")]
    [Tooltip("开启后，体积光只出现在圆柱范围内。轴沿指定灯的 forward。")]
    public bool useCylinderVolume;

    [Min(0.01f)]
    [Tooltip("圆柱半径（世界单位）。")]
    public float cylinderRadius = 8f;

    [Min(0.01f)]
    [Tooltip("圆柱高度，沿光线传播方向。")]
    public float cylinderHeight = 40f;

    [Min(0f)]
    [Tooltip("圆柱边缘软化宽度。0 为硬边。")]
    public float cylinderEdgeFade = 1f;

    public Transform VolumeTransform =>
        specifiedLight != null ? specifiedLight.transform : transform;

    void Reset()
    {
        specifiedLight = GetComponent<Light>();
        if (specifiedLight != null)
        {
            color = specifiedLight.color;
            color.a = 1f;
        }
        intensity = 0.6f;
        cylinderRadius = 8f;
        cylinderHeight = 40f;
        cylinderEdgeFade = 1f;
    }

    void OnValidate()
    {
        cylinderRadius = Mathf.Max(0.01f, cylinderRadius);
        cylinderHeight = Mathf.Max(0.01f, cylinderHeight);
        cylinderEdgeFade = Mathf.Max(0f, cylinderEdgeFade);
    }

    void OnEnable()
    {
        if (!s_Active.Contains(this))
            s_Active.Add(this);
    }

    void OnDisable()
    {
        s_Active.Remove(this);
    }

    public static VolumetricLightSource FindActive()
    {
        VolumetricLightSource first = null;
        int count = 0;

        for (int i = s_Active.Count - 1; i >= 0; i--)
        {
            var src = s_Active[i];
            if (src == null)
            {
                s_Active.RemoveAt(i);
                continue;
            }

            if (!src.isActiveAndEnabled)
                continue;

            count++;
            if (first == null)
                first = src;
        }

        if (count > 1 && !s_WarnedMultiple)
        {
            s_WarnedMultiple = true;
            Debug.LogWarning("[VolumetricLight] 场景里有多个 VolumetricLightSource，只使用第一个启用的。");
        }

        return first;
    }

    void OnDrawGizmos()
    {
        var xform = VolumeTransform;
        Vector3 origin = xform.position;
        Vector3 dir = xform.forward;
        Gizmos.color = new Color(color.r, color.g, color.b, 0.9f);
        Gizmos.DrawRay(origin, dir * 8f);
        Gizmos.DrawSphere(origin + dir * 8f, 0.15f);

        if (useCylinderVolume)
            DrawWireCylinder(origin, dir, cylinderRadius, cylinderHeight);
    }

    static void DrawWireCylinder(Vector3 origin, Vector3 dir, float radius, float height)
    {
        if (dir.sqrMagnitude < 1e-8f)
            return;
        dir.Normalize();

        Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
        Vector3 right = Vector3.Cross(dir, up).normalized;
        up = Vector3.Cross(right, dir);

        Vector3 a = origin;
        Vector3 b = origin + dir * height;
        const int sides = 24;
        Vector3 prevA = a + right * radius;
        Vector3 prevB = b + right * radius;
        for (int i = 1; i <= sides; i++)
        {
            float ang = i * Mathf.PI * 2f / sides;
            Vector3 offset = right * (Mathf.Cos(ang) * radius) + up * (Mathf.Sin(ang) * radius);
            Vector3 pa = a + offset;
            Vector3 pb = b + offset;
            Gizmos.DrawLine(prevA, pa);
            Gizmos.DrawLine(prevB, pb);
            if (i % 6 == 0)
                Gizmos.DrawLine(pa, pb);
            prevA = pa;
            prevB = pb;
        }
    }
}
