using System.Numerics;
using System.Runtime.InteropServices;

namespace SilkyNvg.Rendering.Veldrid
{
    public sealed partial class VeldridRenderer
    {
        /// <summary>
        /// Per-draw-call uniforms for gradient rendering.
        /// Matches the GLSL GradientParams uniform block layout.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct GradientUniforms
        {
            public Matrix4x4 PaintMat;     // 64 bytes — inverse paint transform
            public Vector4 InnerColor;     // 16 bytes
            public Vector4 OuterColor;     // 16 bytes
            public Vector2 Extent;         // 8 bytes
            public float Radius;           // 4 bytes
            public float Feather;          // 4 bytes
            // Total: 112 bytes
        }

        private byte[] GetSolidFillVertexShaderBytes()
        {
            // Passes vertex color and tcoord.y (AA coverage) to fragment shader
            string vertexShaderCode = @"
#version 450

layout(set = 0, binding = 0) uniform ViewSize {
    vec2 viewSize;
};

layout(location = 0) in vec2 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 2) in vec4 Color;

layout(location = 0) out vec4 frag_Color;
layout(location = 1) out vec2 frag_TexCoord;

void main() {
    frag_Color = Color;
    frag_TexCoord = TexCoord;
    gl_Position = vec4(2.0 * Position.x / viewSize.x - 1.0, 1.0 - 2.0 * Position.y / viewSize.y, 0.0, 1.0);
}
";
            return System.Text.Encoding.UTF8.GetBytes(vertexShaderCode);
        }

        private byte[] GetSolidFillFragmentShaderBytes()
        {
            // Computes AA coverage from fringe vertices:
            // - Fill AA: tcoord.y fades from 1 (inside) to 0 (edge)
            // - Stroke AA: tcoord.x encodes cross-stroke position (0/1 = edge, 0.5 = center)
            string fragmentShaderCode = @"
#version 450

layout(location = 0) in vec4 frag_Color;
layout(location = 1) in vec2 frag_TexCoord;

layout(location = 0) out vec4 out_Color;

void main() {
    // Stroke edge fade: 0 at edges (x=0 or x=1), 1 at center (x=0.5)
    // The *3.0 approximates strokeMult for ~2-3px strokes
    float strokeEdgeFade = min(1.0, (1.0 - abs(frag_TexCoord.x * 2.0 - 1.0)) * 3.0);
    // Fill edge fade: tcoord.y = 0 at outer fringe, 1 inside
    float coverage = strokeEdgeFade * frag_TexCoord.y;
    out_Color = vec4(frag_Color.rgb, frag_Color.a * coverage);
}
";
            return System.Text.Encoding.UTF8.GetBytes(fragmentShaderCode);
        }

        private byte[] GetTexturedVertexShaderBytes()
        {
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

        private byte[] GetGradientVertexShaderBytes()
        {
            // Passes world-space position and AA coverage to fragment shader
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

        private byte[] GetGradientFragmentShaderBytes()
        {
            // Computes gradient fill using signed distance to rounded rectangle
            string fragmentShaderCode = @"
#version 450

layout(set = 0, binding = 1) uniform GradientParams {
    mat4 paintMat;
    vec4 innerCol;
    vec4 outerCol;
    vec2 extent;
    float radius;
    float feather;
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