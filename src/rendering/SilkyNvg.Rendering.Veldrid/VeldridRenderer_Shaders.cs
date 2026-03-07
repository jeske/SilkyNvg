namespace SilkyNvg.Rendering.Veldrid
{
    public sealed partial class VeldridRenderer
    {
        private byte[] GetSolidFillVertexShaderBytes()
        {
            string vertexShaderCode = @"
#version 450

layout(set = 0, binding = 0) uniform ViewSize {
    vec2 viewSize;
};

layout(location = 0) in vec2 Position;
layout(location = 1) in vec4 Color;

layout(location = 0) out vec4 frag_Color;

void main() {
    frag_Color = Color;
    gl_Position = vec4(2.0 * Position.x / viewSize.x - 1.0, 1.0 - 2.0 * Position.y / viewSize.y, 0.0, 1.0);
}
";
            return System.Text.Encoding.UTF8.GetBytes(vertexShaderCode);
        }

        private byte[] GetSolidFillFragmentShaderBytes()
        {
            string fragmentShaderCode = @"
#version 450

layout(location = 0) in vec4 frag_Color;

layout(location = 0) out vec4 out_Color;

void main() {
    out_Color = frag_Color;
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
    }
}