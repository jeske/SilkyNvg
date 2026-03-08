using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;

namespace SilkyNvg.Rendering.Veldrid
{
    public sealed partial class VeldridRenderer
    {
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

            if (_vertexBatch.Count == 0)
            {
                Cancel();
                return;
            }

            var commandList = _activeRenderCommandList;

            // Update view size uniform (use Vector4 for proper 16-byte alignment)
            var viewSizeData = new Vector4(_viewportSize.Width, _viewportSize.Height, 0, 0);
            _graphicsDevice.UpdateBuffer(_viewSizeUniformBuffer, 0, viewSizeData);

            // Resize vertex buffer if needed
            uint requiredVertexBufferSize = (uint)(_vertexBatch.Count * Marshal.SizeOf<ShaderLayouts.NvgVertex>());
            if (_vertexBuffer!.SizeInBytes < requiredVertexBufferSize)
            {
                _vertexBuffer.Dispose();
                _vertexBuffer = _graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(
                    requiredVertexBufferSize * 2,
                    BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            }

            // Upload vertices
            _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, _vertexBatch.ToArray());

            // Set shared vertex buffer (same layout for both pipelines)
            commandList.SetVertexBuffer(0, _vertexBuffer);

            // Default scissor to full framebuffer (scissor test is always enabled in pipelines)
            // IMPORTANT: Use framebuffer dimensions, not NVG viewport — scissor rect is in pixel coordinates
            uint fullFramebufferWidth = _graphicsDevice.SwapchainFramebuffer.Width;
            uint fullFramebufferHeight = _graphicsDevice.SwapchainFramebuffer.Height;
            commandList.SetScissorRect(0, 0, 0, fullFramebufferWidth, fullFramebufferHeight);

            // Scale factor from NVG coordinates to framebuffer pixels
            float nvgToFramebufferScaleX = _viewportSize.Width > 0 ? fullFramebufferWidth / _viewportSize.Width : 1.0f;
            float nvgToFramebufferScaleY = _viewportSize.Height > 0 ? fullFramebufferHeight / _viewportSize.Height : 1.0f;

            // DEBUG: Log draw call types once to diagnose gradient issue
            if (_debugLogNextFlush) {
                _debugLogNextFlush = false;
                Console.WriteLine($"[VeldridRenderer.Flush] {_drawCalls.Count} draw calls:");
                for (int debugIdx = 0; debugIdx < _drawCalls.Count; debugIdx++) {
                    var debugDrawCall = _drawCalls[debugIdx];
                    Console.WriteLine($"  [{debugIdx}] Type={debugDrawCall.Type}, VertexOffset={debugDrawCall.VertexOffset}, VertexCount={debugDrawCall.VertexCount}, TextureId={debugDrawCall.TextureId}");
                    if (debugDrawCall.Type == DrawCallType.Gradient || debugDrawCall.Type == DrawCallType.ImagePattern) {
                        var paintParams = debugDrawCall.PaintParams;
                        Console.WriteLine($"       InnerColor=({paintParams.InnerColor.X:F2},{paintParams.InnerColor.Y:F2},{paintParams.InnerColor.Z:F2},{paintParams.InnerColor.W:F2})");
                        Console.WriteLine($"       OuterColor=({paintParams.OuterColor.X:F2},{paintParams.OuterColor.Y:F2},{paintParams.OuterColor.Z:F2},{paintParams.OuterColor.W:F2})");
                        Console.WriteLine($"       Extent=({paintParams.Extent.X:F2},{paintParams.Extent.Y:F2}), Radius={paintParams.Radius:F2}, Feather={paintParams.Feather:F2}");
                    }
                }
            }

