using System.Numerics;
using System.Runtime.InteropServices;

namespace SilkyNvg.Rendering.Veldrid
{
    /// <summary>
    /// Image pattern shader — fills shapes with an RGBA texture using paintMat UV transform.
    /// Transforms world position into paint space, divides by extent to get UV, samples texture.
    /// Multiplies by innerCol (tint color) and AA coverage.
    ///
    /// Resource layout: ViewSize (binding 0) + ImagePatternParams uniform (binding 1)
    ///                  + PatternTexture (binding 2) + PatternSampler (binding 3)
    /// </summary>
    internal static class ImagePatternShader
    {
        // ================================================================
        // C# UNIFORM STRUCT — must match GLSL ImagePatternParams block EXACTLY
        //
        // GLSL layout (std140):
        //   mat4 paintMat;      // 64 bytes (inverse paint transform → world to paint space)
        //   vec4 innerCol;      // 16 bytes (tint color, usually white with alpha)
        //   vec4 outerCol;      // 16 bytes (unused, present for layout compatibility)
        //   vec2 extent;        //  8 bytes (image size for UV normalization)
        //   float radius;       //  4 bytes (unused)
        //   float feather;      //  4 bytes (unused)
        //                       // Total: 112 bytes
        // ================================================================
        [StructLayout(LayoutKind.Sequential)]
        internal struct ImagePatternUniforms
        {
            public Matrix4x4 PaintMat;     // 64 bytes — inverse paint transform
            public Vector4 TintColor;      // 16 bytes — innerCol (usually white)
            public Vector4 _unused_OuterColor; // 16 bytes — not used by image pattern
            public Vector2 Extent;         //  8 bytes — image size for UV normalization
            public float _unused_Radius;   //  4 bytes — not used by image pattern
            public float _unused_Feather;  //  4 bytes — not used by image pattern
            // Total: 112 bytes
        }

        // ================================================================
        // VERTEX SHADER
        // Inputs: Position (vec2), TexCoord (vec2), Color (vec4)
        // Outputs: frag_WorldPosition (vec2) at location 0, frag_TexCoord (vec2) at location 1
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
layout(location = 1) out vec2 frag_TexCoord;

void main() {
    frag_WorldPosition = Position;
    frag_TexCoord = TexCoord;
    gl_Position = vec4(2.0 * Position.x / viewSize.x - 1.0, 1.0 - 2.0 * Position.y / viewSize.y, 0.0, 1.0);
}
";
            return System.Text.Encoding.UTF8.GetBytes(vertexShaderCode);
        }

        // ================================================================
        // FRAGMENT SHADER
        // Transforms world position to UV via paintMat, samples RGBA texture, applies tint.
        //
        // Uniforms: ImagePatternParams at set=0, binding=1
        //   - paintMat (mat4): inverse paint transform (world → paint space)
        //   - innerCol (vec4): tint color (usually white with alpha)
        //   - extent (vec2): image size for UV normalization (paintSpace / extent = UV)
        //   - outerCol, radius, feather: unused (present for struct compatibility)
        //
        // Resources: PatternTexture (binding 2), PatternSampler (binding 3)
        // ================================================================
        internal static byte[] GetFragmentShaderBytes()
        {
            string fragmentShaderCode = @"
#version 450

layout(set = 0, binding = 1) uniform ImagePatternParams {
    mat4 paintMat;      // 64 bytes
    vec4 innerCol;      // 16 bytes — tint color
    vec4 outerCol;      // 16 bytes — unused
    vec2 extent;        //  8 bytes — image size for UV normalization
    float radius;       //  4 bytes — unused
    float feather;      //  4 bytes — unused
};

layout(set = 0, binding = 2) uniform texture2D PatternTexture;
layout(set = 0, binding = 3) uniform sampler PatternSampler;

layout(location = 0) in vec2 frag_WorldPosition;
layout(location = 1) in vec2 frag_TexCoord;

layout(location = 0) out vec4 out_Color;

void main() {
    // Transform world position into paint space, normalize to UV by dividing by extent
    vec2 paintSpacePosition = (paintMat * vec4(frag_WorldPosition, 1.0, 1.0)).xy / extent;

    // Sample the pattern texture
    vec4 texColor = texture(sampler2D(PatternTexture, PatternSampler), paintSpacePosition);

    // Apply tint color (innerCol) and AA coverage from fringe vertices
    float fillCoverage = min(1.0, frag_TexCoord.y);
    float strokeCoverage = min(1.0, (1.0 - abs(frag_TexCoord.x * 2.0 - 1.0)) * 2.0);
    float aaCoverage = fillCoverage * strokeCoverage;

    out_Color = texColor * innerCol * aaCoverage;
}
";
            return System.Text.Encoding.UTF8.GetBytes(fragmentShaderCode);
        }
    }
}