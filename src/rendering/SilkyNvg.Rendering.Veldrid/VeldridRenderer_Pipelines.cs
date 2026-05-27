using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Veldrid;

namespace SilkyNvg.Rendering.Veldrid
{
    /// <summary>
    /// Holds all GPU pipelines for a specific OutputDescription.
    /// Pipelines are target-format-dependent (depth format, color format, sample count).
    /// One PipelineSet is created per unique OutputDescription encountered at Flush() time.
    /// </summary>
    internal sealed class PipelineSet : IDisposable
    {
        public Pipeline SolidFill;
        public Pipeline Textured;
        public Pipeline Gradient;
        public Pipeline ImagePattern;
        public Pipeline StencilFill;
        public Pipeline StencilCoverSolid;
        public Pipeline StencilCoverGradient;
        public Pipeline StencilCoverImagePattern;

        public void Dispose()
        {
            SolidFill?.Dispose();
            Textured?.Dispose();
            Gradient?.Dispose();
            ImagePattern?.Dispose();
            StencilFill?.Dispose();
            StencilCoverSolid?.Dispose();
            StencilCoverGradient?.Dispose();
            StencilCoverImagePattern?.Dispose();
        }
    }

    public sealed partial class VeldridRenderer
    {
        /// <summary>
        /// Depth-stencil state that explicitly disables both depth and stencil testing.
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

        // Bundle results (hold ResourceLayouts used in CreatePipelinesForOutput)
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

        /// <summary>
        /// Creates shared resource layouts (OutputDescription-independent).
        /// Called once during Create().
        /// </summary>
        private void CreateResourceLayouts()
        {
            var factory = _graphicsDevice.ResourceFactory;
            _viewSizeOnlyResourceLayout = factory.CreateResourceLayout(_solidFillResult!.ResourceLayouts[0]);
            _texturedResourceLayout = factory.CreateResourceLayout(_texturedResult!.ResourceLayouts[0]);
            _gradientResourceLayout = factory.CreateResourceLayout(_gradientResult!.ResourceLayouts[0]);
            _imagePatternResourceLayout = factory.CreateResourceLayout(_imagePatternResult!.ResourceLayouts[0]);
        }

        /// <summary>
        /// Gets or creates a PipelineSet for the given OutputDescription.
        /// </summary>
        internal PipelineSet GetOrCreatePipelines(OutputDescription outputDescription)
        {
            // Fast path: same target as last time
            if (_lastUsedPipelineSet != null && _lastUsedOutputDescription.Equals(outputDescription))
                return _lastUsedPipelineSet;

            // Dictionary lookup
            if (!_pipelineCache.TryGetValue(outputDescription, out var pipelineSet))
            {
                pipelineSet = CreatePipelinesForOutput(outputDescription);
                _pipelineCache[outputDescription] = pipelineSet;
                Console.WriteLine($"[VELDRID] Created NVG pipeline set for new OutputDescription (total cached: {_pipelineCache.Count})");
            }

            _lastUsedPipelineSet = pipelineSet;
            _lastUsedOutputDescription = outputDescription;
            return pipelineSet;
        }

        /// <summary>
        /// Creates all 8 pipelines for a specific OutputDescription.
        /// </summary>
        private PipelineSet CreatePipelinesForOutput(OutputDescription outputDescription)
        {
            var factory = _graphicsDevice.ResourceFactory;
            var pipelineSet = new PipelineSet();
            var sharedVertexLayout = ShaderLayouts.CreateVertexLayout();

            pipelineSet.SolidFill = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise, depthClipEnabled: true, scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _viewSizeOnlyResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _vertexColorShaders),
                Outputs = outputDescription
            });

            pipelineSet.Textured = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise, depthClipEnabled: true, scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _texturedResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _texturedShaders),
                Outputs = outputDescription
            });

            pipelineSet.Gradient = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise, depthClipEnabled: true, scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _gradientResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _gradientShaders),
                Outputs = outputDescription
            });

            pipelineSet.ImagePattern = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = DepthStencilDisabledExplicit,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise, depthClipEnabled: true, scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _imagePatternResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _imagePatternShaders),
                Outputs = outputDescription
            });

            pipelineSet.StencilFill = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
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
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise, depthClipEnabled: true, scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _viewSizeOnlyResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _vertexColorShaders),
                Outputs = outputDescription
            });

            var stencilCoverRasterizer = new RasterizerStateDescription(
                cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                frontFace: FrontFace.CounterClockwise, depthClipEnabled: true, scissorTestEnabled: true);

            pipelineSet.StencilCoverSolid = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = StencilCoverDepthStencilState,
                RasterizerState = stencilCoverRasterizer,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _viewSizeOnlyResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _vertexColorShaders),
                Outputs = outputDescription
            });

            pipelineSet.StencilCoverGradient = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = StencilCoverDepthStencilState,
                RasterizerState = stencilCoverRasterizer,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _gradientResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _gradientShaders),
                Outputs = outputDescription
            });

            pipelineSet.StencilCoverImagePattern = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = VeldridCompat.SingleAlphaBlend,
                DepthStencilState = StencilCoverDepthStencilState,
                RasterizerState = stencilCoverRasterizer,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _imagePatternResourceLayout },
                ShaderSet = new ShaderSetDescription(new[] { sharedVertexLayout }, _imagePatternShaders),
                Outputs = outputDescription
            });

            return pipelineSet;
        }

        private void CreateBuffers()
        {
            var factory = _graphicsDevice.ResourceFactory;

            _vertexBuffer = factory.CreateBuffer(new BufferDescription(
                4096 * (uint)Marshal.SizeOf<ShaderLayouts.NvgVertex>(),
                BufferUsage.VertexBuffer | BufferUsage.Dynamic));

            _viewSizeUniformBuffer = factory.CreateBuffer(new BufferDescription(
                16, BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _viewSizeOnlyResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _viewSizeOnlyResourceLayout,
                _viewSizeUniformBuffer));

            _paintUniformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<ShaderLayouts.PaintUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _gradientResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _gradientResourceLayout,
                _viewSizeUniformBuffer,
                _paintUniformBuffer));

            _fontAtlasSampler = factory.CreateSampler(new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = VeldridCompat.LinearFilter,
                MinimumLod = 0,
                MaximumLod = 0
            });

            _imagePatternSampler = factory.CreateSampler(new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = VeldridCompat.LinearFilter,
                MinimumLod = 0,
                MaximumLod = 0
            });

            _textureRegistry = new TextureRegistry(
                _graphicsDevice,
                _texturedResourceLayout!,
                _viewSizeUniformBuffer,
                _fontAtlasSampler!);
        }
    }
}
