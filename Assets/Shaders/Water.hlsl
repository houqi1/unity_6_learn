#ifndef WATER_HLSL
#define WATER_HLSL

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

// SSPR 输出（由 ScreenSpacePlanarReflectionFeature 写入）
// 采样器名必须与纹理名匹配（DX11 要求），不能复用 sampler_CameraOpaqueTexture
TEXTURE2D(_SSPR_ColorRT);
SAMPLER(sampler_SSPR_ColorRT);
float _SSPR_Enabled;

// -----------------------------------------------------------------------------
TEXTURE2D(_NormalMap);
SAMPLER(sampler_NormalMap);

CBUFFER_START(UnityPerMaterial)
    half4  _ShallowColor;
    half4  _DeepColor;
    half   _ColorDepth;
    half   _Alpha;
    half   _NormalStrength;
    float4 _NormalTiling;
    float4 _NormalSpeedA;
    float4 _NormalSpeedB;
    half   _RippleStrength;
    half   _RippleCellSize;
    half   _RippleMaxRadius;
    half   _RippleSpeed;
    half   _RippleExpandEase;
    half   _RippleSharpness;
    half   _RippleAmplitude;
    half   _RippleAreaRadius;
    half   _ReflectionStrength;
    half   _ReflectionFresnelPower;
    half   _ReflectionFresnelBias;
    half   _ReflectionDistortion;
    half   _ReflectionScreenEdgeFade;
    half   _RefractionStrength;
    half   _RefractionDistortion;
    half4  _SpecularColor;
    half   _Smoothness;
    half   _SpecularIntensity;
    half   _EdgeFade;
    half4  _FoamColor;
    half   _FoamWidth;
    half   _FoamIntensity;
CBUFFER_END

// -----------------------------------------------------------------------------
half3 DecodeDualChannelNormal(half4 packedNormal, half scale)
{
#if defined(_NORMALPACK_AG)
    return UnpackNormalAG(packedNormal, scale);
#elif defined(_NORMALPACK_RGB)
    return UnpackNormalRGB(packedNormal, scale);
#else
    half3 n;
    n.xy = packedNormal.rg * 2.0h - 1.0h;
    n.z = max(1.0e-16h, sqrt(1.0h - saturate(dot(n.xy, n.xy))));
    n.xy *= scale;
    return n;
#endif
}

half3 SampleWaterNormalTS(float2 uv)
{
    float2 uvA = uv * _NormalTiling.xy + _Time.y * _NormalSpeedA.xy;
    float2 uvB = uv * _NormalTiling.zw + _Time.y * _NormalSpeedB.xy;
    half3 nA = DecodeDualChannelNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvA), _NormalStrength);
    half3 nB = DecodeDualChannelNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvB), _NormalStrength);
    return normalize(half3(nA.xy + nB.xy, nA.z * nB.z));
}

// -----------------------------------------------------------------------------
// 摄像机周围随机波纹（仅扰动法线）
// - 以相机 XZ 为中心取世界固定网格
// - 每个格子内一个伪随机落点，按生命周期向外扩张的高斯环
// - 输出 XZ 高度梯度，用于改 normalTS / normalWS
// -----------------------------------------------------------------------------
float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float2 Hash22(float2 p)
{
    float n = Hash21(p);
    return float2(n, Hash21(p + float2(n, 19.19)));
}

// gradXZ = ∂H/∂x, ∂H/∂z（已乘 Strength）
void SampleCameraRipples(float3 positionWS, out half2 gradXZ)
{
    gradXZ = half2(0, 0);

    half strength = _RippleStrength;
    if (strength <= 1e-4h)
        return;

    float2 pos = positionWS.xz;
    float2 cam = _WorldSpaceCameraPos.xz;

    float distToCam = length(pos - cam);
    float areaR = max((float)_RippleAreaRadius, 1.0);
    half areaFade = (half)(1.0 - smoothstep(areaR * 0.55, areaR, distToCam));
    if (areaFade <= 1e-4h)
        return;

    float cell = max((float)_RippleCellSize, 0.5);
    float2 origin = floor(cam / cell);
    float time = _Time.y * max((float)_RippleSpeed, 1e-4);
    float maxR = max((float)_RippleMaxRadius, 0.1);
    float k = max((float)_RippleSharpness, 0.1);
    float ampScale = (float)_RippleAmplitude * (float)areaFade;
    // ease-out：radius = maxR * (1 - (1-life)^ease)，ease=1 匀速，>1 先快后慢
    float ease = max((float)_RippleExpandEase, 1.0);

    // 3×3 邻格：跟随相机移动，远处格子自然进出
    [unroll]
    for (int j = -1; j <= 1; ++j)
    {
        [unroll]
        for (int i = -1; i <= 1; ++i)
        {
            float2 id = origin + float2(i, j);
            float2 rnd = Hash22(id);
            float rndLife = Hash21(id + float2(17.13, 91.7));

            float2 center = (id + rnd) * cell;

            float life = frac(time + rndLife * 7.0);
            float fade = life * (1.0 - life) * 4.0;
            // 先快后慢的扩散进度
            float expand = 1.0 - pow(1.0 - life, ease);
            float radius = expand * maxR;

            float2 d = pos - center;
            float dist = length(d);
            float ring = dist - radius;

            float g = exp(-ring * ring * k);
            float wave = g * fade * ampScale;

            // ∂wave/∂dist = wave * (-2 · ring · k)
            if (dist > 1e-5)
            {
                float dWave_ddist = wave * (-2.0 * ring * k);
                float2 dir = d / dist;
                gradXZ += (half2)(dir * dWave_ddist);
            }
        }
    }

    gradXZ *= strength;
}

