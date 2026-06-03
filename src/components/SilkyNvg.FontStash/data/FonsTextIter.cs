namespace FontStash.NET
{
    public struct FonsTextIter
    {

        public float x, y, nextx, nexty, scale, spacing;
        public uint codepoint;
        public short isize, iblur;
        public FonsFont font;
        public int prevGlyphIndex;
        public int strIndex;
        public int nextIndex;
        public int endIndex;
        public uint utf8state;
        public FonsGlyphBitmap bitmapOption;

    }
}