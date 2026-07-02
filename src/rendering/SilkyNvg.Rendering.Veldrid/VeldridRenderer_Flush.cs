using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;

namespace SilkyNvg.Rendering.Veldrid
{
    public sealed partial class VeldridRenderer
    {
        /// <summary>
        /// Uploads batched vertex data and executes all queued draw calls.
        /// </summary>
        public void Flush()
        {
            if (_activeRenderCommandList == null)
            {
                throw new InvalidOperationException(
                    "VeldridRenderer.Flush() called without an active CommandList!\n" +
                    "You must call SetActiveCommandList(commandList) before BeginFrame/EndFrame.\n" +
                    "This is required to properly order SilkyNVG rendering with your rendering pipeline.");
            }

            if (!_isInitialized)
            {
                Cancel();
                return;
            }

            if (_vertexBatchCount == 0)
            {
                Cancel();
                return;
            }

            var commandList = _activeRenderCommandList;

            // Resolve the correct pipeline set for the current framebuffer's OutputDescription.
            var currentFramebuffer = commandList.CurrentFramebuffer;
            if (currentFramebuffer == null)
            {
                throw new InvalidOperationException(
                    "VeldridRenderer.Flush() called but no Framebuffer is set on the active CommandList!\n" +
                    "You must call commandList.SetFramebuffer(...) before BeginFrame/EndFrame.");
            }
            var pipelines = GetOrCreatePipelines(currentFramebuffer.OutputDescription);

            // Update view size uniform via commandList (per-command-list sequencing).
            // CRITICAL: Must use commandList.UpdateBuffer, NOT _graphicsDevice.UpdateBuffer!
            // viewSize.z = Y direction multiplier: +1.0 for OpenGL/D3D11 (Y-up), -1.0 for Vulkan (Y-down)
            float clipSpaceYMultiplier = _graphicsDevice.IsClipSpaceYInverted ? -1.0f : 1.0f;
            var viewSizeData = new Vector4(_viewportSize.Width, _viewportSize.Height, clipSpaceYMultiplier, 0);
            commandList.UpdateBuffer(_viewSizeUniformBuffer, 0, viewSizeData);

            // Upload vertices via commandList.
            var vertexPinHandle = GCHandle.Alloc(_vertexBatchArray, GCHandleType.Pinned);
            try {
                uint vertexUploadBytes = (uint)(_vertexBatchCount * Marshal.SizeOf<ShaderLayouts.NvgVertex>());
                commandList.UpdateBuffer(_vertexBuffer, 0, vertexPinHandle.AddrOfPinnedObject(), vertexUploadBytes);
            } finally {
                vertexPinHandle.Free();
            }

            commandList.SetVertexBuffer(0, _vertexBuffer);

            uint framebufferPixelWidth = (uint)(_viewportSize.Width * _devicePixelRatio);
            uint framebufferPixelHeight = (uint)(_viewportSize.Height * _devicePixelRatio);
            commandList.SetViewport(0, new Viewport(0, 0, framebufferPixelWidth, framebufferPixelHeight, 0, 1));
            commandList.SetScissorRect(0, 0, 0, framebufferPixelWidth, framebufferPixelHeight);

            // Execute draw calls
            DrawCallType lastPipelineType = (DrawCallType)255; // Force first switch
            int lastBoundTextureId = -1;
            bool lastScissorWasFullViewport = true;
            bool flushClipActive = false; // Tracks clip state during flush for pipeline selection

            foreach (var drawCall in _drawCalls)
            {
                // === Clip path stencil operations ===
                if (drawCall.Type == DrawCallType.ClipSet)
                {
                    // Ensure full viewport scissor for clip stencil operations
                    if (!lastScissorWasFullViewport) {
                        commandList.SetScissorRect(0, 0, 0, framebufferPixelWidth, framebufferPixelHeight);
                        lastScissorWasFullViewport = true;
                    }

                    // Render the clip path winding into the stencil buffer's low bits.
                    // Front faces increment, back faces decrement (nonzero winding rule).
                    // Pixels inside the clip path end up with nonzero stencil values.
                    // The clipped draw pipelines test (stencil != 0) to enforce clipping.
                    commandList.SetPipeline(drawCall.ClipIsNested ? pipelines.StencilClipFillNested : pipelines.StencilClipFill);
                    commandList.SetGraphicsResourceSet(0, _viewSizeOnlyResourceSet);
                    commandList.Draw(
                        (uint)drawCall.StencilFillVertexCount, 1,
                        (uint)drawCall.VertexOffset, 0);

                    flushClipActive = true;
                    lastPipelineType = (DrawCallType)255;
                    lastBoundTextureId = -1;
                    continue;
                }

                if (drawCall.Type == DrawCallType.ClipClear)
                {
                    // Clear the stencil buffer to remove the clip mask.
                    // Uses StencilClipClear pipeline: Replace(0) with WriteMask 0x80.
                    // Also need to clear the low bits (winding) — use a full-screen quad
                    // drawn with the StencilFill pipeline's write mask would work, but
                    // simpler to just clear via the existing ClearDepthStencil mechanism.
                    // For now, draw a full-viewport quad with the clear pipeline.
                    if (!lastScissorWasFullViewport) {
                        commandList.SetScissorRect(0, 0, 0, framebufferPixelWidth, framebufferPixelHeight);
                        lastScissorWasFullViewport = true;
                    }
                    commandList.SetPipeline(pipelines.StencilClipClear);
                    commandList.SetGraphicsResourceSet(0, _viewSizeOnlyResourceSet);
                    commandList.Draw((uint)drawCall.VertexCount, 1, (uint)drawCall.VertexOffset, 0);

                    flushClipActive = false;
                    lastPipelineType = (DrawCallType)255;
                    lastBoundTextureId = -1;
                    continue;
                }

                // === Regular draw call pipeline selection ===
                // When clip is active, use clipped pipeline variants that test stencil != 0.
                switch (drawCall.Type) {
                    case DrawCallType.SolidFill:
                        if (lastPipelineType != DrawCallType.SolidFill || flushClipActive) {
                            commandList.SetPipeline(flushClipActive ? pipelines.SolidFillClipped : pipelines.SolidFill);
                            commandList.SetGraphicsResourceSet(0, _viewSizeOnlyResourceSet);
                            lastPipelineType = DrawCallType.SolidFill;
                            lastBoundTextureId = -1;
                        }
                        break;

                    case DrawCallType.Textured:
                        if (lastPipelineType != DrawCallType.Textured || drawCall.TextureId != lastBoundTextureId || flushClipActive) {
                            var texturedResourceSet = _textureRegistry.GetOrCreateTexturedResourceSet(drawCall.TextureId);
                            if (lastPipelineType != DrawCallType.Textured || flushClipActive) {
                                commandList.SetPipeline(flushClipActive ? pipelines.TexturedClipped : pipelines.Textured);
                                lastPipelineType = DrawCallType.Textured;
                            }
                            commandList.SetGraphicsResourceSet(0, texturedResourceSet);
                            lastBoundTextureId = drawCall.TextureId;
                        }
                        break;

                    case DrawCallType.Gradient:
                        commandList.UpdateBuffer(_paintUniformBuffer, 0, drawCall.PaintParams);
                        if (lastPipelineType != DrawCallType.Gradient || flushClipActive) {
                            commandList.SetPipeline(flushClipActive ? pipelines.GradientClipped : pipelines.Gradient);
                            lastPipelineType = DrawCallType.Gradient;
                        }
                        commandList.SetGraphicsResourceSet(0, _gradientResourceSet);
                        lastBoundTextureId = -1;
                        break;

                    case DrawCallType.ImagePattern:
                        commandList.UpdateBuffer(_paintUniformBuffer, 0, drawCall.PaintParams);
                        if (lastPipelineType != DrawCallType.ImagePattern || flushClipActive) {
                            commandList.SetPipeline(flushClipActive ? pipelines.ImagePatternClipped : pipelines.ImagePattern);
                            lastPipelineType = DrawCallType.ImagePattern;
                        }
                        if (!_imagePatternResourceSetCache.TryGetValue(drawCall.TextureId, out var imagePatternResourceSet)) {
                            var patternTextureView = _textureRegistry.GetTextureView(drawCall.TextureId);
                            imagePatternResourceSet = _graphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                                _imagePatternResourceLayout,
                                _viewSizeUniformBuffer,
                                _paintUniformBuffer,
                                patternTextureView,
                                _imagePatternSampler));
                            _imagePatternResourceSetCache[drawCall.TextureId] = imagePatternResourceSet;
                        }
                        commandList.SetGraphicsResourceSet(0, imagePatternResourceSet);
                        lastBoundTextureId = drawCall.TextureId;
                        break;
                    case DrawCallType.NonConvexFill:
                        // Handled below with custom multi-pass draw
                        break;
                }

