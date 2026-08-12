using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Screen Space Planar Reflection (SSPR)
/// Compute：场景像素镜像重投影 → _SSPR_ColorRT
/// 水面：Sample(_SSPR_ColorRT, screenUV + bump)
/// </summary>
[DisallowMultipleRendererFeature("Screen Space Planar Reflection")]
public class ScreenSpacePlanarReflectionFeature : ScriptableRendererFeature
{
    [Serializable]
    public class Settings
    {
        [Tooltip("水平反射平面世界 Y（与水面高度一致）")]
        public float planeY = 0f;

        [Range(0.25f, 1f)]
        public float resolutionScale = 0.5f;

        [Range(0f, 2f)]
        public float stretchIntensity = 0.5f;

        [Range(0f, 1f)]
        public float stretchThreshold = 0.7f;

        public RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingTransparents;
    }

    public Settings settings = new Settings();
    public ComputeShader ssprCompute;

    SSPRPass m_Pass;

    public override void Create()
    {
        m_Pass ??= new SSPRPass();
        m_Pass.Setup(settings, ssprCompute);
        m_Pass.renderPassEvent = settings.injectionPoint;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (ssprCompute == null || !SystemInfo.supportsComputeShaders)
            return;

        var camType = renderingData.cameraData.cameraType;
        if (camType == CameraType.Preview || camType == CameraType.Reflection)
            return;

        if (UniversalRenderer.IsOffscreenDepthTexture(ref renderingData.cameraData))
            return;

        m_Pass.Setup(settings, ssprCompute);
        m_Pass.renderPassEvent = settings.injectionPoint;
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        m_Pass?.Dispose();
        m_Pass = null;
    }

    // =========================================================================
    sealed class SSPRPass : ScriptableRenderPass
    {
        static readonly int ID_RTSize = Shader.PropertyToID("_RTSize");
        static readonly int ID_PlaneY = Shader.PropertyToID("_HorizontalPlaneHeightWS");
        static readonly int ID_InvVP = Shader.PropertyToID("_InverseVP");
        static readonly int ID_VP = Shader.PropertyToID("_VPMatrix");
        static readonly int ID_CamDir = Shader.PropertyToID("_CameraDirection");
        static readonly int ID_StretchI = Shader.PropertyToID("_ScreenLRStretchIntensity");
        static readonly int ID_StretchT = Shader.PropertyToID("_ScreenLRStretchThreshold");
        static readonly int ID_Depth = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int ID_Opaque = Shader.PropertyToID("_CameraOpaqueTexture");
        static readonly int ID_HashRT = Shader.PropertyToID("_HashRT");
        static readonly int ID_ColorRT = Shader.PropertyToID("_ColorRT");
        static readonly int ID_SSPRColor = Shader.PropertyToID("_SSPR_ColorRT");
        static readonly int ID_SSPROn = Shader.PropertyToID("_SSPR_Enabled");

        Settings m_Settings = new Settings();
        ComputeShader m_CS;
        int m_KClear, m_KHash, m_KResolve, m_KFill;

        RTHandle m_ColorRT;
        RTHandle m_HashRT;

        public SSPRPass()
        {
            profilingSampler = new ProfilingSampler("SSPR");
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
        }

        public void Setup(Settings settings, ComputeShader cs)
        {
            m_Settings = settings ?? new Settings();
            m_CS = cs;
            if (m_CS == null) return;

            m_KClear = m_CS.FindKernel("Clear");
            m_KHash = m_CS.FindKernel("RenderHash");
            m_KResolve = m_CS.FindKernel("Resolve");
            m_KFill = m_CS.FindKernel("FillHoles");
        }

        public void Dispose()
        {
            m_ColorRT?.Release();
            m_HashRT?.Release();
            m_ColorRT = null;
            m_HashRT = null;
        }

