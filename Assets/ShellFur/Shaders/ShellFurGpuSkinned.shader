Shader "Custom/ShellFurGpuSkinned"
{
    Properties
    {
        [Header(Base)]
        [MainColor] _BaseColor ("Base Color (Root)", Color) = (0.35, 0.22, 0.12, 1)
        _TipColor ("Tip Color", Color) = (0.85, 0.65, 0.40, 1)
        [MainTexture] _BaseMap ("Albedo (RGB) Mask (A)", 2D) = "white" {}

        [Header(Fur Pattern)]
        _FurMap ("Fur Density Map (R)", 2D) = "white" {}
        [Toggle(_USE_PROCEDURAL)] _UseProcedural ("Use Procedural Strands", Float) = 0
        _Density ("Strand Density", Range(8, 512)) = 128
        _Thickness ("Strand Thickness", Range(0.05, 1.0)) = 0.55
        _Occlusion ("Root Occlusion", Range(0, 1)) = 0.55
        _AlphaCutoff ("Base Alpha Cutoff", Range(0, 1)) = 0.15

        [Header(Shell Shape)]
        _ShellCount ("Shell Count", Float) = 32
        _ShellLayerOffset ("Shell Layer Offset", Float) = 0
        _FurLength ("Fur Length", Range(0.001, 0.5)) = 0.08
        _FurLengthRandom ("Length Randomness", Range(0, 1)) = 0.35

        [Header(Physics Bend)]
        _Gravity ("Gravity Strength", Range(0, 2)) = 0.35
        _GravityDir ("Gravity Direction", Vector) = (0, -1, 0, 0)

        [Header(Lighting)]
        _Smoothness ("Smoothness", Range(0, 1)) = 0.15
        [Toggle(_USE_KAJIYA_KAY)] _UseKajiyaKay ("Kajiya-Kay Specular", Float) = 0
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.5
        _RimStrength ("Rim Strength", Range(0, 10)) = 0.25
        _ShadowStrength ("Self Shadow", Range(0, 10)) = 0.35

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            AlphaToMask On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShellFurGpuVert
            #pragma fragment ShellFurGpuFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma shader_feature_local _USE_PROCEDURAL
            #pragma shader_feature_local _USE_KAJIYA_KAY

            #include "ShellFurGpuSkinning.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShellFurGpuShadowVert
            #pragma fragment ShellFurGpuShadowFrag

            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma shader_feature_local _USE_PROCEDURAL

            #include "ShellFurGpuSkinning.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
