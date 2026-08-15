Shader "Custom/ShellFurGpuFin"
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
        _TipAlphaCutoff ("Tip Alpha Cutoff", Range(0, 1)) = 1

        [Header(Shell Shape)]
        _ShellCount ("Shell Count", Float) = 32
        _ShellLayerOffset ("Shell Layer Offset", Float) = 0
        _FurLength ("Fur Length", Range(0.001, 0.5)) = 0.08
        _FurLengthRandom ("Length Randomness", Range(0, 1)) = 0.35

        [Header(Physics Bend)]
        _Gravity ("Gravity Strength", Range(0, 2)) = 0.35
        _GravityDir ("Gravity Direction", Vector) = (0, -1, 0, 0)
        _GravityPower ("Gravity Falloff Power", Range(0.5, 4)) = 2

        [Header(Fin Opacity)]
        _FinRootOpacity ("Fin Root Opacity", Range(0, 1)) = 1
        _FinTipOpacity ("Fin Tip Opacity", Range(0, 1)) = 0
        _FinOpacityFadeStart ("Fin Opacity Fade Start", Range(0, 1)) = 0
        _FinOpacityFadeEnd ("Fin Opacity Fade End", Range(0, 1)) = 1
        _FinOpacityPower ("Fin Opacity Power", Range(0.25, 4)) = 1

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
            Offset -1, -1
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShellFurGpuFinVert
            #pragma fragment ShellFurGpuFinFrag

            // Reduced variants: cascade hard shadows + fog only.
            // Soft shadows / screen-space shadows / additional lights were OOM'ing FXC on d3d11
            // for this StructuredBuffer + transparent fin path.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog
            #pragma shader_feature_local _USE_PROCEDURAL
            #pragma shader_feature_local _USE_KAJIYA_KAY

            #define SHELL_FUR_GPU_FIN_FORWARD 1
            #include "ShellFurGpuFin.hlsl"
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
            #pragma vertex ShellFurGpuFinShadowVert
            #pragma fragment ShellFurGpuFinShadowFrag

            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma shader_feature_local _USE_PROCEDURAL

            #define SHELL_FUR_GPU_FIN_SHADOW 1
            #include "ShellFurGpuFin.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
