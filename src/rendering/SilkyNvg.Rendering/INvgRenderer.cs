using SilkyNvg.Blending;
using SilkyNvg.Images;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace SilkyNvg.Rendering
{
    public interface INvgRenderer : IDisposable
    {

        bool EdgeAntiAlias { get; }

        bool Create();

        int CreateTexture(Texture type, Size size, ImageFlags imageFlags, ReadOnlySpan<byte> data);

        bool DeleteTexture(int image);

        bool UpdateTexture(int image, Rectangle bounds, ReadOnlySpan<byte> data);

        bool GetTextureSize(int image, out Size size);

        void Viewport(SizeF size, float devicePixelRatio);

        void Cancel();

        void Flush();

        void Fill(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, float fringe, RectangleF bounds, IReadOnlyList<Path> paths);

        /// <summary>
        /// Render the clip mask from the given paths. All subsequent Fill/Stroke/Triangles
        /// draws will be clipped to this mask until ClearClip() or another SetClip() is called.
        /// </summary>
        void SetClip(IReadOnlyList<Path> paths, Scissor scissor, float fringe, RectangleF bounds, bool evenOdd);

        /// <summary>
        /// Clear the clip mask. All subsequent draws are unclipped until the next SetClip().
        /// </summary>
        void ClearClip();

        void Stroke(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, float fringe, float strokeWidth, IReadOnlyList<Path> paths);

        void Triangles(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, ICollection<Vertex> vertices, float fringeWidth);

    }
}