                // Apply scissor rect BEFORE drawing
                if (drawCall.HasScissor) {
                    uint scaledScissorX = (uint)Math.Max(0, (int)(drawCall.ScissorX * _devicePixelRatio));
                    uint scaledScissorY = (uint)Math.Max(0, (int)(drawCall.ScissorY * _devicePixelRatio));
                    uint scaledScissorWidth = (uint)(drawCall.ScissorWidth * _devicePixelRatio);
                    uint scaledScissorHeight = (uint)(drawCall.ScissorHeight * _devicePixelRatio);
                    commandList.SetScissorRect(0,
                        scaledScissorX, scaledScissorY,
                        scaledScissorWidth, scaledScissorHeight);
                    lastScissorWasFullViewport = false;
                } else if (!lastScissorWasFullViewport) {
                    commandList.SetScissorRect(0, 0, 0, framebufferPixelWidth, framebufferPixelHeight);
                    lastScissorWasFullViewport = true;
                }

                // Execute draw
                if (drawCall.Type == DrawCallType.NonConvexFill) {
                    // === TWO-PASS STENCIL-THEN-COVER for non-convex paths ===
                    // Pass 1: Write winding count to stencil buffer (no color output)
                    commandList.SetPipeline(pipelines.StencilFill);
                    commandList.SetGraphicsResourceSet(0, _viewSizeOnlyResourceSet);
                    commandList.Draw(
                        (uint)drawCall.StencilFillVertexCount, 1,
                        (uint)drawCall.VertexOffset, 0);

                    // Pass 2: Draw cover quad where stencil low bits != 0, zeroing low bits.
                    switch (drawCall.NonConvexCoverPaintType) {
                        case DrawCallType.Gradient:
                            commandList.UpdateBuffer(_paintUniformBuffer, 0, drawCall.PaintParams);
                            commandList.SetPipeline(pipelines.StencilCoverGradient);
                            commandList.SetGraphicsResourceSet(0, _gradientResourceSet);
                            break;
                        case DrawCallType.ImagePattern:
                            commandList.UpdateBuffer(_paintUniformBuffer, 0, drawCall.PaintParams);
                            commandList.SetPipeline(pipelines.StencilCoverImagePattern);
                            if (!_imagePatternResourceSetCache.TryGetValue(drawCall.TextureId, out var coverImagePatternResourceSet)) {
                                var coverPatternTextureView = _textureRegistry.GetTextureView(drawCall.TextureId);
                                coverImagePatternResourceSet = _graphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                                    _imagePatternResourceLayout,
                                    _viewSizeUniformBuffer,
                                    _paintUniformBuffer,
                                    coverPatternTextureView,
                                    _imagePatternSampler));
                                _imagePatternResourceSetCache[drawCall.TextureId] = coverImagePatternResourceSet;
                            }
                            commandList.SetGraphicsResourceSet(0, coverImagePatternResourceSet);
                            break;
                        default: // SolidFill
                            commandList.SetPipeline(pipelines.StencilCoverSolid);
                            commandList.SetGraphicsResourceSet(0, _viewSizeOnlyResourceSet);
                            break;
                    }
                    commandList.Draw(6, 1, (uint)drawCall.CoverQuadVertexOffset, 0);

                    // Reset pipeline tracking (stencil pipelines are transient)
                    lastPipelineType = (DrawCallType)255;
                    lastBoundTextureId = -1;
                } else {
                    commandList.Draw((uint)drawCall.VertexCount, 1, (uint)drawCall.VertexOffset, 0);
                }
            }

            Cancel();
        }

    }
}