            // Execute draw calls, switching pipeline and scissor per call
            DrawCallType lastPipelineType = (DrawCallType)255; // Force first switch
            int lastBoundTextureId = -1;
            bool lastScissorWasFullViewport = true;
            foreach (var drawCall in _drawCalls)
            {
                // Switch pipeline based on draw call type
                switch (drawCall.Type) {
                    case DrawCallType.SolidFill:
                        if (lastPipelineType != DrawCallType.SolidFill) {
                            commandList.SetPipeline(_solidFillPipeline);
                            commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);
                            lastPipelineType = DrawCallType.SolidFill;
                            lastBoundTextureId = -1;
                        }
                        break;

                    case DrawCallType.Textured:
                        if (lastPipelineType != DrawCallType.Textured || drawCall.TextureId != lastBoundTextureId) {
                            var texturedResourceSet = _textureRegistry.GetOrCreateTexturedResourceSet(drawCall.TextureId);
                            if (lastPipelineType != DrawCallType.Textured) {
                                commandList.SetPipeline(_texturedPipeline);
                                lastPipelineType = DrawCallType.Textured;
                            }
                            commandList.SetGraphicsResourceSet(0, texturedResourceSet);
                            lastBoundTextureId = drawCall.TextureId;
                        }
                        break;

                    case DrawCallType.Gradient:
                        // Update paint uniform buffer via command list for proper per-draw-call sequencing
                        commandList.UpdateBuffer(_paintUniformBuffer, 0, drawCall.PaintParams);
                        if (lastPipelineType != DrawCallType.Gradient) {
                            commandList.SetPipeline(_gradientPipeline);
                            lastPipelineType = DrawCallType.Gradient;
                        }
                        commandList.SetGraphicsResourceSet(0, _gradientResourceSet);
                        lastBoundTextureId = -1;
                        break;

                    case DrawCallType.ImagePattern:
                        // Update paint uniform buffer via command list for proper per-draw-call sequencing
                        commandList.UpdateBuffer(_paintUniformBuffer, 0, drawCall.PaintParams);
                        if (lastPipelineType != DrawCallType.ImagePattern) {
                            commandList.SetPipeline(_imagePatternPipeline);
                            lastPipelineType = DrawCallType.ImagePattern;
                        }
                        // Get or create cached resource set for this texture
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

                // Apply scissor rect BEFORE drawing (scale from NVG coordinates to framebuffer pixels)
                if (drawCall.HasScissor) {
                    uint scaledScissorX = (uint)Math.Max(0, (int)(drawCall.ScissorX * nvgToFramebufferScaleX));
                    uint scaledScissorY = (uint)Math.Max(0, (int)(drawCall.ScissorY * nvgToFramebufferScaleY));
                    uint scaledScissorWidth = (uint)(drawCall.ScissorWidth * nvgToFramebufferScaleX);
                    uint scaledScissorHeight = (uint)(drawCall.ScissorHeight * nvgToFramebufferScaleY);
                    commandList.SetScissorRect(0,
                        scaledScissorX, scaledScissorY,
                        scaledScissorWidth, scaledScissorHeight);
                    lastScissorWasFullViewport = false;
                } else if (!lastScissorWasFullViewport) {
                    commandList.SetScissorRect(0, 0, 0, fullFramebufferWidth, fullFramebufferHeight);
                    lastScissorWasFullViewport = true;
                }

                // Execute draw (NonConvexFill has its own multi-pass draw sequence)
                if (drawCall.Type == DrawCallType.NonConvexFill) {
                    // === TWO-PASS STENCIL-THEN-COVER for non-convex paths ===
                    // Stencil is already cleared to 0 by ClearDepthStencil in GameLoop.

                    // Pass 1: Write winding count to stencil buffer (no color output)
                    // Front faces increment, back faces decrement (non-zero winding rule)
                    commandList.SetPipeline(_stencilFillPipeline);
                    commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);
                    commandList.Draw(
                        (uint)drawCall.StencilFillVertexCount, 1,
                        (uint)drawCall.VertexOffset, 0);

                    // Pass 2: Draw cover quad where stencil != 0, zeroing stencil as we go
                    commandList.SetPipeline(_stencilCoverSolidPipeline);
                    commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);
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

        /// <summary>
        /// DEBUG: Draw a hardcoded test triangle bypassing NVG completely.
        /// Validates the Veldrid pipeline is working.
        /// </summary>
        public void DrawTestTriangle(CommandList commandList)
        {
            if (!_isInitialized) {
                if (!Create()) {
                    return;
                }
            }

            float viewWidth = _viewportSize.Width > 0 ? _viewportSize.Width : 800;
            float viewHeight = _viewportSize.Height > 0 ? _viewportSize.Height : 600;

            var testTriangleVertices = new ShaderLayouts.NvgVertex[]
            {
                new ShaderLayouts.NvgVertex { Position = new Vector2(viewWidth * 0.5f, viewHeight * 0.2f), TexCoord = Vector2.Zero, Color = new RgbaFloat(1, 0, 0, 1) },
                new ShaderLayouts.NvgVertex { Position = new Vector2(viewWidth * 0.2f, viewHeight * 0.8f), TexCoord = Vector2.Zero, Color = new RgbaFloat(0, 1, 0, 1) },
                new ShaderLayouts.NvgVertex { Position = new Vector2(viewWidth * 0.8f, viewHeight * 0.8f), TexCoord = Vector2.Zero, Color = new RgbaFloat(0, 0, 1, 1) },
            };

            var viewSizeData = new Vector4(viewWidth, viewHeight, 0, 0);
            _graphicsDevice.UpdateBuffer(_viewSizeUniformBuffer, 0, viewSizeData);
            _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, testTriangleVertices);

