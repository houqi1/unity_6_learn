using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// URP 17 Render Graph 版 SSPR（Screen Space Planar Reflection）。
/// 官方模式：ScriptableRenderPass.RecordRenderGraph + AddComputePass + ComputeGraphContext。
/// 不启用 Compatibility Mode，不依赖 RenderingData.commandBuffer（internal）。
/// </summary>
[DisallowMultipleRendererFeature("Screen Space Planar Reflection")]
public class ScreenSpacePlanarReflectionFeature : ScriptableRendererFeature
{
    [Serializable]
    public class Settings
    {
        [Tooltip("水平反射平面世界 Y，必须与水面高度一致")]
        public float planeY = 0f;

        [Range(0.25f, 1f)]
        [Tooltip("SSPR 分辨率相对屏幕")]
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
        m_Pass = new SSPRPass();
        m_Pass.renderPassEvent = settings != null
            ? settings.injectionPoint
            : RenderPassEvent.BeforeRenderingTransparents;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (ssprCompute == null)
        {
            Debug.LogWarning("[SSPR] ComputeShader 未赋值，跳过。");
            return;
        }

        if (!SystemInfo.supportsComputeShaders)
        {
            Debug.LogWarning("[SSPR] 设备不支持 ComputeShader，跳过。");
            return;
        }

        var camType = renderingData.cameraData.cameraType;
        if (camType == CameraType.Preview || camType == CameraType.Reflection)
            return;

        if (UniversalRenderer.IsOffscreenDepthTexture(ref renderingData.cameraData))
            return;

        m_Pass.Setup(settings, ssprCompute);
        m_Pass.renderPassEvent = settings.injectionPoint;
        // Depth + Color 输入会让 URP 准备 _CameraDepthTexture / Opaque
        m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
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

        Settings m_Settings;
        ComputeShader m_CS;
        int m_KClear, m_KHash, m_KResolve, m_KFill;

        public SSPRPass()
        {
            profilingSampler = new ProfilingSampler("SSPR");
        }

        public void Setup(Settings settings, ComputeShader cs)
        {
            m_Settings = settings ?? new Settings();
            m_CS = cs;
            if (m_CS == null)
                return;

            m_KClear = m_CS.FindKernel("Clear");
            m_KHash = m_CS.FindKernel("RenderHash");
            m_KResolve = m_CS.FindKernel("Resolve");
            m_KFill = m_CS.FindKernel("FillHoles");
        }

        // ---------------------------------------------------------------------
        // Pass data：只放能跨 Record → Execute 传递的数据
        // ---------------------------------------------------------------------
        class PassData
        {
            public ComputeShader cs;
            public int kClear, kHash, kResolve, kFill;
            public int groupsX, groupsY;
            public int fillX, fillY;

            public TextureHandle depthTex;
            public TextureHandle opaqueTex;
            public TextureHandle hashRT;
            public TextureHandle colorRT;

            public Vector4 rtSize;
            public Matrix4x4 vp;
            public Matrix4x4 invVP;
            public Vector3 camForward;
            public float planeY;
            public float stretchI;
            public float stretchT;
            public bool resourcesValid;
        }

        /// <summary>
        /// URP 17 主路径：仅通过 Render Graph 调度。
        /// </summary>
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_CS == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            // ---- 分辨率 ----
            var camDesc = cameraData.cameraTargetDescriptor;
            float scale = Mathf.Clamp(m_Settings.resolutionScale, 0.25f, 1f);
            int w = Mathf.Max(8, Mathf.RoundToInt(camDesc.width * scale));
            int h = Mathf.Max(8, Mathf.RoundToInt(camDesc.height * scale));
            w = (w + 7) / 8 * 8;
            h = (h + 7) / 8 * 8;

            // ---- 创建 UAV 纹理（RenderGraph 管理生命周期）----
            var colorDesc = new TextureDesc(w, h)
            {
                name = "_SSPR_ColorRT",
                format = GraphicsFormat.R8G8B8A8_UNorm,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                enableRandomWrite = true,
                clearBuffer = true,
                clearColor = Color.clear,
                msaaSamples = MSAASamples.None,
                depthBufferBits = DepthBits.None
            };
            var hashDesc = colorDesc;
            hashDesc.name = "_SSPR_HashRT";
            hashDesc.format = GraphicsFormat.R32_UInt;
            hashDesc.filterMode = FilterMode.Point;

            TextureHandle colorRT = renderGraph.CreateTexture(colorDesc);
            TextureHandle hashRT = renderGraph.CreateTexture(hashDesc);

            // 深度 / 不透明（URP 资源）
            TextureHandle depthTex = resourceData.cameraDepthTexture;
            TextureHandle opaqueTex = resourceData.cameraOpaqueTexture;
            bool resourcesValid = depthTex.IsValid() && opaqueTex.IsValid();

