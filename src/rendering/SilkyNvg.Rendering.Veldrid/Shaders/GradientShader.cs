namespace SilkyNvg.Rendering.Veldrid
{
    /// <summary>
    /// Gradient shader — renders linear, radial, and box gradients using SDF.
    /// Transforms world position into paint space via paintMat, computes signed distance
    /// to rounded rectangle, and mixes between inner/outer colors.
    /// Resource layout: ViewSize (binding 0) + GradientParams uniform (binding 1)
    ///
    /// C# uniform struct: ShaderLayouts.PaintUniforms (shared with ImagePatternShader)
    /// </summary>
    internal static class GradientShader
    {

        // ================================================================
        // VERTEX SHADER
        // Inputs: Position (vec2), TexCoord (vec2), Color (vec4)
        // Outputs: frag_WorldPosition (vec2) at location 0, frag_AACoverage (float) at location 1
        // Uniforms: ViewSize (vec2) at set=0, binding=0
        // ================================================================
        internal static byte[] GetVertexShaderBytes()
        {
            string vertexShaderCode = @"
#version 450

layout(set = 0, binding = 0) uniform ViewSize {
    vec2 viewSize;
};

layout(location = 0) in vec2 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 2) in vec4 Color;

layout(location = 0) out vec2 frag_WorldPosition;
layout(location = 1) out float frag_AACoverage;

void main() {
    frag_WorldPosition = Position;
    frag_AACoverage = TexCoord.y;
    gl_Position = vec4(2.0 * Position.x / viewSize.x - 1.0, 1.0 - 2.0 * Position.y / viewSize.y, 0.0, 1.0);
}
";
            return System.Text.Encoding.UTF8.GetBytes(vertexShaderCode);
        }

        // ================================================================
        // FRAGMENT SHADER
        // Computes gradient fill using signed distance to rounded rectangle.
        // Uniforms: GradientParams at set=0, binding=1
        //   - paintMat: inverse paint transform (world → paint space)
        //   - innerCol/outerCol: gradient endpoint colors
        //   - extent: half-size of the gradient rectangle
        //   - radius: corner radius for box gradients
        //   - feather: gradient softness (gradient length for linear)
        // ================================================================
        internal static byte[] GetFragmentShaderBytes()
        {
            string fragmentShaderCode = @"
#version 450

layout(set = 0, binding = 1) uniform GradientParams {
    mat4 paintMat;      // 64 bytes
    vec4 innerCol;      // 16 bytes
    vec4 outerCol;      // 16 bytes
    vec2 extent;        //  8 bytes
    float radius;       //  4 bytes
    float feather;      //  4 bytes
};

layout(location = 0) in vec2 frag_WorldPosition;
layout(location = 1) in float frag_AACoverage;

layout(location = 0) out vec4 out_Color;

float sdroundrect(vec2 pt, vec2 ext, float rad) {
    vec2 ext2 = ext - vec2(rad, rad);
    vec2 d = abs(pt) - ext2;
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - rad;
}

void main() {
    // Transform world position into paint space
    vec2 pt = (paintMat * vec4(frag_WorldPosition, 1.0, 1.0)).xy;

    // Compute signed distance to rounded rectangle
    float d = clamp((sdroundrect(pt, extent, radius) + feather * 0.5) / feather, 0.0, 1.0);

    // Mix between inner and outer colors, apply AA coverage
    vec4 colour = mix(innerCol, outerCol, d);
    out_Color = vec4(colour.rgb, colour.a * frag_AACoverage);
}
";
            return System.Text.Encoding.UTF8.GetBytes(fragmentShaderCode);
        }
    }
}