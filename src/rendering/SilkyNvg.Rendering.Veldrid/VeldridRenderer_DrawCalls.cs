using System;
using SilkyNvg.Blending;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;

namespace SilkyNvg.Rendering.Veldrid
{
    public sealed partial class VeldridRenderer
    {
        // Vertex format: position (x,y) + texcoord (u,v) + color (rgba)
        // IMPORTANT: Must use Vector2 and RgbaFloat types to match Veldrid's expected layout!
        [StructLayout(LayoutKind.Sequential)]
        private struct NvgVertex
        {
            public Vector2 Position;    // 8 bytes
            public Vector2 TexCoord;    // 8 bytes
            public RgbaFloat Color;     // 16 bytes

            public NvgVertex(Vertex vertex, Colour color)
            {
                Position = new Vector2(vertex.X, vertex.Y);
                TexCoord = new Vector2(vertex.U, vertex.V);
                Color = new RgbaFloat(color.R, color.G, color.B, color.A);
            }
        }

        private struct DrawCall
        {
            public int VertexOffset;
            public int VertexCount;
            public int TextureId; // 0 = solid fill, non-zero = textured (font atlas or image)
            public bool HasScissor;
            public int ScissorX;
            public int ScissorY;
            public uint ScissorWidth;
            public uint ScissorHeight;
            public BlendStateDescription BlendState;
        }

        /// <summary>
        /// Extracts an axis-aligned scissor rect from the NVG Scissor.
        /// Scissor.Transform positions the center, Scissor.Extent is the half-size.
        /// Returns false if scissor is disabled (extent &lt; -0.5).
        /// </summary>
        private static bool TryExtractScissorRect(Scissor scissor, out int scissorX, out int scissorY, out uint scissorWidth, out uint scissorHeight)
        {
            if (scissor.Extent.Width < -0.5f || scissor.Extent.Height < -0.5f) {
                scissorX = 0;
                scissorY = 0;
                scissorWidth = 0;
                scissorHeight = 0;
                return false;
            }

            // Scissor center is at Transform translation, extent is half-size
            float centerX = scissor.Transform.M31;
            float centerY = scissor.Transform.M32;
            float halfWidth = scissor.Extent.Width;
            float halfHeight = scissor.Extent.Height;

            scissorX = Math.Max(0, (int)(centerX - halfWidth));
            scissorY = Math.Max(0, (int)(centerY - halfHeight));
            scissorWidth = (uint)Math.Max(0, (int)(halfWidth * 2.0f));
            scissorHeight = (uint)Math.Max(0, (int)(halfHeight * 2.0f));
            return true;
        }

        private DrawCall CreateDrawCall(int vertexOffset, int vertexCount, int textureId, Scissor scissor)
        {
            var drawCall = new DrawCall
            {
                VertexOffset = vertexOffset,
                VertexCount = vertexCount,
                TextureId = textureId,
                BlendState = BlendStateDescription.SingleAlphaBlend
            };

            drawCall.HasScissor = TryExtractScissorRect(scissor,
                out drawCall.ScissorX, out drawCall.ScissorY,
                out drawCall.ScissorWidth, out drawCall.ScissorHeight);

            return drawCall;
        }

        public void Fill(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, float fringe, RectangleF bounds, IReadOnlyList<Path> paths)
        {
            var fillColor = paint.InnerColour;
            int vertexOffset = _vertexBatch.Count;

            foreach (var path in paths)
            {
                // For convex paths, add fill vertices directly as triangle fan
                if (path.Fill.Count >= 3) {
                    // Convert triangle fan to triangle list
                    var firstVertex = path.Fill[0];
                    for (int i = 1; i < path.Fill.Count - 1; i++) {
                        _vertexBatch.Add(new NvgVertex(firstVertex, fillColor));
                        _vertexBatch.Add(new NvgVertex(path.Fill[i], fillColor));
                        _vertexBatch.Add(new NvgVertex(path.Fill[i + 1], fillColor));
                    }
                }
            }

            int vertexCount = _vertexBatch.Count - vertexOffset;

            if (vertexCount > 0) {
                _drawCalls.Add(CreateDrawCall(vertexOffset, vertexCount, paint.Image, scissor));
            }
        }

        public void Stroke(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, float fringe, float strokeWidth, IReadOnlyList<Path> paths)
        {
            var strokeColor = paint.InnerColour;

            int vertexOffset = _vertexBatch.Count;

            foreach (var path in paths)
            {
                // Stroke vertices are already tesselated as triangle strips
                if (path.Stroke.Count >= 3) {
                    // Convert triangle strip to triangle list
                    for (int i = 0; i < path.Stroke.Count - 2; i++) {
                        if (i % 2 == 0) {
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i], strokeColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 1], strokeColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 2], strokeColor));
                        } else {
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 1], strokeColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i], strokeColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 2], strokeColor));
                        }
                    }
                }
            }

            int vertexCount = _vertexBatch.Count - vertexOffset;
            if (vertexCount > 0) {
                _drawCalls.Add(CreateDrawCall(vertexOffset, vertexCount, paint.Image, scissor));
            }
        }

        public void Triangles(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, ICollection<Vertex> vertices, float fringeWidth)
        {
            // Text rendering: paint.Image contains the font atlas texture ID
            var color = paint.InnerColour;
            int vertexOffset = _vertexBatch.Count;

            foreach (var vertex in vertices) {
                _vertexBatch.Add(new NvgVertex(vertex, color));
            }

            int vertexCount = _vertexBatch.Count - vertexOffset;
            if (vertexCount > 0) {
                _drawCalls.Add(CreateDrawCall(vertexOffset, vertexCount, paint.Image, scissor));
            }
        }
    }
}