// 粒子落水涟漪：CPU 环形缓冲，xy=世界 XZ，z=startTime，w=强度
#define WATER_PARTICLE_RIPPLE_MAX 32
float4 _ParticleRipples[WATER_PARTICLE_RIPPLE_MAX];
float  _ParticleRippleCount;
float  _ParticleRippleDuration;
float  _ParticleRippleMaxRadius;
float  _ParticleRippleStrength;
float  _ParticleRippleTime;

void SampleParticleRipples(float3 positionWS, inout half2 gradXZ)
{
    int count = (int)clamp(_ParticleRippleCount, 0.0, (float)WATER_PARTICLE_RIPPLE_MAX);
    if (count <= 0)
        return;

    float duration = max((float)_ParticleRippleDuration, 1e-3);
    float maxR = max((float)_ParticleRippleMaxRadius, 0.1);
    float ease = max((float)_RippleExpandEase, 1.0);
    float k = max((float)_RippleSharpness, 0.1);
    float ampScale = max((float)_RippleAmplitude, 0.2) * max((float)_ParticleRippleStrength, 0.0);
    float now = _ParticleRippleTime;
    float2 pos = positionWS.xz;

    [loop]
    for (int i = 0; i < WATER_PARTICLE_RIPPLE_MAX; ++i)
    {
        if (i >= count)
            break;

        float4 ripple = _ParticleRipples[i];
        if (ripple.w <= 1e-4)
            continue;

        float life = saturate((now - ripple.z) / duration);
        if (life <= 0.0 || life >= 1.0)
            continue;

        float fade = life * (1.0 - life) * 4.0;
        float expand = 1.0 - pow(1.0 - life, ease);
        float radius = expand * maxR;

        float2 d = pos - ripple.xy;
        float dist = length(d);
        float ring = dist - radius;
        float g = exp(-ring * ring * k);
        float wave = g * fade * ampScale * (float)ripple.w;

        if (dist > 1e-5)
        {
            float dWave_ddist = wave * (-2.0 * ring * k);
            gradXZ += (half2)((d / dist) * dWave_ddist);
        }
    }
}

float2 ProjectiveToUV(float4 projective)
{
    return projective.xy / max(projective.w, 1e-5);
}

// =============================================================================
// SSPR 反射采样
//
// SSPR 已经在 Compute 里做完「场景点镜像 → 重投影」：
//   ColorRT[mirroredScreenUV] = Opaque[sourceUV]
//
// 水面片元只做：
//   uv = screenUV + bump 扰动
//   color = Sample(_SSPR_ColorRT, uv)
//
// 这才是 SSPR 的正确用法；不要在水面里自己算 reflect 投影。
// =============================================================================
half4 SampleSSPR(float2 screenUV, half3 bumpTS, float4 screenPos)
{
    // 扰动加在投影坐标上再除 w（FX/Water 风格，透视正确）
    float2 offset = bumpTS.xy * _ReflectionDistortion / max(screenPos.w, 1e-5);
    float2 uv = saturate(screenUV + offset);

    half4 sspr = SAMPLE_TEXTURE2D(_SSPR_ColorRT, sampler_SSPR_ColorRT, uv);

    // 边缘淡出
    float2 edge = abs(uv * 2.0 - 1.0);
    float border = max(edge.x, edge.y);
    half edgeFade = 1.0h - smoothstep(1.0h - _ReflectionScreenEdgeFade, 1.0h, border);
    sspr.a *= edgeFade;

    return sspr;
}

// 折射：当前 screenUV + bump
float2 BuildRefractionUV(float4 screenPos, half3 bumpTS)
{
    float4 refr = screenPos;
    refr.xy += bumpTS.xy * _RefractionDistortion;
    return ProjectiveToUV(refr);
}

