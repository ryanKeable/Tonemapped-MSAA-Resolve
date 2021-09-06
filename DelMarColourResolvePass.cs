using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Perform ResolvePass On a render target
    /// </summary>
    internal class DelMarColourResolvePass : ScriptableRenderPass
    {
        RenderTargetHandle m_Source;
        RenderTargetHandle m_Destination;
        Material m_ResolveMaterial;
        
        const string k_RenderPostProcessingTag = "Resolve Render Target";

        public DelMarColourResolvePass(RenderPassEvent evt, Material resolveMaterial)
        {
            renderPassEvent = evt;
            m_ResolveMaterial = resolveMaterial;
        }

        /// <summary>
        /// Setup the pass
        /// </summary>
        /// <param name="sourceHandle">Source of rendering to execute the post on</param>
        public void Setup(in RenderTargetHandle sourceHandle, RenderTargetHandle dstHandle)
        {
            m_Source = sourceHandle;
            m_Destination = dstHandle;
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Resolved color buffer");
            SetupMSAAResolve(cmd, m_Source.id, m_Destination.id);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

        }

        void SetupMSAAResolve(CommandBuffer cmd, int source, int destination)
        {
            cmd.SetGlobalTexture("_CameraColorAttachment", BlitSourceDiscardContent(cmd, source));            
            Blit(cmd, BlitSourceDiscardContent(cmd, source), BlitDstDiscardContent(cmd, destination), m_ResolveMaterial);
        }

        private BuiltinRenderTextureType BlitDstDiscardContent(CommandBuffer cmd, RenderTargetIdentifier rt)
        {
            cmd.SetRenderTarget(new RenderTargetIdentifier(rt, 0, CubemapFace.Unknown, -1),
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
            return BuiltinRenderTextureType.CurrentActive;
        }
        private BuiltinRenderTextureType BlitSourceDiscardContent(CommandBuffer cmd, RenderTargetIdentifier rt)
        {
            cmd.SetRenderTarget(new RenderTargetIdentifier(rt, 0, CubemapFace.Unknown, -1),
                RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
            return BuiltinRenderTextureType.CurrentActive;
        }

    }
}
