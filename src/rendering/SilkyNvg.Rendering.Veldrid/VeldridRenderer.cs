using SilkyNvg.Blending;
using SilkyNvg.Images;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using Veldrid.SPIRV;

namespace SilkyNvg.Rendering.Veldrid
{
    /// <summary>
    /// Minimal Veldrid NanoVG renderer - supports solid fill only.
    /// This is a proof-of-concept for evaluation purposes.
    /// </summary>
    public sealed class VeldridRenderer : INvgRenderer
    {
        private readonly GraphicsDevice _graphicsDevice;
        private readonly bool _edgeAntiAlias;

        // Pipeline resources
        private Pipeline? _solidFillPipeline;
        private DeviceBuffer? _vertexBuffer;
        private DeviceBuffer? _viewSizeUniformBuffer;
        private ResourceLayout? _resourceLayout;
        private ResourceSet? _resourceSet;
        private Shader[]? _shaders;

        // Batching
        private readonly List<NvgVertex> _vertexBatch = new(4096);
        private readonly List<DrawCall> _drawCalls = new(64);
        private SizeF _viewportSize;
        private bool _isInitialized;

        // Active command list for rendering - MUST be set before BeginFrame/EndFrame!
        // Veldrid requires explicit CommandList management (unlike OpenGL's immediate mode).
        private CommandList? _activeRenderCommandList;

        // Vertex format: position (x,y) + texcoord (u,v) + color (rgba)
        // IMPORTANT: Must use Vector2 and RgbaFloat types to match Veldrid's expected layout!
        [StructLayout(LayoutKind.Sequential)]
        private struct NvgVertex
        {
            public Vector2 Position;    // 8 bytes
            public Vector2 TexCoord;    // 8 bytes
            public RgbaFloat Color;     // 16 bytes

            public NvgVertex(Vertex vertex, Colour color)
            {
                Position = new Vector2(vertex.X, vertex.Y);
                TexCoord = new Vector2(vertex.U, vertex.V);
                Color = new RgbaFloat(color.R, color.G, color.B, color.A);
            }
        }

        private struct DrawCall
        {
            public int VertexOffset;
            public int VertexCount;
            public BlendStateDescription BlendState;
        }

        public bool EdgeAntiAlias => _edgeAntiAlias;

        public VeldridRenderer(GraphicsDevice graphicsDevice, bool edgeAntiAlias = true)
        {
            _graphicsDevice = graphicsDevice;
            _edgeAntiAlias = edgeAntiAlias;
        }

        public bool Create()
        {
            if (_isInitialized)
            {
                return true;
            }

            try
            {
                CreateShaders();
                CreatePipeline();
                CreateBuffers();
                _isInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"VeldridRenderer.Create failed: {ex.Message}");
                return false;
            }
        }

