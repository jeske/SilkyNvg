using System.Runtime.InteropServices;
using Veldrid;
using Veldrid.SPIRV;

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

        private void CreateShaders()
        {
            var factory = _graphicsDevice.ResourceFactory;

            _solidFillShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, SolidFillShader.GetVertexShaderBytes(), "main"),
                new ShaderDescription(ShaderStages.Fragment, SolidFillShader.GetFragmentShaderBytes(), "main"));

            _texturedShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, TexturedShader.GetVertexShaderBytes(), "main"),
                new ShaderDescription(ShaderStages.Fragment, TexturedShader.GetFragmentShaderBytes(), "main"));

            _gradientShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, GradientShader.GetVertexShaderBytes(), "main"),
                new ShaderDescription(ShaderStages.Fragment, GradientShader.GetFragmentShaderBytes(), "main"));

            _imagePatternShaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, ImagePatternShader.GetVertexShaderBytes(), "main"),
                new ShaderDescription(ShaderStages.Fragment, ImagePatternShader.GetFragmentShaderBytes(), "main"));
        }

        private void CreatePipeline()
        {
            var factory = _graphicsDevice.ResourceFactory;

            // === SOLID FILL PIPELINE (shapes without textures) ===
            var sharedVertexLayout = ShaderLayouts.CreateVertexLayout();

            // Resource layout for view size uniform only
            _solidFillResourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ViewSize", ResourceKind.UniformBuffer, ShaderStages.Vertex)));

            var solidFillPipelineDesc = new GraphicsPipelineDescription {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: false,
                    scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _solidFillResourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { sharedVertexLayout },
                    _solidFillShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _solidFillPipeline = factory.CreateGraphicsPipeline(solidFillPipelineDesc);

            // === TEXTURED PIPELINE (font atlas text rendering) ===

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
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: false,
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

            // Resource layout: ViewSize uniform (vertex) + GradientParams uniform (fragment)
            _gradientResourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ViewSize", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("GradientParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            var gradientPipelineDesc = new GraphicsPipelineDescription {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: false,
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

            // Resource layout: ViewSize (vertex) + ImagePatternParams (fragment) + Texture + Sampler
            _imagePatternResourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ViewSize", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("ImagePatternParams", ResourceKind.UniformBuffer, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("PatternTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("PatternSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // Sampler for image patterns (linear filtering, clamp to edge)
            _imagePatternSampler = factory.CreateSampler(new SamplerDescription {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = SamplerFilter.MinLinear_MagLinear_MipLinear,
                MinimumLod = 0,
                MaximumLod = 0
            });

            var imagePatternPipelineDesc = new GraphicsPipelineDescription {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: false,
                    scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _imagePatternResourceLayout },
                ShaderSet = new ShaderSetDescription(
                    new[] { sharedVertexLayout },
                    _imagePatternShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _imagePatternPipeline = factory.CreateGraphicsPipeline(imagePatternPipelineDesc);
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
            _solidFillResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _solidFillResourceLayout,
                _viewSizeUniformBuffer));

            // Paint uniform buffer — shared by gradient and image pattern pipelines (updated per draw call)
            // NOT Dynamic — must use commandList.UpdateBuffer() for proper per-draw-call sequencing
            _paintUniformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<ShaderLayouts.PaintUniforms>(),
                BufferUsage.UniformBuffer));

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