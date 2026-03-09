namespace SilkyNvg.Rendering.Veldrid
{
    /// <summary>
    /// Solid fill shader — renders shapes with per-vertex color and AA coverage.
    /// Used for: rectangles, circles, rounded rects, arcs, polygons, strokes.
    /// No uniforms beyond ViewSize. Color comes from vertex attributes.
    /// </summary>
    internal static class SolidFillShader
    {
        // ================================================================
        // VERTEX SHADER
        // Inputs: Position (vec2), TexCoord (vec2), Color (vec4)
        // Outputs: frag_Color (vec4), frag_TexCoord (vec2)
        // Uniforms: ViewSize (vec2) at set=0, binding=0
        // ================================================================
        internal static byte[] GetVertexShaderBytes()
        {
            string vertexShaderCode = @"
#version 450

layout(set = 0, binding = 0) uniform ViewSize {
    vec4 viewSize; // xy = viewport size, z = Y clip-space multiplier (+1 or -1), w = unused
};

layout(location = 0) in vec2 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 2) in vec4 Color;

layout(location = 0) out vec4 frag_Color;
layout(location = 1) out vec2 frag_TexCoord;

void main() {
    frag_Color = Color;
    frag_TexCoord = TexCoord;
    // viewSize.z = Y clip-space multiplier: +1.0 for D3D11/OpenGL (Y-up), -1.0 for Vulkan (Y-down)
    gl_Position = vec4(2.0 * Position.x / viewSize.x - 1.0, (1.0 - 2.0 * Position.y / viewSize.y) * viewSize.z, 0.0, 1.0);
}
";
            return System.Text.Encoding.UTF8.GetBytes(vertexShaderCode);
        }

        // ================================================================
        // FRAGMENT SHADER
        // Computes AA coverage from fringe vertices:
        //   Fill AA:   tcoord.y fades from 1 (inside) to 0 (outer edge)
        //   Stroke AA: tcoord.x encodes cross-stroke position (0/1=edge, 0.5=center)
        // ================================================================
        internal static byte[] GetFragmentShaderBytes()
        {
            string fragmentShaderCode = @"
#version 450

layout(location = 0) in vec4 frag_Color;
layout(location = 1) in vec2 frag_TexCoord;

layout(location = 0) out vec4 out_Color;

void main() {
    float fillCoverage = min(1.0, frag_TexCoord.y);
    float strokeCoverage = min(1.0, (1.0 - abs(frag_TexCoord.x * 2.0 - 1.0)) * 2.0);
    float aaCoverage = fillCoverage * strokeCoverage;
    out_Color = vec4(frag_Color.rgb, frag_Color.a * aaCoverage);
}
";
            return System.Text.Encoding.UTF8.GetBytes(fragmentShaderCode);
        }
    }
}