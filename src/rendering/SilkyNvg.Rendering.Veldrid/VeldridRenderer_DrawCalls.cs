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
        private enum DrawCallType : byte
        {
            SolidFill,
            Textured,
            Gradient
        }

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
            public DrawCallType Type;
            public int TextureId; // Only used when Type == Textured
            public GradientUniforms GradientParams; // Only used when Type == Gradient
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

        private DrawCall CreateDrawCall(int vertexOffset, int vertexCount, Paint paint, Scissor scissor)
        {
            var drawCall = new DrawCall
            {
                VertexOffset = vertexOffset,
                VertexCount = vertexCount,
                BlendState = BlendStateDescription.SingleAlphaBlend
            };

            // Determine draw call type based on paint properties
            if (paint.Image != 0) {
                drawCall.Type = DrawCallType.Textured;
                drawCall.TextureId = paint.Image;
            } else if (IsGradientPaint(paint)) {
                drawCall.Type = DrawCallType.Gradient;
                drawCall.GradientParams = ComputeGradientUniforms(paint);
            } else {
                drawCall.Type = DrawCallType.SolidFill;
            }

            drawCall.HasScissor = TryExtractScissorRect(scissor,
                out drawCall.ScissorX, out drawCall.ScissorY,
                out drawCall.ScissorWidth, out drawCall.ScissorHeight);

            return drawCall;
        }

        /// <summary>
        /// Detects if a paint represents a gradient (vs solid color).
        /// A gradient has different inner/outer colors, or non-zero radius/feather.
        /// </summary>
        private static bool IsGradientPaint(Paint paint)
        {
            // NVG sets radius > 0 for box gradients, feather > 1 for all gradients
            // Solid colors have feather = 0 and radius = 0
            return paint.Radius > 0 || paint.Feather > 1.0f ||
                   !ColorsEqual(paint.InnerColour, paint.OuterColour);
        }

        private static bool ColorsEqual(Colour colorA, Colour colorB)
        {
            return colorA.R == colorB.R && colorA.G == colorB.G &&
                   colorA.B == colorB.B && colorA.A == colorB.A;
        }

        /// <summary>
        /// Computes gradient uniform data from a NVG Paint, matching the OpenGL backend's FragUniforms logic.
        /// </summary>
        private static GradientUniforms ComputeGradientUniforms(Paint paint)
        {
            Matrix3x2.Invert(paint.Transform, out Matrix3x2 inversePaintTransform);

            return new GradientUniforms
            {
                PaintMat = new Matrix4x4(inversePaintTransform),
                InnerColor = new Vector4(
                    paint.InnerColour.R, paint.InnerColour.G,
                    paint.InnerColour.B, paint.InnerColour.A),
                OuterColor = new Vector4(
                    paint.OuterColour.R, paint.OuterColour.G,
                    paint.OuterColour.B, paint.OuterColour.A),
                Extent = new Vector2(paint.Extent.Width, paint.Extent.Height),
                Radius = paint.Radius,
                Feather = MathF.Max(1.0f, paint.Feather)
            };
        }

        public void Fill(Paint paint, CompositeOperationState compositeOperation, Scissor scissor, float fringe, RectangleF bounds, IReadOnlyList<Path> paths)
        {
            var fillColor = paint.InnerColour;
            int vertexOffset = _vertexBatch.Count;

            foreach (var path in paths)
            {
                // Add fill vertices as triangle fan → triangle list
                if (path.Fill.Count >= 3) {
                    var firstVertex = path.Fill[0];
                    for (int i = 1; i < path.Fill.Count - 1; i++) {
                        _vertexBatch.Add(new NvgVertex(firstVertex, fillColor));
                        _vertexBatch.Add(new NvgVertex(path.Fill[i], fillColor));
                        _vertexBatch.Add(new NvgVertex(path.Fill[i + 1], fillColor));
                    }
                }

                // Add stroke (fringe) vertices for edge anti-aliasing
                // These have tcoord.y fading to 0 at the outer edge for smooth AA
                if (path.Stroke.Count >= 3) {
                    for (int i = 0; i < path.Stroke.Count - 2; i++) {
                        if (i % 2 == 0) {
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i], fillColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 1], fillColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 2], fillColor));
                        } else {
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 1], fillColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i], fillColor));
                            _vertexBatch.Add(new NvgVertex(path.Stroke[i + 2], fillColor));
                        }
                    }
                }
            }

            int vertexCount = _vertexBatch.Count - vertexOffset;

            if (vertexCount > 0) {
                _drawCalls.Add(CreateDrawCall(vertexOffset, vertexCount, paint, scissor));
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
                _drawCalls.Add(CreateDrawCall(vertexOffset, vertexCount, paint, scissor));
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
                _drawCalls.Add(CreateDrawCall(vertexOffset, vertexCount, paint, scissor));
            }
        }
    }
}