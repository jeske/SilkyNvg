using System.Drawing;

#nullable enable

namespace SilkyNvg.Core.States
{
    /// <summary>
    /// Immutable snapshot of a clip path's tessellated geometry, sufficient to
    /// re-render the clip mask on Save/Restore. Stored in the NVG State.
    /// </summary>
    /// <remarks>
    /// This is CPU-side data only — vertex arrays, not GPU resources.
    /// It gets re-uploaded and re-rendered when a Restore() requires rebuilding the clip mask.
    /// 
    /// Because State.Clone() uses MemberwiseClone(), this must be an immutable reference type
    /// so that multiple state stack entries safely share the same snapshot.
    /// 
    /// For nested clips, ClipSnapshots form an immutable chain: each snapshot references
    /// the previous clip it was intersected with, so the full clip can be reconstructed.
    /// </remarks>
    internal sealed class ClipPathSnapshot
    {
        /// <summary>
        /// GPU-ready vertex data for the clip path's stencil fill pass.
        /// These are the triangle-fan vertices converted to triangle-list format,
        /// ready to be uploaded directly to the GPU vertex buffer.
        /// </summary>
        public readonly Rendering.Vertex[] StencilVertices;

        /// <summary>
        /// Bounding rectangle of the clip path (used for the promote pass quad).
        /// </summary>
        public readonly RectangleF Bounds;

        /// <summary>
        /// True if this clip uses even-odd fill rule; false for nonzero winding.
        /// </summary>
        public readonly bool EvenOdd;

        /// <summary>
        /// The previous clip snapshot in the intersection chain, or null if this is
        /// the first/only clip. When re-rendering after Restore(), we rebuild from
        /// the chain: render each snapshot's geometry in sequence, intersecting.
        /// </summary>
        public readonly ClipPathSnapshot? Previous;

        public ClipPathSnapshot(Rendering.Vertex[] stencilVertices, RectangleF bounds, bool evenOdd, ClipPathSnapshot? previous)
        {
            StencilVertices = stencilVertices;
            Bounds = bounds;
            EvenOdd = evenOdd;
            Previous = previous;
        }
    }
}