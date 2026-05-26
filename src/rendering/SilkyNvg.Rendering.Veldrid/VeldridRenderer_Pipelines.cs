using System;
using System.Runtime.InteropServices;
using Veldrid;

namespace SilkyNvg.Rendering.Veldrid
{
    public sealed partial class VeldridRenderer
    {
        /// <summary>
        /// Depth-stencil state that explicitly disables both depth and stencil testing.
        /// Required when the framebuffer has a depth-stencil attachment but we don't want to use it.
        /// DepthStencilStateDescription.Disabled leaves stencil fields at zero defaults which
        /// can cause issues on some drivers when a stencil buffer is present.
        /// </summary>
        private static readonly DepthStencilStateDescription DepthStencilDisabledExplicit = new DepthStencilStateDescription
        {
            DepthTestEnabled = false,
            DepthWriteEnabled = false,
            DepthComparison = ComparisonKind.Always,
            StencilTestEnabled = false,
            StencilFront = new StencilBehaviorDescription(StencilOperation.Keep, StencilOperation.Keep, StencilOperation.Keep, ComparisonKind.Always),
            StencilBack = new StencilBehaviorDescription(StencilOperation.Keep, StencilOperation.Keep, StencilOperation.Keep, ComparisonKind.Always),
            StencilReadMask = 0xFF,
            StencilWriteMask = 0x00,
            StencilReference = 0
        };

        /// <summary>
        /// Stencil cover depth-stencil state: draw where stencil != 0, zero stencil on pass.
        /// Shared by all stencil cover pipeline variants (solid, gradient, image pattern).
        /// </summary>
        private static readonly DepthStencilStateDescription StencilCoverDepthStencilState = new DepthStencilStateDescription
        {
            DepthTestEnabled = false,
            DepthWriteEnabled = false,
            StencilTestEnabled = true,
            StencilFront = new StencilBehaviorDescription(
                StencilOperation.Zero, StencilOperation.Zero,
                StencilOperation.Zero, ComparisonKind.NotEqual),
            StencilBack = new StencilBehaviorDescription(
                StencilOperation.Zero, StencilOperation.Zero,
                StencilOperation.Zero, ComparisonKind.NotEqual),
            StencilReadMask = 0xFF,
            StencilWriteMask = 0xFF,
            StencilReference = 0
        };

        private void CreateShaders()
        {
            var factory = _graphicsDevice.ResourceFactory;

            // Load precompiled shaders from .vdshader bundles (embedded resources).
            // CreateFromBundle() returns both the Shader objects AND the ResourceLayoutDescription[]
            // that guarantees correct resource binding on all backends (Vulkan, D3D11, Metal, OpenGL).
            _solidFillResult = factory.CreateFromBundle(LoadBundleJson("SilkyNvg.Shaders.SolidFill.vdshader"));
            _vertexColorShaders = _solidFillResult.Shaders;
            Console.WriteLine($"[VELDRID] Created SolidFill shaders for {factory.BackendType}");

            _texturedResult = factory.CreateFromBundle(LoadBundleJson("SilkyNvg.Shaders.Textured.vdshader"));
            _texturedShaders = _texturedResult.Shaders;
            Console.WriteLine($"[VELDRID] Created Textured shaders for {factory.BackendType}");

            _gradientResult = factory.CreateFromBundle(LoadBundleJson("SilkyNvg.Shaders.Gradient.vdshader"));
            _gradientShaders = _gradientResult.Shaders;
            Console.WriteLine($"[VELDRID] Created Gradient shaders for {factory.BackendType}");

            _imagePatternResult = factory.CreateFromBundle(LoadBundleJson("SilkyNvg.Shaders.ImagePattern.vdshader"));
            _imagePatternShaders = _imagePatternResult.Shaders;
            Console.WriteLine($"[VELDRID] Created ImagePattern shaders for {factory.BackendType}");
        }

        // Bundle results (hold ResourceLayouts used in CreatePipeline)
        private PrecompiledShaderResult? _solidFillResult;
        private PrecompiledShaderResult? _texturedResult;
        private PrecompiledShaderResult? _gradientResult;
        private PrecompiledShaderResult? _imagePatternResult;

        private static string LoadBundleJson(string resourceName)
        {
            var assembly = typeof(VeldridRenderer).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded shader bundle '{resourceName}' not found. " +
                    $"Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }

        private void CreatePipeline()
        {
            var factory = _graphicsDevice.ResourceFactory;

            // === SOLID FILL PIPELINE (shapes without textures) ===
            var sharedVertexLayout = ShaderLayouts.CreateVertexLayout();

            // Resource layouts from the .vdshader bundle — guaranteed correct on all backends
            _viewSizeOnlyResourceLayout = factory.CreateResourceLayout(_solidFillResult!.ResourceLayouts[0]);

            var solidFillPipelineDesc = new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _viewSizeOnlyResourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { sharedVertexLayout },
                    _vertexColorShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _solidFillPipeline = factory.CreateGraphicsPipeline(solidFillPipelineDesc);

            // === TEXTURED PIPELINE (font atlas text rendering) ===

            // Resource layout from bundle (ViewSize + FontAtlas + Sampler)
            _texturedResourceLayout = factory.CreateResourceLayout(_texturedResult!.ResourceLayouts[0]);

            // Create sampler for font atlas (linear filtering for smooth text)
            _fontAtlasSampler = factory.CreateSampler(new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = VeldridCompat.LinearFilter,
                MinimumLod = 0,
                MaximumLod = 0
            });

            var texturedPipelineDesc = new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _texturedResourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { sharedVertexLayout },
                    _texturedShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _texturedPipeline = factory.CreateGraphicsPipeline(texturedPipelineDesc);

            // === GRADIENT PIPELINE (linear, radial, box gradients) ===

            // Resource layout from bundle (ViewSize + GradientParams)
            _gradientResourceLayout = factory.CreateResourceLayout(_gradientResult!.ResourceLayouts[0]);

            var gradientPipelineDesc = new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _gradientResourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { sharedVertexLayout },
                    _gradientShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _gradientPipeline = factory.CreateGraphicsPipeline(gradientPipelineDesc);

            // === IMAGE PATTERN PIPELINE (RGBA texture fill with paintMat UV transform) ===

            // Resource layout from bundle (ViewSize + ImagePatternParams + Texture + Sampler)
            _imagePatternResourceLayout = factory.CreateResourceLayout(_imagePatternResult!.ResourceLayouts[0]);

            // Sampler for image patterns (linear filtering, clamp to edge)
            _imagePatternSampler = factory.CreateSampler(new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = VeldridCompat.LinearFilter,
                MinimumLod = 0,
                MaximumLod = 0
            });

