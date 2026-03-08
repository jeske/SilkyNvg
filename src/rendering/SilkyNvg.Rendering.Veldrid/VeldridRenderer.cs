using SilkyNvg.Images;
using System;
using System.Collections.Generic;
using System.Drawing;
using Veldrid;

namespace SilkyNvg.Rendering.Veldrid
{
    /// <summary>
    /// Veldrid NanoVG renderer backend.
    /// Supports solid fill, stroke, and textured (font atlas) rendering via separate GPU pipelines.
    /// Split across partial files:
    ///   VeldridRenderer.cs          - Core fields, constructor, lifecycle
    ///   VeldridRenderer_Pipelines.cs - Pipeline/shader/buffer creation
    ///   VeldridRenderer_Shaders.cs   - GLSL shader source strings
    ///   VeldridRenderer_DrawCalls.cs - Fill(), Stroke(), Triangles() batching
    ///   VeldridRenderer_Flush.cs     - Flush() draw dispatch + DrawTestTriangle()
    ///   TextureRegistry.cs           - Texture CRUD + ResourceSet caching (separate class)
    /// </summary>
    public sealed partial class VeldridRenderer : INvgRenderer
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

        // Pipeline resources - gradient (linear, radial, box gradients)
        private Pipeline? _gradientPipeline;
        private ResourceLayout? _gradientResourceLayout;
        private ResourceSet? _gradientResourceSet;
        private Shader[]? _gradientShaders;
        private DeviceBuffer? _paintUniformBuffer;

        // Pipeline resources - image pattern (RGBA texture fill with paintMat UV transform)
        private Pipeline? _imagePatternPipeline;
        private ResourceLayout? _imagePatternResourceLayout;
        private Shader[]? _imagePatternShaders;
        private Sampler? _imagePatternSampler;
        private readonly Dictionary<int, ResourceSet> _imagePatternResourceSetCache = new();

        // Texture management (font atlas, images, ResourceSet caching)
        private TextureRegistry _textureRegistry = null!; // Initialized in CreateBuffers()

        // Batching
        private readonly List<ShaderLayouts.NvgVertex> _vertexBatch = new(4096);
        private readonly List<DrawCall> _drawCalls = new(64);
        private SizeF _viewportSize;
        private bool _isInitialized;
        private bool _debugLogNextFlush = true; // Log draw calls on first flush for debugging

        // Active command list for rendering - MUST be set before BeginFrame/EndFrame!
        // Veldrid requires explicit CommandList management (unlike OpenGL's immediate mode).
        private CommandList? _activeRenderCommandList;

        public bool EdgeAntiAlias => _edgeAntiAlias;

        public VeldridRenderer(GraphicsDevice graphicsDevice, bool edgeAntiAlias = true)
        {
            _graphicsDevice = graphicsDevice;
            _edgeAntiAlias = edgeAntiAlias;
        }

        public bool Create()
        {
            if (_isInitialized) {
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

        public void Viewport(SizeF size, float devicePixelRatio)
        {
            _viewportSize = size;
        }

        /// <summary>
        /// Sets the active CommandList for rendering. MUST be called before BeginFrame/EndFrame!
        /// This is Veldrid-specific - unlike OpenGL's immediate mode, Veldrid requires
        /// explicit CommandList management to ensure proper draw ordering.
        /// </summary>
        public void SetActiveCommandList(CommandList commandList)
        {
            _activeRenderCommandList = commandList;
        }

        public void Cancel()
        {
            _vertexBatch.Clear();
            _drawCalls.Clear();
        }

        // --- INvgRenderer texture methods delegate to TextureRegistry ---

        public int CreateTexture(Texture type, Size size, ImageFlags imageFlags, ReadOnlySpan<byte> data)
            => _textureRegistry.CreateTexture(type, size, imageFlags, data);

        public bool DeleteTexture(int textureId)
            => _textureRegistry.DeleteTexture(textureId);

        public bool UpdateTexture(int textureId, Rectangle bounds, ReadOnlySpan<byte> data)
            => _textureRegistry.UpdateTexture(textureId, bounds, data);

        public bool GetTextureSize(int textureId, out Size size)
            => _textureRegistry.GetTextureSize(textureId, out size);

        // --- Dispose ---

        public void Dispose()
        {
            _textureRegistry?.Dispose();

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

            // Dispose gradient pipeline resources
            _gradientPipeline?.Dispose();
            _gradientResourceSet?.Dispose();
            _gradientResourceLayout?.Dispose();
            _paintUniformBuffer?.Dispose();
            if (_gradientShaders != null) {
                foreach (var shader in _gradientShaders) {
                    shader.Dispose();
                }
            }

            // Dispose image pattern pipeline resources
            _imagePatternPipeline?.Dispose();
            _imagePatternResourceLayout?.Dispose();
            _imagePatternSampler?.Dispose();
            if (_imagePatternShaders != null) {
                foreach (var shader in _imagePatternShaders) {
                    shader.Dispose();
                }
            }
            foreach (var kvp in _imagePatternResourceSetCache) {
                kvp.Value.Dispose();
            }
            _imagePatternResourceSetCache.Clear();

            // Dispose shared resources
            _vertexBuffer?.Dispose();
            _viewSizeUniformBuffer?.Dispose();
        }
    }
}