        private void CreateShaders()
        {
            var vertexShaderDesc = new ShaderDescription(
                ShaderStages.Vertex,
                GetVertexShaderBytes(),
                "main");

            var fragmentShaderDesc = new ShaderDescription(
                ShaderStages.Fragment,
                GetFragmentShaderBytes(),
                "main");

            _shaders = _graphicsDevice.ResourceFactory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);
        }

        private void CreatePipeline()
        {
            var factory = _graphicsDevice.ResourceFactory;

            // Vertex layout: Position at location 0, skip 8 bytes (TexCoord), Color at location 1
            // The struct HAS TexCoord for future texture support, but shader doesn't use it yet
            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4, 16)); // Offset 16 bytes (skip Position + TexCoord)

            // Resource layout for view size uniform
            _resourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ViewSize", ResourceKind.UniformBuffer, ShaderStages.Vertex)));

            var pipelineDesc = new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: false,
                    scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _resourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { vertexLayout },
                    _shaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _solidFillPipeline = factory.CreateGraphicsPipeline(pipelineDesc);
        }

        private void CreateBuffers()
        {
            var factory = _graphicsDevice.ResourceFactory;

            // Dynamic vertex buffer (will resize as needed)
            _vertexBuffer = factory.CreateBuffer(new BufferDescription(
                4096 * (uint)Marshal.SizeOf<NvgVertex>(),
                BufferUsage.VertexBuffer | BufferUsage.Dynamic));

            // View size uniform buffer
            _viewSizeUniformBuffer = factory.CreateBuffer(new BufferDescription(
                16, // vec2 padded to 16 bytes
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _resourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _resourceLayout,
                _viewSizeUniformBuffer));
        }

        private byte[] GetVertexShaderBytes()
        {
            // GLSL vertex shader (SPIRV-Cross compatible)
            string vertexShaderCode = @"
#version 450

layout(set = 0, binding = 0) uniform ViewSize {
    vec2 viewSize;
};

layout(location = 0) in vec2 Position;
layout(location = 1) in vec4 Color;

layout(location = 0) out vec4 frag_Color;

void main() {
    frag_Color = Color;
    gl_Position = vec4(2.0 * Position.x / viewSize.x - 1.0, 1.0 - 2.0 * Position.y / viewSize.y, 0.0, 1.0);
}
";
            return System.Text.Encoding.UTF8.GetBytes(vertexShaderCode);
        }

        private byte[] GetFragmentShaderBytes()
        {
            // GLSL fragment shader (SPIRV-Cross compatible)
            string fragmentShaderCode = @"
#version 450

layout(location = 0) in vec4 frag_Color;

layout(location = 0) out vec4 out_Color;

void main() {
    out_Color = frag_Color;
}
";
            return System.Text.Encoding.UTF8.GetBytes(fragmentShaderCode);
        }

        public int CreateTexture(Texture type, Size size, ImageFlags imageFlags, ReadOnlySpan<byte> data)
        {
            // Minimal implementation - return dummy texture ID
            return 1;
        }

        public bool DeleteTexture(int image)
        {
            return true;
        }

        public bool UpdateTexture(int image, System.Drawing.Rectangle bounds, ReadOnlySpan<byte> data)
        {
            return true;
        }

        public bool GetTextureSize(int image, out Size size)
        {
            size = new Size(1, 1);
            return true;
        }

        public void Viewport(SizeF size, float devicePixelRatio)
        {
            _viewportSize = size;
        }

        /// <summary>
        /// Sets the active CommandList for rendering. MUST be called before BeginFrame/EndFrame!
        /// This is Veldrid-specific - unlike OpenGL's immediate mode, Veldrid requires
        /// explicit CommandList management to ensure proper draw ordering.
        /// </summary>
        /// <param name="commandList">The CommandList from the engine's render loop</param>
        public void SetActiveCommandList(CommandList commandList)
        {
            _activeRenderCommandList = commandList;
        }

        public void Cancel()
        {
            _vertexBatch.Clear();
            _drawCalls.Clear();
        }

        public void Flush()
        {
            Console.WriteLine($"[VeldridRenderer] Flush called - vertices: {_vertexBatch.Count}, drawCalls: {_drawCalls.Count}, initialized: {_isInitialized}");
            Console.WriteLine($"[VeldridRenderer] Viewport: {_viewportSize.Width}x{_viewportSize.Height}");

            // FAIL-FAST: CommandList must be set before Flush
            if (_activeRenderCommandList == null)
            {
                throw new InvalidOperationException(
                    "VeldridRenderer.Flush() called without an active CommandList! " +
                    "You must call SetActiveCommandList(commandList) before BeginFrame/EndFrame. " +
                    "This is required to properly order SilkyNVG rendering with your rendering pipeline.");
            }

            if (!_isInitialized)
            {
                Console.WriteLine("[VeldridRenderer] ERROR: Not initialized!");
                Cancel();
                return;
            }

            // DEBUG: If no vertices from NVG, add a hard-coded test triangle
            if (_vertexBatch.Count == 0)
            {
                Console.WriteLine("[VeldridRenderer] No vertices from NVG - adding debug triangle");
                // Add a bright red triangle in the center as a debug indicator
                float cx = _viewportSize.Width / 2;
                float cy = _viewportSize.Height / 2;
                float size = 100;

                _vertexBatch.Add(new NvgVertex { Position = new Vector2(cx, cy - size), TexCoord = Vector2.Zero, Color = new RgbaFloat(1, 0, 0, 1) });
                _vertexBatch.Add(new NvgVertex { Position = new Vector2(cx - size, cy + size), TexCoord = Vector2.Zero, Color = new RgbaFloat(1, 0, 0, 1) });
                _vertexBatch.Add(new NvgVertex { Position = new Vector2(cx + size, cy + size), TexCoord = Vector2.Zero, Color = new RgbaFloat(1, 0, 0, 1) });

                _drawCalls.Add(new DrawCall { VertexOffset = 0, VertexCount = 3, BlendState = BlendStateDescription.SingleAlphaBlend });
            }

            // Use the engine's active CommandList - framebuffer already set by engine
            var commandList = _activeRenderCommandList;

            // Update view size uniform (use Vector4 for proper 16-byte alignment)
            var viewSizeData = new Vector4(_viewportSize.Width, _viewportSize.Height, 0, 0);
            _graphicsDevice.UpdateBuffer(_viewSizeUniformBuffer, 0, viewSizeData);

            // Resize vertex buffer if needed
            uint requiredSize = (uint)(_vertexBatch.Count * Marshal.SizeOf<NvgVertex>());
            if (_vertexBuffer!.SizeInBytes < requiredSize)
            {
                _vertexBuffer.Dispose();
                _vertexBuffer = _graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(
                    requiredSize * 2,
                    BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            }

            // Upload vertices
            var vertexArray = _vertexBatch.ToArray();
            Console.WriteLine($"[VeldridRenderer] Uploading {vertexArray.Length} vertices");
            Console.WriteLine($"  Vertex 0: pos={vertexArray[0].Position} uv={vertexArray[0].TexCoord} color={vertexArray[0].Color}");
            if (vertexArray.Length > 6)
            {
                Console.WriteLine($"  Vertex 6: pos={vertexArray[6].Position} uv={vertexArray[6].TexCoord} color={vertexArray[6].Color}");
            }
            _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, vertexArray);

            // Set pipeline state
            commandList.SetPipeline(_solidFillPipeline);
            commandList.SetVertexBuffer(0, _vertexBuffer);
            commandList.SetGraphicsResourceSet(0, _resourceSet);

            // Execute draw calls
            foreach (var drawCall in _drawCalls)
            {
                Console.WriteLine($"[VeldridRenderer] Draw: offset={drawCall.VertexOffset}, count={drawCall.VertexCount}");
                commandList.Draw((uint)drawCall.VertexCount, 1, (uint)drawCall.VertexOffset, 0);
            }

            // Do NOT End/Submit/Dispose - the engine's game loop handles that
            Console.WriteLine("[VeldridRenderer] Flush complete");
            Cancel();
        }

        public void Fill(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, float fringe, RectangleF bounds, IReadOnlyList<Path> paths)
        {
            Console.WriteLine($"[VeldridRenderer] Fill called - paths: {paths.Count}, bounds: {bounds}");

            // Get the inner color (solid fill)
            var fillColor = paint.InnerColour;
            Console.WriteLine($"[VeldridRenderer] Fill color: ({fillColor.R}, {fillColor.G}, {fillColor.B}, {fillColor.A})");

            int vertexOffset = _vertexBatch.Count;

            foreach (var path in paths)
            {
                Console.WriteLine($"[VeldridRenderer] Path fill vertices: {path.Fill.Count}, stroke vertices: {path.Stroke.Count}");

                // For convex paths, add fill vertices directly as triangle fan
                if (path.Fill.Count >= 3)
                {
                    // Convert triangle fan to triangle list
                    var firstVertex = path.Fill[0];
                    for (int i = 1; i < path.Fill.Count - 1; i++)
                    {
                        _vertexBatch.Add(new NvgVertex(firstVertex, fillColor));
                        _vertexBatch.Add(new NvgVertex(path.Fill[i], fillColor));
                        _vertexBatch.Add(new NvgVertex(path.Fill[i + 1], fillColor));
                    }
                }
            }

            int vertexCount = _vertexBatch.Count - vertexOffset;
            Console.WriteLine($"[VeldridRenderer] Fill added {vertexCount} vertices");

            if (vertexCount > 0)
            {
                _drawCalls.Add(new DrawCall
                {
                    VertexOffset = vertexOffset,
                    VertexCount = vertexCount,
                    BlendState = BlendStateDescription.SingleAlphaBlend
                });
            }
        }

        public void Stroke(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, float fringe, float strokeWidth, IReadOnlyList<Path> paths)
        {
            // Get the stroke color
            var strokeColor = paint.InnerColour;

            int vertexOffset = _vertexBatch.Count;

            foreach (var path in paths)
            {
                // Stroke vertices are already tesselated as triangle strips
                if (path.Stroke.Count >= 3)
                {
                    // Convert triangle strip to triangle list
                    for (int i = 0; i < path.Stroke.Count - 2; i++)
                    {
                        if (i % 2 == 0)
                        {
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i], strokeColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 1], strokeColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 2], strokeColor));
                        }
                        else
                        {
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 1], strokeColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i], strokeColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 2], strokeColor));
                        }
                    }
                }
            }

            int vertexCount = _vertexBatch.Count - vertexOffset;
            if (vertexCount > 0)
            {
                _drawCalls.Add(new DrawCall
                {
                    VertexOffset = vertexOffset,
                    VertexCount = vertexCount,
                    BlendState = BlendStateDescription.SingleAlphaBlend
                });
            }
        }

        public void Triangles(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, ICollection<Vertex> vertices, float fringeWidth)
        {
            // Minimal implementation for text rendering (not supported in this version)
            // Just add vertices with color
            var color = paint.InnerColour;
            int vertexOffset = _vertexBatch.Count;

            foreach (var vertex in vertices)
            {
                _vertexBatch.Add(new NvgVertex(vertex, color));
            }

            int vertexCount = _vertexBatch.Count - vertexOffset;
            if (vertexCount > 0)
            {
                _drawCalls.Add(new DrawCall
                {
                    VertexOffset = vertexOffset,
                    VertexCount = vertexCount,
                    BlendState = BlendStateDescription.SingleAlphaBlend
                });
            }
        }

        /// <summary>
        /// DEBUG: Draw a hardcoded test triangle bypassing NVG completely.
        /// This validates the Veldrid pipeline is working.
        /// Uses the passed-in CommandList from the engine to maintain proper draw order.
        /// </summary>
        public void DrawTestTriangle(CommandList commandList)
        {
            if (!_isInitialized)
            {
                Console.WriteLine("[VeldridRenderer] DrawTestTriangle: Not initialized, calling Create()");
                if (!Create())
                {
                    Console.WriteLine("[VeldridRenderer] DrawTestTriangle: Create() failed!");
                    return;
                }
            }

            float w = _viewportSize.Width > 0 ? _viewportSize.Width : 800;
            float h = _viewportSize.Height > 0 ? _viewportSize.Height : 600;

            Console.WriteLine($"[VeldridRenderer] DrawTestTriangle: viewport {w}x{h}");

            // Create a simple triangle in screen coordinates
            // Triangle in the center of the screen
            var vertices = new NvgVertex[]
            {
                new NvgVertex { Position = new Vector2(w * 0.5f, h * 0.2f), TexCoord = Vector2.Zero, Color = new RgbaFloat(1, 0, 0, 1) }, // Top (red)
                new NvgVertex { Position = new Vector2(w * 0.2f, h * 0.8f), TexCoord = Vector2.Zero, Color = new RgbaFloat(0, 1, 0, 1) }, // Bottom-left (green)
                new NvgVertex { Position = new Vector2(w * 0.8f, h * 0.8f), TexCoord = Vector2.Zero, Color = new RgbaFloat(0, 0, 1, 1) }, // Bottom-right (blue)
            };

            Console.WriteLine($"[VeldridRenderer] DrawTestTriangle: Triangle vertices:");
            Console.WriteLine($"  Top: ({vertices[0].Position.X}, {vertices[0].Position.Y}) color: {vertices[0].Color}");
            Console.WriteLine($"  BotLeft: ({vertices[1].Position.X}, {vertices[1].Position.Y}) color: {vertices[1].Color}");
            Console.WriteLine($"  BotRight: ({vertices[2].Position.X}, {vertices[2].Position.Y}) color: {vertices[2].Color}");

            // Update buffers using graphicsDevice BEFORE commandList operations
            var viewSizeData = new Vector4(w, h, 0, 0);
            _graphicsDevice.UpdateBuffer(_viewSizeUniformBuffer, 0, viewSizeData);
            _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, vertices);

            // Use the passed-in commandList - framebuffer already set by engine
            commandList.SetPipeline(_solidFillPipeline);
            commandList.SetVertexBuffer(0, _vertexBuffer);
            commandList.SetGraphicsResourceSet(0, _resourceSet);

            // Draw the triangle
            commandList.Draw(3, 1, 0, 0);

            Console.WriteLine("[VeldridRenderer] DrawTestTriangle: Complete");
        }

        public void Dispose()
        {
            _solidFillPipeline?.Dispose();
            _vertexBuffer?.Dispose();
            _viewSizeUniformBuffer?.Dispose();
            _resourceSet?.Dispose();
            _resourceLayout?.Dispose();

            if (_shaders != null)
            {
                foreach (var shader in _shaders)
                {
                    shader.Dispose();
                }
            }
        }
    }
}