            var imagePatternPipelineDesc = new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _imagePatternResourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { sharedVertexLayout },
                    _imagePatternShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _imagePatternPipeline = factory.CreateGraphicsPipeline(imagePatternPipelineDesc);

            // === STENCIL FILL PIPELINE (Pass 1: write winding count to stencil, no color output) ===
            // Non-zero winding rule: front faces increment, back faces decrement.
            // CullMode must be None so both face orientations contribute to the winding count.
            // In concave areas where triangles overlap with opposite winding, increments and
            // decrements cancel to 0, so the cover quad won't draw there.
            var stencilFillPipelineDesc = new GraphicsPipelineDescription
            {
                BlendState = new BlendStateDescription
                {
                    AttachmentStates = new[] {
                        new BlendAttachmentDescription { BlendEnabled = false, ColorWriteMask = ColorWriteMask.None }
                    }
                },
                DepthStencilState = new DepthStencilStateDescription
                {
                    DepthTestEnabled = false,
                    DepthWriteEnabled = false,
                    StencilTestEnabled = true,
                    // Non-zero winding rule: front faces increment, back faces decrement.
                    // All three ops set to same value because with DepthTestEnabled=false,
                    // some drivers route through depthFail instead of pass.
                    StencilFront = new StencilBehaviorDescription(
                        StencilOperation.IncrementAndWrap, StencilOperation.IncrementAndWrap,
                        StencilOperation.IncrementAndWrap, ComparisonKind.Always),
                    StencilBack = new StencilBehaviorDescription(
                        StencilOperation.DecrementAndWrap, StencilOperation.DecrementAndWrap,
                        StencilOperation.DecrementAndWrap, ComparisonKind.Always),
                    StencilReadMask = 0xFF,
                    StencilWriteMask = 0xFF,
                    StencilReference = 0
                },
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _viewSizeOnlyResourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { sharedVertexLayout },
                    _vertexColorShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _stencilFillPipeline = factory.CreateGraphicsPipeline(stencilFillPipelineDesc);

            // === STENCIL COVER PIPELINES (Pass 2: fill where stencil != 0, then zero stencil) ===
            // Three variants for the three paint types: solid, gradient, image pattern.
            // Each uses the same stencil state (NotEqual to 0, zero on pass) but different shaders.
            var stencilCoverRasterizer = new RasterizerStateDescription(
                cullMode: FaceCullMode.None,
                fillMode: PolygonFillMode.Solid,
                frontFace: FrontFace.CounterClockwise,
                depthClipEnabled: true,
                scissorTestEnabled: true);

            // Solid cover (vertex color only)
            _stencilCoverSolidPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = StencilCoverDepthStencilState,
                RasterizerState = stencilCoverRasterizer,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _viewSizeOnlyResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _vertexColorShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            });

            // Gradient cover (gradient shader + stencil test)
            _stencilCoverGradientPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = StencilCoverDepthStencilState,
                RasterizerState = stencilCoverRasterizer,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _gradientResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _gradientShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            });

            // Image pattern cover (image pattern shader + stencil test)
            _stencilCoverImagePatternPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = StencilCoverDepthStencilState,
                RasterizerState = stencilCoverRasterizer,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _imagePatternResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _imagePatternShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            });
        }

        private void CreateBuffers()
        {
            var factory = _graphicsDevice.ResourceFactory;

            // Dynamic vertex buffer (will resize as needed)
            _vertexBuffer = factory.CreateBuffer(new BufferDescription(
                4096 * (uint)Marshal.SizeOf<ShaderLayouts.NvgVertex>(),
                BufferUsage.VertexBuffer | BufferUsage.Dynamic));

            // View size uniform buffer
            _viewSizeUniformBuffer = factory.CreateBuffer(new BufferDescription(
                16, // vec2 padded to 16 bytes
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // Resource set for solid fill pipeline (just view size uniform)
            _viewSizeOnlyResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _viewSizeOnlyResourceLayout,
                _viewSizeUniformBuffer));

            // Paint uniform buffer — shared by gradient and image pattern pipelines (updated per draw call)
            // Dynamic for CPU-accessible memory; updated via commandList.UpdateBuffer() for proper sequencing
            _paintUniformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<ShaderLayouts.PaintUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // Resource set for gradient pipeline (viewSize + paint params)
            _gradientResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _gradientResourceLayout,
                _viewSizeUniformBuffer,
                _paintUniformBuffer));

            // Create the TextureRegistry now that we have the required Veldrid resources
            _textureRegistry = new TextureRegistry(
                _graphicsDevice,
                _texturedResourceLayout!,
                _viewSizeUniformBuffer,
                _fontAtlasSampler!);
        }
    }
}