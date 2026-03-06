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

        // Pipeline resources - solid fill (shapes)
        private Pipeline? _solidFillPipeline;
        private DeviceBuffer? _vertexBuffer;
        private DeviceBuffer? _viewSizeUniformBuffer;
        private ResourceLayout? _solidFillResourceLayout;
        private ResourceSet? _solidFillResourceSet;
        private Shader[]? _solidFillShaders;

        // Pipeline resources - textured (font atlas text rendering)
        private Pipeline? _texturedPipeline;
        private ResourceLayout? _texturedResourceLayout;
        private Shader[]? _texturedShaders;
        private Sampler? _fontAtlasSampler;

        // Texture management for font atlas and images
        private readonly Dictionary<int, ManagedTexture> _textureRegistry = new();
        private int _nextTextureId = 1;

        // Cached ResourceSets for textured draw calls (keyed by texture ID)
        private readonly Dictionary<int, ResourceSet> _texturedResourceSetCache = new();

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
            public int TextureId; // 0 = solid fill, non-zero = textured (font atlas or image)
            public BlendStateDescription BlendState;
        }

        /// <summary>
        /// Tracks a Veldrid texture created by NVG (font atlas or image)
        /// </summary>
        private struct ManagedTexture
        {
            public global::Veldrid.Texture GpuTexture;
            public TextureView TextureView;
            public Size TextureSize;
            public ImageFlags CreationFlags;
            public bool IsAlphaOnly; // Font atlas uses R8_UNorm (alpha only)
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
            // Solid fill shaders (shapes without textures)
            var solidVertexShaderDesc = new ShaderDescription(
                ShaderStages.Vertex,
                GetSolidFillVertexShaderBytes(),
                "main");

            var solidFragmentShaderDesc = new ShaderDescription(
                ShaderStages.Fragment,
                GetSolidFillFragmentShaderBytes(),
                "main");

            _solidFillShaders = _graphicsDevice.ResourceFactory.CreateFromSpirv(solidVertexShaderDesc, solidFragmentShaderDesc);

            // Textured shaders (font atlas text rendering)
            var texturedVertexShaderDesc = new ShaderDescription(
                ShaderStages.Vertex,
                GetTexturedVertexShaderBytes(),
                "main");

            var texturedFragmentShaderDesc = new ShaderDescription(
                ShaderStages.Fragment,
                GetTexturedFragmentShaderBytes(),
                "main");

            _texturedShaders = _graphicsDevice.ResourceFactory.CreateFromSpirv(texturedVertexShaderDesc, texturedFragmentShaderDesc);
        }

        private void CreatePipeline()
        {
            var factory = _graphicsDevice.ResourceFactory;

            // === SOLID FILL PIPELINE (shapes without textures) ===
            // Vertex layout: Position at location 0, skip 8 bytes (TexCoord), Color at location 1
            var solidFillVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4, 16)); // Offset 16 bytes (skip Position + TexCoord)

            // Resource layout for view size uniform only
            _solidFillResourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ViewSize", ResourceKind.UniformBuffer, ShaderStages.Vertex)));

            var solidFillPipelineDesc = new GraphicsPipelineDescription {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: false,
                    scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _solidFillResourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { solidFillVertexLayout },
                    _solidFillShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _solidFillPipeline = factory.CreateGraphicsPipeline(solidFillPipelineDesc);

            // === TEXTURED PIPELINE (font atlas text rendering) ===
            // Vertex layout: Position, TexCoord, Color - all with explicit offsets
            var texturedVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2, 8),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4, 16));

            // Resource layout: ViewSize uniform + font atlas texture + sampler
            _texturedResourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ViewSize", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("FontAtlas", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("FontAtlasSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // Create sampler for font atlas (linear filtering for smooth text)
            _fontAtlasSampler = factory.CreateSampler(new SamplerDescription {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = SamplerFilter.MinLinear_MagLinear_MipLinear,
                MinimumLod = 0,
                MaximumLod = 0
            });

            var texturedPipelineDesc = new GraphicsPipelineDescription {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: false,
                    scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _texturedResourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { texturedVertexLayout },
                    _texturedShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _texturedPipeline = factory.CreateGraphicsPipeline(texturedPipelineDesc);
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

            // Resource set for solid fill pipeline (just view size uniform)
            _solidFillResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _solidFillResourceLayout,
                _viewSizeUniformBuffer));
        }

        private byte[] GetSolidFillVertexShaderBytes()
        {
            // GLSL vertex shader for solid fill (no texture)
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

        private byte[] GetSolidFillFragmentShaderBytes()
        {
            // GLSL fragment shader for solid fill (no texture)
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

        private byte[] GetTexturedVertexShaderBytes()
        {
            // GLSL vertex shader for textured rendering (font atlas)
            string vertexShaderCode = @"
#version 450

layout(set = 0, binding = 0) uniform ViewSize {
    vec2 viewSize;
};

layout(location = 0) in vec2 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 2) in vec4 Color;

layout(location = 0) out vec2 frag_TexCoord;
layout(location = 1) out vec4 frag_Color;

void main() {
    frag_TexCoord = TexCoord;
    frag_Color = Color;
    gl_Position = vec4(2.0 * Position.x / viewSize.x - 1.0, 1.0 - 2.0 * Position.y / viewSize.y, 0.0, 1.0);
}
";
            return System.Text.Encoding.UTF8.GetBytes(vertexShaderCode);
        }

        private byte[] GetTexturedFragmentShaderBytes()
        {
            // GLSL fragment shader for textured rendering (font atlas)
            // Font atlas is R8_UNorm (alpha only), sample red channel as alpha
            string fragmentShaderCode = @"
#version 450

layout(set = 0, binding = 1) uniform texture2D FontAtlas;
layout(set = 0, binding = 2) uniform sampler FontAtlasSampler;

layout(location = 0) in vec2 frag_TexCoord;
layout(location = 1) in vec4 frag_Color;

layout(location = 0) out vec4 out_Color;

void main() {
    float alpha = texture(sampler2D(FontAtlas, FontAtlasSampler), frag_TexCoord).r;
    out_Color = vec4(frag_Color.rgb, frag_Color.a * alpha);
}
";
            return System.Text.Encoding.UTF8.GetBytes(fragmentShaderCode);
        }

        public int CreateTexture(Texture type, Size size, ImageFlags imageFlags, ReadOnlySpan<byte> data)
        {
            var factory = _graphicsDevice.ResourceFactory;

            // Font atlas is alpha-only (R8_UNorm), RGBA images use R8G8B8A8_UNorm
            bool isAlphaOnly = (type == Texture.Alpha);
            var pixelFormat = isAlphaOnly ? PixelFormat.R8_UNorm : PixelFormat.R8_G8_B8_A8_UNorm;

            // Create the texture
            var textureDescription = TextureDescription.Texture2D(
                (uint)size.Width,
                (uint)size.Height,
                mipLevels: 1,
                arrayLayers: 1,
                pixelFormat,
                TextureUsage.Sampled);

            var gpuTexture = factory.CreateTexture(textureDescription);
            var textureView = factory.CreateTextureView(gpuTexture);

            // Upload initial data if provided
            if (!data.IsEmpty) {
                uint bytesPerPixel = isAlphaOnly ? 1u : 4u;
                _graphicsDevice.UpdateTexture(
                    gpuTexture,
                    data.ToArray(),
                    0, 0, 0,
                    (uint)size.Width, (uint)size.Height, 1,
                    0, 0);
            }

            int textureId = _nextTextureId++;
            _textureRegistry[textureId] = new ManagedTexture {
                GpuTexture = gpuTexture,
                TextureView = textureView,
                TextureSize = size,
                CreationFlags = imageFlags,
                IsAlphaOnly = isAlphaOnly
            };

            Console.WriteLine($"[VeldridRenderer] CreateTexture: id={textureId}, size={size.Width}x{size.Height}, alpha={isAlphaOnly}");
            return textureId;
        }

        public bool DeleteTexture(int textureId)
        {
            if (!_textureRegistry.TryGetValue(textureId, out var managedTexture)) {
                return false;
            }

            // Invalidate cached ResourceSet for this texture
            if (_texturedResourceSetCache.TryGetValue(textureId, out var cachedResourceSet)) {
                cachedResourceSet.Dispose();
                _texturedResourceSetCache.Remove(textureId);
            }

            managedTexture.TextureView.Dispose();
            managedTexture.GpuTexture.Dispose();
            _textureRegistry.Remove(textureId);

            Console.WriteLine($"[VeldridRenderer] DeleteTexture: id={textureId}");
            return true;
        }

        public bool UpdateTexture(int textureId, System.Drawing.Rectangle bounds, ReadOnlySpan<byte> data)
        {
            if (!_textureRegistry.TryGetValue(textureId, out var managedTexture)) {
                Console.WriteLine($"[VeldridRenderer] UpdateTexture: texture {textureId} not found!");
                return false;
            }

            if (data.IsEmpty) {
                return true; // Nothing to upload
            }

            uint bytesPerPixel = managedTexture.IsAlphaOnly ? 1u : 4u;
            int atlasWidth = managedTexture.TextureSize.Width;
            int regionWidth = bounds.Width;
            int regionHeight = bounds.Height;
            uint regionRowBytes = (uint)(regionWidth * bytesPerPixel);
            uint atlasRowBytes = (uint)(atlasWidth * bytesPerPixel);
            uint expectedRegionSize = regionRowBytes * (uint)regionHeight;

            // FontStash passes the ENTIRE atlas data but with dirty region bounds.
            // We must extract just the dirty sub-rectangle for Veldrid's tightly-packed upload.
            byte[] regionPixelData;
            if ((uint)data.Length == expectedRegionSize) {
                // Data is already the right size for the region
                regionPixelData = data.ToArray();
            } else {
                // Extract sub-rectangle from full atlas data
                regionPixelData = new byte[expectedRegionSize];
                for (int row = 0; row < regionHeight; row++) {
                    int sourceOffset = (int)(((bounds.Y + row) * atlasRowBytes) + (bounds.X * bytesPerPixel));
                    int destOffset = (int)(row * regionRowBytes);
                    data.Slice(sourceOffset, (int)regionRowBytes).CopyTo(regionPixelData.AsSpan(destOffset));
                }
            }

            _graphicsDevice.UpdateTexture(
                managedTexture.GpuTexture,
                regionPixelData,
                (uint)bounds.X, (uint)bounds.Y, 0,
                (uint)regionWidth, (uint)regionHeight, 1,
                0, 0);

            Console.WriteLine($"[VeldridRenderer] UpdateTexture: id={textureId}, region=({bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}), extracted={data.Length != expectedRegionSize}");
            return true;
        }

        public bool GetTextureSize(int textureId, out Size size)
        {
            if (!_textureRegistry.TryGetValue(textureId, out var managedTexture)) {
                size = new Size(1, 1);
                return false;
            }

            size = managedTexture.TextureSize;
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

        /// <summary>
        /// Gets or creates a ResourceSet for the textured pipeline bound to the given texture.
        /// Caches ResourceSets so they aren't recreated every frame.
        /// </summary>
        private ResourceSet GetOrCreateTexturedResourceSet(int textureId)
        {
            if (_texturedResourceSetCache.TryGetValue(textureId, out var existingResourceSet)) {
                return existingResourceSet;
            }

            if (!_textureRegistry.TryGetValue(textureId, out var managedTexture)) {
                throw new InvalidOperationException(
                    $"VeldridRenderer: Texture ID {textureId} not found in registry. " +
                    "This means a draw call references a texture that was never created or was already deleted.");
            }

            var newTexturedResourceSet = _graphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                _texturedResourceLayout,
                _viewSizeUniformBuffer,
                managedTexture.TextureView,
                _fontAtlasSampler));

            _texturedResourceSetCache[textureId] = newTexturedResourceSet;
            Console.WriteLine($"[VeldridRenderer] Created textured ResourceSet for texture {textureId}");
            return newTexturedResourceSet;
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

            // Set shared vertex buffer (same layout for both pipelines)
            commandList.SetVertexBuffer(0, _vertexBuffer);

            // Execute draw calls, switching pipeline per call based on TextureId
            int lastBoundTextureId = -1; // Track to avoid redundant pipeline switches
            foreach (var drawCall in _drawCalls)
            {
                if (drawCall.TextureId != lastBoundTextureId) {
                    if (drawCall.TextureId == 0) {
                        // Solid fill: no texture
                        commandList.SetPipeline(_solidFillPipeline);
                        commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);
                    } else {
                        // Textured: bind font atlas or image texture
                        var texturedResourceSet = GetOrCreateTexturedResourceSet(drawCall.TextureId);
                        commandList.SetPipeline(_texturedPipeline);
                        commandList.SetGraphicsResourceSet(0, texturedResourceSet);
                    }
                    lastBoundTextureId = drawCall.TextureId;
                }

                Console.WriteLine($"[VeldridRenderer] Draw: offset={drawCall.VertexOffset}, count={drawCall.VertexCount}, texture={drawCall.TextureId}");
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
                    TextureId = paint.Image,
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
                    TextureId = paint.Image,
                    BlendState = BlendStateDescription.SingleAlphaBlend
                });
            }
        }

        public void Triangles(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, ICollection<Vertex> vertices, float fringeWidth)
        {
            // Text rendering: paint.Image contains the font atlas texture ID
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
                    TextureId = paint.Image,
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
            commandList.SetGraphicsResourceSet(0, _solidFillResourceSet);

            // Draw the triangle
            commandList.Draw(3, 1, 0, 0);

            Console.WriteLine("[VeldridRenderer] DrawTestTriangle: Complete");
        }

        public void Dispose()
        {
            // Dispose all managed textures
            foreach (var kvp in _textureRegistry) {
                kvp.Value.TextureView.Dispose();
                kvp.Value.GpuTexture.Dispose();
            }
            _textureRegistry.Clear();

            // Dispose cached textured ResourceSets
            foreach (var kvp in _texturedResourceSetCache) {
                kvp.Value.Dispose();
            }
            _texturedResourceSetCache.Clear();

            // Dispose solid fill pipeline resources
            _solidFillPipeline?.Dispose();
            _solidFillResourceSet?.Dispose();
            _solidFillResourceLayout?.Dispose();
            if (_solidFillShaders != null) {
                foreach (var shader in _solidFillShaders) {
                    shader.Dispose();
                }
            }

            // Dispose textured pipeline resources
            _texturedPipeline?.Dispose();
            _texturedResourceLayout?.Dispose();
            _fontAtlasSampler?.Dispose();
            if (_texturedShaders != null) {
                foreach (var shader in _texturedShaders) {
                    shader.Dispose();
                }
            }

            // Dispose shared resources
            _vertexBuffer?.Dispose();
            _viewSizeUniformBuffer?.Dispose();
        }
    }
}