            var cam = cameraData.camera;
            Matrix4x4 view = cam.worldToCameraMatrix;
            // RenderGraph 路径下 RT 为 texture，renderIntoTexture=true
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 vp = proj * view;

            // -----------------------------------------------------------------
            // 单一 Compute Pass：Clear → Hash → Resolve → FillHoles → SetGlobal
            // 使用 TextureHandle 声明依赖；执行时用 ComputeGraphContext.cmd
            // -----------------------------------------------------------------
            using (var builder = renderGraph.AddComputePass<PassData>("SSPR.Compute", out var passData, profilingSampler))
            {
                passData.cs = m_CS;
                passData.kClear = m_KClear;
                passData.kHash = m_KHash;
                passData.kResolve = m_KResolve;
                passData.kFill = m_KFill;
                passData.groupsX = w / 8;
                passData.groupsY = h / 8;
                passData.fillX = Mathf.Max(1, passData.groupsX / 2);
                passData.fillY = Mathf.Max(1, passData.groupsY / 2);

                passData.hashRT = hashRT;
                passData.colorRT = colorRT;
                passData.depthTex = depthTex;
                passData.opaqueTex = opaqueTex;
                passData.resourcesValid = resourcesValid;

                passData.rtSize = new Vector4(w, h, 0, 0);
                passData.vp = vp;
                passData.invVP = vp.inverse;
                passData.camForward = cam.transform.forward;
                passData.planeY = m_Settings.planeY;
                passData.stretchI = m_Settings.stretchIntensity;
                passData.stretchT = m_Settings.stretchThreshold;

                // 依赖声明
                if (depthTex.IsValid())
                    builder.UseTexture(depthTex, AccessFlags.Read);
                if (opaqueTex.IsValid())
                    builder.UseTexture(opaqueTex, AccessFlags.Read);

                builder.UseTexture(hashRT, AccessFlags.ReadWrite);
                builder.UseTexture(colorRT, AccessFlags.ReadWrite);

                // 全局纹理副作用：禁止裁掉本 Pass
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData data, ComputeGraphContext ctx) => ExecuteSSPR(data, ctx));
            }
        }

        /// <summary>
        /// static 执行函数：禁止捕获外部成员，只读 PassData。
        /// </summary>
        static void ExecuteSSPR(PassData data, ComputeGraphContext ctx)
        {
            var cmd = ctx.cmd;

            if (!data.resourcesValid || data.cs == null)
            {
                cmd.SetGlobalFloat(ID_SSPROn, 0f);
                return;
            }

            // 常量
            cmd.SetComputeVectorParam(data.cs, ID_RTSize, data.rtSize);
            cmd.SetComputeFloatParam(data.cs, ID_PlaneY, data.planeY);
            cmd.SetComputeMatrixParam(data.cs, ID_InvVP, data.invVP);
            cmd.SetComputeMatrixParam(data.cs, ID_VP, data.vp);
            cmd.SetComputeVectorParam(data.cs, ID_CamDir, data.camForward);
            cmd.SetComputeFloatParam(data.cs, ID_StretchI, data.stretchI);
            cmd.SetComputeFloatParam(data.cs, ID_StretchT, data.stretchT);

            // --- Clear ---
            cmd.SetComputeTextureParam(data.cs, data.kClear, ID_HashRT, data.hashRT);
            cmd.SetComputeTextureParam(data.cs, data.kClear, ID_ColorRT, data.colorRT);
            cmd.DispatchCompute(data.cs, data.kClear, data.groupsX, data.groupsY, 1);

            // --- Hash：源像素镜像写入 ---
            cmd.SetComputeTextureParam(data.cs, data.kHash, ID_HashRT, data.hashRT);
            cmd.SetComputeTextureParam(data.cs, data.kHash, ID_Depth, data.depthTex);
            cmd.DispatchCompute(data.cs, data.kHash, data.groupsX, data.groupsY, 1);

            // --- Resolve：采 Opaque 写入 ColorRT ---
            cmd.SetComputeTextureParam(data.cs, data.kResolve, ID_HashRT, data.hashRT);
            cmd.SetComputeTextureParam(data.cs, data.kResolve, ID_ColorRT, data.colorRT);
            cmd.SetComputeTextureParam(data.cs, data.kResolve, ID_Opaque, data.opaqueTex);
            cmd.DispatchCompute(data.cs, data.kResolve, data.groupsX, data.groupsY, 1);

            // --- FillHoles ---
            cmd.SetComputeTextureParam(data.cs, data.kFill, ID_ColorRT, data.colorRT);
            cmd.DispatchCompute(data.cs, data.kFill, data.fillX, data.fillY, 1);

            // 供水面 Shader 采样
            cmd.SetGlobalTexture(ID_SSPRColor, data.colorRT);
            cmd.SetGlobalFloat(ID_SSPROn, 1f);
        }
    }
}
