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

            // Gradient shaders (linear, radial, box gradients)
            var gradientVertexShaderDesc = new ShaderDescription(
                ShaderStages.Vertex,
                GetGradientVertexShaderBytes(),
                "main");

            var gradientFragmentShaderDesc = new ShaderDescription(
                ShaderStages.Fragment,
                GetGradientFragmentShaderBytes(),
                "main");

            _gradientShaders = _graphicsDevice.ResourceFactory.CreateFromSpirv(gradientVertexShaderDesc, gradientFragmentShaderDesc);
        }

        private void CreatePipeline()
        {
            var factory = _graphicsDevice.ResourceFactory;

            // === SOLID FILL PIPELINE (shapes without textures) ===
            // Vertex layout: Position, TexCoord (for AA coverage), Color
            var solidFillVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2, 8),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4, 16));

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
                    new[] { texturedVertexLayout },
                    _texturedShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _texturedPipeline = factory.CreateGraphicsPipeline(texturedPipelineDesc);

            // === GRADIENT PIPELINE (linear, radial, box gradients) ===
            // Vertex layout: Position, TexCoord (unused), Color (unused) — same NvgVertex struct
            var gradientVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2, 8),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4, 16));

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
                    new[] { gradientVertexLayout },
                    _gradientShaders),
                Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
            };

            _gradientPipeline = factory.CreateGraphicsPipeline(gradientPipelineDesc);
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

            // Gradient uniform buffer (updated per draw call)
            _gradientUniformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<GradientUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // Resource set for gradient pipeline (viewSize + gradient params)
            _gradientResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _gradientResourceLayout,
                _viewSizeUniformBuffer,
                _gradientUniformBuffer));

            // Create the TextureRegistry now that we have the required Veldrid resources
            _textureRegistry = new TextureRegistry(
                _graphicsDevice,
                _texturedResourceLayout!,
                _viewSizeUniformBuffer,
                _fontAtlasSampler!);
        }
    }
}