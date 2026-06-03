namespace SilkyNvg.Text
{
    public struct GlyphPosition
    {

        /// <summary>
        /// Index of the glyph in the source string.
        /// </summary>
        public int StrIndex { get; }

        /// <summary>
        /// The X-coordinate of the logical glyph position.
        /// </summary>
        public float X { get; }

        /// <summary>
        /// The smallest X-bound of the glyph shape.
        /// </summary>
        public float MinX { get; }

        /// <summary>
        /// The largest X-bound of the glyph shape.
        /// </summary>
        public float MaxX { get; }

        internal GlyphPosition(int strIndex, float x, float minX, float maxX)
        {
            StrIndex = strIndex;
            X = x;
            MinX = minX;
            MaxX = maxX;
        }

    }
}