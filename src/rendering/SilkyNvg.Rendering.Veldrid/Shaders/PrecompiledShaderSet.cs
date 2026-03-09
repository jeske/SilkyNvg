using System;
using Veldrid;

namespace SilkyNvg.Rendering.Veldrid
{
    /// <summary>
    /// Static helper for creating Veldrid Shader objects from precompiled shader bytecode.
    /// Each generated shader class (e.g. SolidFill.generated.cs) calls CreateShaderPair()
    /// after selecting the correct bytes for the current graphics backend.
    /// </summary>
    internal static class PrecompiledShaderSet
    {
        /// <summary>
        /// Creates a vertex + fragment shader pair from precompiled bytes.
        /// The caller is responsible for selecting the correct bytes and entry points
        /// for the current GraphicsBackend.
        /// </summary>
        internal static Shader[] CreateShaderPair(
            ResourceFactory factory,
            byte[] vertexShaderBytes, string vertexEntryPoint,
            byte[] fragmentShaderBytes, string fragmentEntryPoint)
        {
            var vertexShader = factory.CreateShader(
                new ShaderDescription(ShaderStages.Vertex, vertexShaderBytes, vertexEntryPoint));
            var fragmentShader = factory.CreateShader(
                new ShaderDescription(ShaderStages.Fragment, fragmentShaderBytes, fragmentEntryPoint));
            return new[] { vertexShader, fragmentShader };
        }
    }
}