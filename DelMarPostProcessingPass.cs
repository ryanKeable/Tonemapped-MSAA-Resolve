using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal.Internal
{
    // TODO: TAA
    // TODO: Motion blur
    /// <summary>
    /// Renders the post-processing effect stack.
    /// </summary>
    public class DelMarPostProcessingPass : ScriptableRenderPass
    {
        RenderTextureDescriptor m_Descriptor;
        RenderTargetHandle m_Source;
        RenderTargetHandle m_Destination;

        const string k_RenderPostProcessingTag = "Render PostProcessing Effects";

        PostProcessData m_Data;

        // Builtin effects settings
        Bloom m_Bloom;

        // Misc
        const int k_MaxPyramidSize = 16;
        readonly GraphicsFormat m_DefaultFormat;
        readonly GraphicsFormat m_DefaultHDRFormat;
        bool m_ResetHistory;
        bool m_IsStereo;

        Material m_BloomMaterial;
        Material m_BlitMaterial;
        Material m_ResolveMaterial;
        int tw;
        int th;
        int maxSize;
        int iterations;
        int mipCount;
        float threshold;
        float thresholdKnee;
        float scatter;

        public DelMarPostProcessingPass(RenderPassEvent evt, PostProcessData data, Material blitMaterial = null, Material bloomMaterial = null, Material resolveMaterial = null)
        {
            renderPassEvent = evt;
            m_Data = data;
            m_BlitMaterial = blitMaterial;
            m_BloomMaterial = bloomMaterial;
            m_ResolveMaterial = resolveMaterial;

            m_DefaultFormat = GraphicsFormat.R8G8B8A8_SRGB;
            m_DefaultHDRFormat = GraphicsFormat.B10G11R11_UFloatPack32;

            // Bloom pyramid shader ids - can't use a simple stackalloc in the bloom function as we
            // unfortunately need to allocate strings
            ShaderConstants._BloomMipUp = new int[k_MaxPyramidSize];
            ShaderConstants._BloomMipDown = new int[k_MaxPyramidSize];

            for (int i = 0; i < k_MaxPyramidSize; i++)
            {
                ShaderConstants._BloomMipUp[i] = Shader.PropertyToID("_BloomMipUp" + i);
                ShaderConstants._BloomMipDown[i] = Shader.PropertyToID("_BloomMipDown" + i);
            }

            m_ResetHistory = true;
        }

        public void Setup(in RenderTextureDescriptor baseDescriptor, in RenderTargetHandle source, in RenderTargetHandle destination)
        {
            m_Descriptor = baseDescriptor;
            m_Source = source;
            m_Destination = destination;
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            if (m_Destination == RenderTargetHandle.CameraTarget)
                return;

            // THIS IS MY MSAA DESCRIPTOR!!

            var desc = cameraTextureDescriptor;
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;

            cmd.GetTemporaryRT(m_Destination.id, desc, FilterMode.Point);
        }

        public void ResetHistory()
        {
            m_ResetHistory = true;
        }

        public bool CanRunOnTile()
        {
            // Check builtin & user effects here
            return false;
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // Start by pre-fetching all builtin effect settings we need
            // Some of the color-grading settings are only used in the color grading lut pass
            var stack = VolumeManager.instance.stack;

            m_Bloom               = stack.GetComponent<Bloom>();

            var cmd = CommandBufferPool.Get(k_RenderPostProcessingTag);
            Render(cmd, ref renderingData);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            m_ResetHistory = false;
        }

        RenderTextureDescriptor GetStereoCompatibleDescriptor()
            => GetStereoCompatibleDescriptor(m_Descriptor.width, m_Descriptor.height, m_Descriptor.graphicsFormat, m_Descriptor.depthBufferBits);

        RenderTextureDescriptor GetStereoCompatibleDescriptor(int width, int height, GraphicsFormat format, int depthBufferBits = 0)
        {
            // Inherit the VR setup from the camera descriptor
            var desc = m_Descriptor;
            desc.bindMS = false;
            desc.depthBufferBits = depthBufferBits;
            desc.msaaSamples = 1;
            desc.width = width;
            desc.height = height;
            desc.graphicsFormat = format;
            return desc;
        }

        void Render(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ref var cameraData = ref renderingData.cameraData;
            m_IsStereo = renderingData.cameraData.isStereoEnabled;

            // Don't use these directly unless you have a good reason to, use GetSource() and
            // GetDestination() instead
            int source = m_Source.id;

            // Utilities to simplify intermediate target management
            int GetSource() => source;

            // Setup projection matrix for cmd.DrawMesh()
            cmd.SetGlobalMatrix(ShaderConstants._FullscreenProjMat, GL.GetGPUProjectionMatrix(Matrix4x4.identity, true));

            // Setup projection matrix for cmd.DrawMesh()
            cmd.SetGlobalMatrix(ShaderConstants._FullscreenProjMat, GL.GetGPUProjectionMatrix(Matrix4x4.identity, true));

            ProfilingSampler uberSampler = new ProfilingSampler("Post Processing");
            ProfilingSampler msaaSampler = new ProfilingSampler("MSAA Resolve Pass");
            ProfilingSampler bloomSampler = new ProfilingSampler("Bloom Pass");
        
            using (new ProfilingScope(cmd, uberSampler))
            {

                // Bloom goes first
                using (new ProfilingScope(cmd, bloomSampler))
                    SetupBloom(cmd, GetSource());
                
                // Done with Uber, blit it
                cmd.SetGlobalTexture("_BlitTex", GetSource()); //GetSource()

                var colorLoadAction = RenderBufferLoadAction.DontCare;
                if (m_Destination == RenderTargetHandle.CameraTarget && !cameraData.isDefaultViewport)
                    colorLoadAction = RenderBufferLoadAction.Load;

                // Note: We rendering to "camera target" we need to get the cameraData.targetTexture as this will get the targetTexture of the camera stack.
                // Overlay cameras need to output to the target described in the base camera while doing camera stack.
                RenderTargetIdentifier cameraTarget = m_Destination.Identifier(); // (m_Destination == RenderTargetHandle.CameraTarget) ? cameraTarget : m_Destination.Identifier();
                cmd.SetRenderTarget(cameraTarget, colorLoadAction, RenderBufferStoreAction.Store, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);

                
                // With camera stacking we not always resolve post to final screen as we might run post-processing in the middle of the stack.
                if (m_IsStereo)
                {
                    Blit(cmd, GetSource(), BuiltinRenderTextureType.CurrentActive, m_BlitMaterial);  //GetSource()
                }
                else
                {
                    // cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);

                    // if (m_Destination == RenderTargetHandle.CameraTarget)
                    //     cmd.SetViewport(cameraData.camera.pixelRect);

                    // cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_BlitMaterial);

                    // // TODO: We need a proper camera texture swap chain in URP.
                    // // For now, when render post-processing in the middle of the camera stack (not resolving to screen)
                    // // we do an extra blit to ping pong results back to color texture. In future we should allow a Swap of the current active color texture
                    // // in the pipeline to avoid this extra blit.
                    // if (!finishPostProcessOnScreen)
                    // {
                    //     cmd.SetGlobalTexture("_BlitTex", m_Source.id);
                    //     cmd.SetRenderTarget(m_Source.id, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
                    //     cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_BlitMaterial);
                    // }

                    // cmd.SetViewProjectionMatrices(cameraData.camera.worldToCameraMatrix, cameraData.camera.projectionMatrix);

                    cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    cmd.SetViewport(cameraData.camera.pixelRect);
                    cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_BlitMaterial);
                    cmd.SetViewProjectionMatrices(cameraData.camera.worldToCameraMatrix, cameraData.camera.projectionMatrix);
                }

                // Cleanup
                cmd.ReleaseTemporaryRT(ShaderConstants._BloomMipUp[0]);
            }
        }

        private BuiltinRenderTextureType BlitDstDiscardContent(CommandBuffer cmd, RenderTargetIdentifier rt)
        {
            // We set depth to DontCare because rt might be the source of PostProcessing used as a temporary target
            // Source typically comes with a depth buffer and right now we don't have a way to only bind the color attachment of a RenderTargetIdentifier
            cmd.SetRenderTarget(new RenderTargetIdentifier(rt, 0, CubemapFace.Unknown, -1),
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
            return BuiltinRenderTextureType.CurrentActive;
        }
        private BuiltinRenderTextureType BlitSourceDiscardContent(CommandBuffer cmd, RenderTargetIdentifier rt)
        {
            // We set depth to DontCare because rt might be the source of PostProcessing used as a temporary target
            // Source typically comes with a depth buffer and right now we don't have a way to only bind the color attachment of a RenderTargetIdentifier
            cmd.SetRenderTarget(new RenderTargetIdentifier(rt, 0, CubemapFace.Unknown, -1),
                RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
            return BuiltinRenderTextureType.CurrentActive;
        }

        #region Bloom
        void ConfigureBloom()
        {
            // Start at half-res
            tw = m_Descriptor.width >> 1;
            th = m_Descriptor.height >> 1;

            // Determine the iteration count
            maxSize = Mathf.Max(tw, th);
            iterations = Mathf.FloorToInt(Mathf.Log(maxSize, 2f) - 1);
            mipCount = Mathf.Clamp(iterations, 1, k_MaxPyramidSize);

            // Pre-filtering parameters
            threshold = Mathf.GammaToLinearSpace(m_Bloom.threshold.value);
            thresholdKnee = threshold * 0.5f; // Hardcoded soft knee

            // Material setup
            scatter = Mathf.Lerp(0.05f, 0.95f, m_Bloom.scatter.value) * 1f;
            m_BloomMaterial.SetVector(ShaderConstants._Bloom_Params, new Vector4(scatter, m_Bloom.intensity.value, threshold, thresholdKnee));
            m_BlitMaterial.SetFloat( ShaderConstants._Bloom_Intenisty, m_Bloom.intensity.value);
        }

        void SetupBloom(CommandBuffer cmd, int source)
        {
            ConfigureBloom();

            // make sure we MSAA sample the filter blur in order to correct the anti-aliasing
            // subsequent blurs can be performed without MSAA sampling
            // tw = tw >> 1;
            // th = th >> 1;
            
            var desc = GetStereoCompatibleDescriptor(tw, th, m_DefaultHDRFormat);
            // filterDesc.msaaSamples = 4;

            // bilinear performs first basic blur at half res
            // this way we do not discard too many pixels before the blur passes
            cmd.GetTemporaryRT(ShaderConstants._BloomMipDown[0], desc, FilterMode.Bilinear);
            cmd.GetTemporaryRT(ShaderConstants._BloomMipUp[0], desc, FilterMode.Bilinear);

            cmd.Blit(BlitSourceDiscardContent(cmd, source), ShaderConstants._BloomMipDown[0], m_BloomMaterial, 0);            
            
            // Downsample - gaussian pyramid
            // we go down again before we blur
            int lastDown = ShaderConstants._BloomMipDown[0];
            for (int i = 1; i < mipCount - 1; i++)
            {
                tw = Mathf.Max(1, tw >> 1);
                th = Mathf.Max(1, th >> 1);

                int mipDown = ShaderConstants._BloomMipDown[i];
                int mipUp = ShaderConstants._BloomMipUp[i];

                desc.width = tw;
                desc.height = th;

                cmd.GetTemporaryRT(mipDown, desc, FilterMode.Bilinear);
                cmd.GetTemporaryRT(mipUp, desc, FilterMode.Bilinear);

                // Classic two pass gaussian blur - use mipUp as a temporary target
                //   First pass does 2x downsampling + 9-tap gaussian using a 5-tap filter + bilinear filtering
                //   Second pass does 9-tap gaussian using a 5-tap filter + bilinear filtering
                cmd.Blit(lastDown, mipUp, m_BloomMaterial, 1);
                cmd.Blit(mipUp, mipDown, m_BloomMaterial, 2);
                lastDown = mipDown;
            }


            for (int i = mipCount - 3; i >= 0; i--)
            {
                int lowMip = (i == mipCount - 2) ? ShaderConstants._BloomMipDown[i + 1] : ShaderConstants._BloomMipUp[i + 1];
                int highMip = (i == 0) ? ShaderConstants._BloomMipDown[2] : ShaderConstants._BloomMipDown[i];

                int dst = ShaderConstants._BloomMipUp[i];

                cmd.SetGlobalTexture(ShaderConstants._MainTexLowMip, lowMip);
                cmd.Blit(highMip, BlitDstDiscardContent(cmd, dst), m_BloomMaterial, 3);
            }

            // Cleanup
            for (int i = 0; i < k_MaxPyramidSize; i++)
            {
                cmd.ReleaseTemporaryRT(ShaderConstants._BloomMipDown[i]);
                if (i > 0) cmd.ReleaseTemporaryRT(ShaderConstants._BloomMipUp[i]);
            }


            cmd.SetGlobalTexture(ShaderConstants._Bloom_Texture, ShaderConstants._BloomMipUp[0]);

        }

        #endregion

        #region Internal utilities

        // Precomputed shader ids to same some CPU cycles (mostly affects mobile)
        static class ShaderConstants
        {
            public static readonly int _MainTexLowMip      = Shader.PropertyToID("_MainTexLowMip");
            public static readonly int _Bloom_Params       = Shader.PropertyToID("_Bloom_Params");
            public static readonly int _Bloom_Intenisty       = Shader.PropertyToID("_Bloom_Intensity");
            public static readonly int _Bloom_Texture      = Shader.PropertyToID("_Bloom_Texture");
            public static readonly int _FullscreenProjMat  = Shader.PropertyToID("_FullscreenProjMat");
            public static int[] _BloomMipUp;
            public static int[] _BloomMipDown;
           
        }

        #endregion
    }
}