        void EnsureRTs(int screenW, int screenH)
        {
            float scale = Mathf.Clamp(m_Settings.resolutionScale, 0.25f, 1f);
            int w = Mathf.Max(8, Mathf.RoundToInt(screenW * scale));
            int h = Mathf.Max(8, Mathf.RoundToInt(screenH * scale));
            w = (w + 7) / 8 * 8;
            h = (h + 7) / 8 * 8;

            var colorDesc = new RenderTextureDescriptor(w, h)
            {
                graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
                depthBufferBits = 0,
                msaaSamples = 1,
                enableRandomWrite = true,
                sRGB = false
            };
            var hashDesc = colorDesc;
            hashDesc.graphicsFormat = GraphicsFormat.R32_UInt;

            RenderingUtils.ReAllocateHandleIfNeeded(ref m_ColorRT, colorDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_SSPR_ColorRT");
            RenderingUtils.ReAllocateHandleIfNeeded(ref m_HashRT, hashDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SSPR_HashRT");
        }

        class PassData
        {
            public ComputeShader cs;
            public int kClear, kHash, kResolve, kFill;
            public RTHandle colorRT;
            public RTHandle hashRT;
            public TextureHandle depthHandle;
            public TextureHandle opaqueHandle;
            public Vector2Int rtSize;
            public Matrix4x4 vp;
            public Matrix4x4 invVP;
            public Vector3 camForward;
            public float planeY;
            public float stretchI;
            public float stretchT;
            public bool hasDepthOpaque;
        }

        void DispatchSSPR(CommandBuffer cmd, PassData data)
        {
            if (data.cs == null || data.colorRT == null || data.hashRT == null)
            {
                cmd.SetGlobalFloat(ID_SSPROn, 0f);
                return;
            }

            if (!data.hasDepthOpaque)
            {
                cmd.SetGlobalFloat(ID_SSPROn, 0f);
                return;
            }

            cmd.SetComputeVectorParam(data.cs, ID_RTSize, new Vector4(data.rtSize.x, data.rtSize.y, 0, 0));
            cmd.SetComputeFloatParam(data.cs, ID_PlaneY, data.planeY);
            cmd.SetComputeMatrixParam(data.cs, ID_InvVP, data.invVP);
            cmd.SetComputeMatrixParam(data.cs, ID_VP, data.vp);
            cmd.SetComputeVectorParam(data.cs, ID_CamDir, data.camForward);
            cmd.SetComputeFloatParam(data.cs, ID_StretchI, data.stretchI);
            cmd.SetComputeFloatParam(data.cs, ID_StretchT, data.stretchT);

            int gx = data.rtSize.x / 8;
            int gy = data.rtSize.y / 8;

            cmd.SetComputeTextureParam(data.cs, data.kClear, ID_HashRT, data.hashRT);
            cmd.SetComputeTextureParam(data.cs, data.kClear, ID_ColorRT, data.colorRT);
            cmd.DispatchCompute(data.cs, data.kClear, gx, gy, 1);

            // 深度 / Opaque 用全局纹理（URP 已 set global）
            var depthTex = Shader.GetGlobalTexture("_CameraDepthTexture");
            var opaqueTex = Shader.GetGlobalTexture("_CameraOpaqueTexture");
            if (depthTex == null || opaqueTex == null)
            {
                cmd.SetGlobalFloat(ID_SSPROn, 0f);
                return;
            }

            cmd.SetComputeTextureParam(data.cs, data.kHash, ID_HashRT, data.hashRT);
            cmd.SetComputeTextureParam(data.cs, data.kHash, ID_Depth, depthTex);
            cmd.DispatchCompute(data.cs, data.kHash, gx, gy, 1);

            cmd.SetComputeTextureParam(data.cs, data.kResolve, ID_HashRT, data.hashRT);
            cmd.SetComputeTextureParam(data.cs, data.kResolve, ID_ColorRT, data.colorRT);
            cmd.SetComputeTextureParam(data.cs, data.kResolve, ID_Opaque, opaqueTex);
            cmd.DispatchCompute(data.cs, data.kResolve, gx, gy, 1);

            int fx = Mathf.Max(1, gx / 2);
            int fy = Mathf.Max(1, gy / 2);
            cmd.SetComputeTextureParam(data.cs, data.kFill, ID_ColorRT, data.colorRT);
            cmd.DispatchCompute(data.cs, data.kFill, fx, fy, 1);

            cmd.SetGlobalTexture(ID_SSPRColor, data.colorRT);
            cmd.SetGlobalFloat(ID_SSPROn, 1f);
        }

        // ----- Compatibility Mode (Render Graph off) -----
#pragma warning disable 618, 672
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            EnsureRTs(desc.width, desc.height);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_CS == null)
                return;

            var cam = renderingData.cameraData.camera;
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            EnsureRTs(desc.width, desc.height);

            CommandBuffer cmd = CommandBufferPool.Get("SSPR");
            using (new ProfilingScope(cmd, profilingSampler))
            {
                Matrix4x4 view = cam.worldToCameraMatrix;
                Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
                Matrix4x4 vp = proj * view;

                var data = new PassData
                {
                    cs = m_CS,
                    kClear = m_KClear,
                    kHash = m_KHash,
                    kResolve = m_KResolve,
                    kFill = m_KFill,
                    colorRT = m_ColorRT,
                    hashRT = m_HashRT,
                    rtSize = new Vector2Int(m_ColorRT.rt.width, m_ColorRT.rt.height),
                    vp = vp,
                    invVP = vp.inverse,
                    camForward = cam.transform.forward,
                    planeY = m_Settings.planeY,
                    stretchI = m_Settings.stretchIntensity,
                    stretchT = m_Settings.stretchThreshold,
                    hasDepthOpaque = true
                };
                DispatchSSPR(cmd, data);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore 618, 672

        // ----- Render Graph (URP 17 默认路径) -----
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_CS == null)
                return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            var desc = cameraData.cameraTargetDescriptor;
            EnsureRTs(desc.width, desc.height);
            if (m_ColorRT == null || m_HashRT == null)
                return;

            var cam = cameraData.camera;
            Matrix4x4 view = cam.worldToCameraMatrix;
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 vp = proj * view;

            // Import 持久 RT 进 RenderGraph
            TextureHandle colorHandle = renderGraph.ImportTexture(m_ColorRT);
            TextureHandle hashHandle = renderGraph.ImportTexture(m_HashRT);

            using (var builder = renderGraph.AddUnsafePass<PassData>("SSPR", out var passData, profilingSampler))
            {
                passData.cs = m_CS;
                passData.kClear = m_KClear;
                passData.kHash = m_KHash;
                passData.kResolve = m_KResolve;
                passData.kFill = m_KFill;
                passData.colorRT = m_ColorRT;
                passData.hashRT = m_HashRT;
                passData.rtSize = new Vector2Int(m_ColorRT.rt.width, m_ColorRT.rt.height);
                passData.vp = vp;
                passData.invVP = vp.inverse;
                passData.camForward = cam.transform.forward;
                passData.planeY = m_Settings.planeY;
                passData.stretchI = m_Settings.stretchIntensity;
                passData.stretchT = m_Settings.stretchThreshold;
                passData.hasDepthOpaque = true;

                // 声明依赖：需要相机深度与不透明色（若有效）
                if (resources.cameraDepthTexture.IsValid())
                {
                    passData.depthHandle = resources.cameraDepthTexture;
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                }
                if (resources.cameraOpaqueTexture.IsValid())
                {
                    passData.opaqueHandle = resources.cameraOpaqueTexture;
                    builder.UseTexture(resources.cameraOpaqueTexture, AccessFlags.Read);
                }

                builder.UseTexture(colorHandle, AccessFlags.ReadWrite);
                builder.UseTexture(hashHandle, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    DispatchSSPR(cmd, data);
                });
            }
        }
    }
}
