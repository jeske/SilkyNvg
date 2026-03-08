namespace SilkyNvg.Rendering.Veldrid
{
    /// <summary>
    /// Textured shader — renders font atlas text (R8_UNorm alpha-only texture).
    /// Vertices have proper UV coordinates from FontStash.
    /// Samples red channel as alpha, multiplies by vertex color.
    /// Resource layout: ViewSize (binding 0) + FontAtlas texture (binding 1) + Sampler (binding 2)
    /// </summary>
    internal static class TexturedShader
    {
        // ================================================================
        // VERTEX SHADER
        // Inputs: Position (vec2), TexCoord (vec2), Color (vec4)
        // Outputs: frag_TexCoord (vec2) at location 0, frag_Color (vec4) at location 1
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

        // ================================================================
        // FRAGMENT SHADER
        // Font atlas is R8_UNorm (alpha only), sample red channel as alpha.
        // Resources: FontAtlas (binding 1), FontAtlasSampler (binding 2)
        // ================================================================
        internal static byte[] GetFragmentShaderBytes()
        {
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
    }
}