            commandList.SetPipeline(_solidFillPipeline);
            commandList.SetVertexBuffer(0, _vertexBuffer);
            commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);
            commandList.Draw(3, 1, 0, 0);
        }

        /// <summary>
        /// DEBUG: Draw a checkerboard "debug clear" into stencil, then draw the real NVG path
        /// triangles on top, then draw the cover quad reading the combined stencil.
        ///
        /// The checkerboard provides a known visible baseline pattern in the stencil buffer.
        /// The NVG path triangles are drawn ON TOP — they'll fill in the checkerboard gaps
        /// where they cover, making those areas solid (no checkerboard pattern).
        ///
        /// What you'll see:
        ///   - Checkerboard pattern = stencil from debug clear only (NVG triangles missed here)
        ///   - Solid fill areas = NVG triangles wrote stencil here (overwriting checkerboard gaps)
        ///   - Empty areas = neither checkerboard nor NVG triangles covered this pixel
        /// </summary>
        private void DrawNonConvexFillWithDebugClearCheckerboard(CommandList commandList, DrawCall drawCall)
        {
            // Log framebuffer output description once for diagnostics
            if (_debugLogNextStencilCheckerboard) {
                _debugLogNextStencilCheckerboard = false;
                var outputDesc = _graphicsDevice.SwapchainFramebuffer.OutputDescription;
                Console.WriteLine($"[StencilDebug] Framebuffer OutputDescription:");
                Console.WriteLine($"  DepthAttachment: {(outputDesc.DepthAttachment.HasValue ? outputDesc.DepthAttachment.Value.Format.ToString() : "NONE")}");
                Console.WriteLine($"  ColorAttachments: {outputDesc.ColorAttachments.Length}");
                for (int i = 0; i < outputDesc.ColorAttachments.Length; i++) {
                    Console.WriteLine($"    [{i}] Format={outputDesc.ColorAttachments[i].Format}");
                }
                Console.WriteLine($"  SampleCount: {outputDesc.SampleCount}");
                Console.WriteLine($"  StencilFillVertexCount: {drawCall.StencilFillVertexCount}");
                Console.WriteLine($"  CoverQuadVertexOffset: {drawCall.CoverQuadVertexOffset}");
            }

            // Extract cover quad bounds from the existing cover quad vertices in _vertexBatch
            // Cover quad is 6 vertices starting at drawCall.CoverQuadVertexOffset
            // Vertices are: TL, TR, BR, TL, BR, BL — so TL is [0], BR is [2]
            var coverTopLeft = _vertexBatch[drawCall.CoverQuadVertexOffset].Position;
            var coverBottomRight = _vertexBatch[drawCall.CoverQuadVertexOffset + 2].Position;
            float boundsLeft = coverTopLeft.X;
            float boundsTop = coverTopLeft.Y;
            float boundsRight = coverBottomRight.X;
            float boundsBottom = coverBottomRight.Y;

            // Generate checkerboard quads covering the cover quad bounds
            const int checkerCellSize = 6; // 6x6 pixel cells
            int cellStartX = (int)Math.Floor(boundsLeft / checkerCellSize);
            int cellStartY = (int)Math.Floor(boundsTop / checkerCellSize);
            int cellEndX = (int)Math.Ceiling(boundsRight / checkerCellSize);
            int cellEndY = (int)Math.Ceiling(boundsBottom / checkerCellSize);

            // Count "on" cells for checkerboard
            int stencilQuadCount = 0;
            for (int cellY = cellStartY; cellY < cellEndY; cellY++) {
                for (int cellX = cellStartX; cellX < cellEndX; cellX++) {
                    if ((cellX + cellY) % 2 == 0) {
                        stencilQuadCount++;
                    }
                }
            }

            // Build checkerboard vertex array (6 verts per quad)
            int checkerboardVertexCount = stencilQuadCount * 6;
            var checkerboardVertices = new ShaderLayouts.NvgVertex[checkerboardVertexCount];
            int writeIdx = 0;

            for (int cellY = cellStartY; cellY < cellEndY; cellY++) {
                for (int cellX = cellStartX; cellX < cellEndX; cellX++) {
                    if ((cellX + cellY) % 2 == 0) {
                        float quadLeft = cellX * checkerCellSize;
                        float quadTop = cellY * checkerCellSize;
                        float quadRight = quadLeft + checkerCellSize;
                        float quadBottom = quadTop + checkerCellSize;

                        // Triangle 1: TL, TR, BR
                        checkerboardVertices[writeIdx++] = new ShaderLayouts.NvgVertex {
                            Position = new Vector2(quadLeft, quadTop), TexCoord = new Vector2(0, 1), Color = new RgbaFloat(1, 0, 1, 1)
                        };
                        checkerboardVertices[writeIdx++] = new ShaderLayouts.NvgVertex {
                            Position = new Vector2(quadRight, quadTop), TexCoord = new Vector2(0, 1), Color = new RgbaFloat(1, 0, 1, 1)
                        };
                        checkerboardVertices[writeIdx++] = new ShaderLayouts.NvgVertex {
                            Position = new Vector2(quadRight, quadBottom), TexCoord = new Vector2(0, 1), Color = new RgbaFloat(1, 0, 1, 1)
                        };
                        // Triangle 2: TL, BR, BL
                        checkerboardVertices[writeIdx++] = new ShaderLayouts.NvgVertex {
                            Position = new Vector2(quadLeft, quadTop), TexCoord = new Vector2(0, 1), Color = new RgbaFloat(1, 0, 1, 1)
                        };
                        checkerboardVertices[writeIdx++] = new ShaderLayouts.NvgVertex {
                            Position = new Vector2(quadRight, quadBottom), TexCoord = new Vector2(0, 1), Color = new RgbaFloat(1, 0, 1, 1)
                        };
                        checkerboardVertices[writeIdx++] = new ShaderLayouts.NvgVertex {
                            Position = new Vector2(quadLeft, quadBottom), TexCoord = new Vector2(0, 1), Color = new RgbaFloat(1, 0, 1, 1)
                        };
                    }
                }
            }

            // Upload checkerboard vertices to a temporary section of the vertex buffer
            // We need to upload AFTER the main vertex batch, so we use a separate buffer upload
            // Strategy: use _graphicsDevice.UpdateBuffer to append checkerboard data after the batch
            uint batchByteSize = (uint)(_vertexBatch.Count * Marshal.SizeOf<ShaderLayouts.NvgVertex>());
            uint checkerByteSize = (uint)(checkerboardVertices.Length * Marshal.SizeOf<ShaderLayouts.NvgVertex>());
            uint totalRequired = batchByteSize + checkerByteSize;

            // Resize vertex buffer if needed
            if (_vertexBuffer!.SizeInBytes < totalRequired) {
                _vertexBuffer.Dispose();
                _vertexBuffer = _graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(
                    totalRequired * 2,
                    BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                // Re-upload the main batch since we recreated the buffer
                _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, _vertexBatch.ToArray());
                commandList.SetVertexBuffer(0, _vertexBuffer);
            }

            // Append checkerboard vertices after the main batch
            uint checkerboardVertexOffset = (uint)_vertexBatch.Count;
            _graphicsDevice.UpdateBuffer(_vertexBuffer, batchByteSize, checkerboardVertices);

            // Pass 1a: Write stencil checkerboard — "debug clear" (no color output)
            commandList.SetPipeline(_stencilFillPipeline);
            commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);
            commandList.Draw((uint)checkerboardVertexCount, 1, checkerboardVertexOffset, 0);

            // Pass 1b: Write real NVG path triangles into stencil ON TOP of checkerboard
            // These use the same _stencilFillPipeline (Replace ref=1), so they'll fill in
            // the checkerboard gaps wherever the NVG triangles cover pixels.
            commandList.Draw(
                (uint)drawCall.StencilFillVertexCount, 1,
                (uint)drawCall.VertexOffset, 0);

            // Pass 2: Draw the existing cover quad where stencil != 0
            commandList.SetPipeline(_stencilCoverSolidPipeline);
            commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);
            commandList.Draw(6, 1, (uint)drawCall.CoverQuadVertexOffset, 0);
        }

        private bool _debugLogNextStencilCheckerboard = true;
    }
}