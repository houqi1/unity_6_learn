Shader "Custom/Water"
{
    Properties
    {
        [Header(Base Colors)]
        _ShallowColor ("Shallow Color", Color) = (0.25, 0.55, 0.55, 0.65)
        _DeepColor ("Deep Color", Color) = (0.05, 0.15, 0.25, 0.9)
        _ColorDepth ("Color Depth Fade", Range(0.1, 20)) = 4
        _Alpha ("Overall Alpha", Range(0, 1)) = 0.85

        [Header(Normal Waves)]
        [NoScaleOffset] _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1
        _NormalTiling ("Normal Tiling", Vector) = (1, 1, 1.3, 1.3)
        _NormalSpeedA ("Normal Speed A (XY)", Vector) = (0.03, 0.02, 0, 0)
        _NormalSpeedB ("Normal Speed B (XY)", Vector) = (-0.02, 0.04, 0, 0)
        [KeywordEnum(RG, AG, RGB)] _NormalPack ("Normal Pack Mode", Float) = 2

        [Header(SSPR Reflection)]
        // 反射来自 SSPR Feature 生成的 _SSPR_ColorRT
        // 水面只用 screenUV + bump 采样（映射已在 Compute 完成）
        _ReflectionStrength ("Reflection Strength", Range(0, 1)) = 0.85
        _ReflectionFresnelPower ("Reflection Fresnel Power", Range(0.5, 8)) = 3
        _ReflectionFresnelBias ("Reflection Fresnel Bias", Range(0, 1)) = 0.05
        _ReflectionDistortion ("Reflection Wave Distort", Range(0, 1.5)) = 0.25
        _ReflectionScreenEdgeFade ("Reflection Screen Edge Fade", Range(0.01, 0.5)) = 0.12

        [Header(Refraction)]
        _RefractionStrength ("Refraction Strength", Range(0, 1)) = 0.55
        _RefractionDistortion ("Refraction Distort", Range(0, 1.5)) = 0.3

        [Header(Specular)]
        _SpecularColor ("Specular Color", Color) = (1, 1, 1, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.85
        _SpecularIntensity ("Specular Intensity", Range(0, 5)) = 1.2

        [Header(Edge Softness)]
        _EdgeFade ("Depth Edge Fade", Range(0.01, 5)) = 0.8
        _FoamColor ("Foam Color", Color) = (0.85, 0.95, 1, 1)
        _FoamWidth ("Foam Width", Range(0, 2)) = 0.35
        _FoamIntensity ("Foam Intensity", Range(0, 2)) = 0.6

        [Header(Debug)]
        [Toggle(_DEBUG_REFLECTION)] _DebugReflection ("Output Reflection Color Only", Float) = 0

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex WaterVert
            #pragma fragment WaterFrag

            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #pragma shader_feature_local _NORMALPACK_RG _NORMALPACK_AG _NORMALPACK_RGB
            #pragma shader_feature_local _DEBUG_REFLECTION

            #define WATER_FORWARD_PASS 1
            #include "Water.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
