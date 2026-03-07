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
            uint requiredVertexBufferSize = (uint)(_vertexBatch.Count * Marshal.SizeOf<NvgVertex>());
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

            // Execute draw calls, switching pipeline and scissor per call
            int lastBoundTextureId = -1;
            bool lastScissorWasFullViewport = true;
            foreach (var drawCall in _drawCalls)
            {
                // Switch pipeline if texture changed
                if (drawCall.TextureId != lastBoundTextureId)
                {
                    if (drawCall.TextureId == 0)
                    {
                        commandList.SetPipeline(_solidFillPipeline);
                        commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);
                    }
                    else
                    {
                        var texturedResourceSet = _textureRegistry.GetOrCreateTexturedResourceSet(drawCall.TextureId);
                        commandList.SetPipeline(_texturedPipeline);
                        commandList.SetGraphicsResourceSet(0, texturedResourceSet);
                    }
                    lastBoundTextureId = drawCall.TextureId;
                }

                // Apply scissor rect (scale from NVG coordinates to framebuffer pixels)
                if (drawCall.HasScissor)
                {
                    uint scaledScissorX = (uint)Math.Max(0, (int)(drawCall.ScissorX * nvgToFramebufferScaleX));
                    uint scaledScissorY = (uint)Math.Max(0, (int)(drawCall.ScissorY * nvgToFramebufferScaleY));
                    uint scaledScissorWidth = (uint)(drawCall.ScissorWidth * nvgToFramebufferScaleX);
                    uint scaledScissorHeight = (uint)(drawCall.ScissorHeight * nvgToFramebufferScaleY);
                    commandList.SetScissorRect(0,
                        scaledScissorX, scaledScissorY,
                        scaledScissorWidth, scaledScissorHeight);
                    lastScissorWasFullViewport = false;
                }
                else if (!lastScissorWasFullViewport)
                {
                    commandList.SetScissorRect(0, 0, 0, fullFramebufferWidth, fullFramebufferHeight);
                    lastScissorWasFullViewport = true;
                }

                commandList.Draw((uint)drawCall.VertexCount, 1, (uint)drawCall.VertexOffset, 0);
            }

            Cancel();
        }

        /// <summary>
        /// DEBUG: Draw a hardcoded test triangle bypassing NVG completely.
        /// Validates the Veldrid pipeline is working.
        /// </summary>
        public void DrawTestTriangle(CommandList commandList)
        {
            if (!_isInitialized)
            {
                if (!Create())
                {
                    return;
                }
            }

            float viewWidth = _viewportSize.Width > 0 ? _viewportSize.Width : 800;
            float viewHeight = _viewportSize.Height > 0 ? _viewportSize.Height : 600;

            var testTriangleVertices = new NvgVertex[]
            {
                new NvgVertex { Position = new Vector2(viewWidth * 0.5f, viewHeight * 0.2f), TexCoord = Vector2.Zero, Color = new RgbaFloat(1, 0, 0, 1) },
                new NvgVertex { Position = new Vector2(viewWidth * 0.2f, viewHeight * 0.8f), TexCoord = Vector2.Zero, Color = new RgbaFloat(0, 1, 0, 1) },
                new NvgVertex { Position = new Vector2(viewWidth * 0.8f, viewHeight * 0.8f), TexCoord = Vector2.Zero, Color = new RgbaFloat(0, 0, 1, 1) },
            };

            var viewSizeData = new Vector4(viewWidth, viewHeight, 0, 0);
            _graphicsDevice.UpdateBuffer(_viewSizeUniformBuffer, 0, viewSizeData);
            _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, testTriangleVertices);

            commandList.SetPipeline(_solidFillPipeline);
            commandList.SetVertexBuffer(0, _vertexBuffer);
            commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);
            commandList.Draw(3, 1, 0, 0);
        }
    }
}