// -----------------------------------------------------------------------------
struct WaterAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct WaterVaryings
{
    float4 positionCS  : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float3 positionWS  : TEXCOORD1;
    float3 normalWS    : TEXCOORD2;
    float4 tangentWS   : TEXCOORD3;
    float  fogFactor   : TEXCOORD4;
    float4 screenPos   : TEXCOORD5;
    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord : TEXCOORD6;
    #endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

#if defined(WATER_FORWARD_PASS)

WaterVaryings WaterVert(WaterAttributes input)
{
    WaterVaryings o = (WaterVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs   nrmInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    o.positionCS = posInputs.positionCS;
    o.positionWS = posInputs.positionWS;
    o.uv         = input.uv;
    o.normalWS   = nrmInputs.normalWS;
    real sign    = input.tangentOS.w * GetOddNegativeScale();
    o.tangentWS  = float4(nrmInputs.tangentWS.xyz, sign);
    o.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);
    o.screenPos  = ComputeScreenPos(posInputs.positionCS);

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    o.shadowCoord = GetShadowCoord(posInputs);
    #endif
    return o;
}

half4 WaterFrag(WaterVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    float3 positionWS = i.positionWS;
    half3  viewDirWS  = GetWorldSpaceNormalizeViewDir(positionWS);
    float4 screenPos  = i.screenPos;
    float2 screenUV   = ProjectiveToUV(screenPos);

    half3 normalTS = SampleWaterNormalTS(i.uv);

    // 摄像机周围随机环形波纹：只改法线（折射/SSPR/高光/菲涅尔随之变化，不改颜色/泡沫）
    half2 rippleGrad;
    SampleCameraRipples(positionWS, rippleGrad);
    SampleParticleRipples(positionWS, rippleGrad);
    normalTS.xy += rippleGrad;
    normalTS = normalize(normalTS);

    half3 nWS = normalize(i.normalWS);
    half3 tWS = normalize(i.tangentWS.xyz);
    half3 bWS = cross(nWS, tWS) * i.tangentWS.w;
    half3 normalWS = normalize(TransformTangentToWorld(normalTS, half3x3(tWS, bWS, nWS)));
    // 水平水面：再叠一层世界 XZ 梯度，避免切线朝向与世界轴不一致时波纹偏斜
    normalWS = normalize(normalWS + half3(-rippleGrad.x, 0.0h, -rippleGrad.y));

#if defined(_DEBUG_NORMALS)
    return half4(normalWS * 0.5h + 0.5h, 1.0h);
#endif

    // 水深
    float sceneEyeDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
    float surfaceEyeDepth = LinearEyeDepth(i.positionCS.z, _ZBufferParams);
    float waterDepth = max(sceneEyeDepth - surfaceEyeDepth, 0.0);

    half depthFade = saturate(waterDepth / max(_ColorDepth, 1e-3h));
    half edgeFade  = saturate(waterDepth / max(_EdgeFade, 1e-3h));
    half foamMask  = saturate((1.0h - saturate(waterDepth / max(_FoamWidth, 1e-3h))) * _FoamIntensity);
    half3 waterColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, depthFade);

    // 折射
    float2 refractUV = saturate(BuildRefractionUV(screenPos, normalTS));
    half3 sceneRefract = SampleSceneColor(refractUV);
    half3 refractColor = lerp(waterColor, sceneRefract * waterColor, _RefractionStrength);

    // ========== SSPR 反射 ==========
    half4 sspr = 0;
    if (_SSPR_Enabled > 0.5)
        sspr = SampleSSPR(screenUV, normalTS, screenPos);

    // 无 SSPR 数据时回退水色（不要再用错误的 SceneColor UV 映射）
    half3 sceneReflect = lerp(waterColor, sspr.rgb, sspr.a);

#if defined(_DEBUG_REFLECTION)
    return half4(sceneReflect, 1.0h);
#endif

    half NoV = saturate(dot(normalWS, viewDirWS));
    half fresnel = _ReflectionFresnelBias + (1.0h - _ReflectionFresnelBias)
                 * pow(1.0h - NoV, _ReflectionFresnelPower);
    fresnel = saturate(fresnel);

    half3 body = lerp(refractColor, sceneReflect, fresnel * _ReflectionStrength * max(sspr.a, 0.15h));
    body = lerp(body, _FoamColor.rgb, foamMask * edgeFade);

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord = i.shadowCoord;
    #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    #else
    float4 shadowCoord = float4(0, 0, 0, 0);
    #endif

    Light mainLight = GetMainLight(shadowCoord);
    half atten = mainLight.distanceAttenuation * mainLight.shadowAttenuation;
    half3 halfDir = normalize(mainLight.direction + viewDirWS);
    half NdotH = saturate(dot(normalWS, halfDir));
    half specPow = exp2(10.0h * _Smoothness + 1.0h);
    half3 specular = mainLight.color * atten * pow(NdotH, specPow)
                   * _SpecularColor.rgb * _SpecularIntensity * fresnel;

    half3 color = MixFog(body + specular, i.fogFactor);
    half alpha = saturate(lerp(_ShallowColor.a, _DeepColor.a, depthFade) * _Alpha * edgeFade);
    return half4(color, alpha);
}

#endif
#endif
