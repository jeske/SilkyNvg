using SilkyNvg.Core.States;
using SilkyNvg.Rendering;
using System.Collections.Generic;
using System.Drawing;

namespace SilkyNvg.Clipping
{
    /// <summary>
    /// <para>Clip path support for SilkyNvg. Allows clipping subsequent drawing
    /// operations to arbitrary path shapes (circles, polygons, bezier outlines, etc.).</para>
    /// <para>Clip paths are part of the NVG state stack — Save() snapshots the clip,
    /// Restore() restores it. Clip paths compose with rectangular Scissor clipping.</para>
    /// </summary>
    public static class NvgClipping
    {
        /// <summary>
        /// Clips subsequent drawing to the current path using the nonzero winding rule.
        /// If a clip is already active, the new clip is intersected with the existing clip.
        /// Consumes the current path (same as Fill()).
        /// </summary>
        public static void ClipPath(this Nvg nvg)
        {
            ClipPathInternal(nvg, evenOdd: false);
        }

        /// <summary>
        /// Clips subsequent drawing to the current path using the even-odd fill rule.
        /// If a clip is already active, the new clip is intersected with the existing clip.
        /// Consumes the current path (same as Fill()).
        /// </summary>
        public static void ClipPathEvenOdd(this Nvg nvg)
        {
            ClipPathInternal(nvg, evenOdd: true);
        }

        /// <summary>
        /// Removes all clip paths and returns to unclipped drawing.
        /// Analogous to ResetScissor().
        /// </summary>
        public static void ResetClip(this Nvg nvg)
        {
            var state = nvg.stateStack.CurrentState;
            if (!state.ClipActive)
                return;

            state.ClipActive = false;
            state.ClipSnapshot = null;
            nvg.renderer.ClearClip();
        }

        private static void ClipPathInternal(Nvg nvg, bool evenOdd)
        {
            // Flatten and tessellate the current path (same pipeline as Fill())
            nvg.instructionQueue.FlattenPaths();

            // Expand fill to get tessellated vertices
            if (nvg.renderer.EdgeAntiAlias && nvg.stateStack.CurrentState.ShapeAntiAlias)
            {
                nvg.pathCache.ExpandFill(nvg.pixelRatio.FringeWidth, Graphics.LineCap.Miter, 2.4f, nvg.pixelRatio);
            }
            else
            {
                nvg.pathCache.ExpandFill(0.0f, Graphics.LineCap.Miter, 2.4f, nvg.pixelRatio);
            }

            // Snapshot the tessellated fill vertices for clip mask re-rendering
            var paths = nvg.pathCache.Paths;
            var stencilVertices = SnapshotFillVertices(paths);
            var bounds = nvg.pathCache.Bounds;

            // Build the clip snapshot chain (for nested clips)
            var state = nvg.stateStack.CurrentState;
            var previousSnapshot = state.ClipActive ? state.ClipSnapshot : null;
            var snapshot = new ClipPathSnapshot(stencilVertices, bounds, evenOdd, previousSnapshot);

            // Update state
            state.ClipActive = true;
            state.ClipSnapshot = snapshot;

            // Tell the renderer to set the clip mask
            nvg.renderer.SetClip(paths, state.Scissor, nvg.pixelRatio.FringeWidth, bounds, evenOdd);
        }

        /// <summary>
        /// Extracts the fill vertices from all paths as a flat Vertex array.
        /// These are the raw triangle-fan points (not yet converted to triangle-list),
        /// which is what the renderer's stencil fill pass needs.
        /// </summary>
        private static Vertex[] SnapshotFillVertices(IReadOnlyList<Path> paths)
        {
            // Count total fill vertices
            int totalCount = 0;
            for (int i = 0; i < paths.Count; i++)
                totalCount += paths[i].Fill.Count;

            if (totalCount == 0)
                return System.Array.Empty<Vertex>();

            // Copy into flat array
            var result = new Vertex[totalCount];
            int offset = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                var fill = paths[i].Fill;
                for (int j = 0; j < fill.Count; j++)
                    result[offset++] = fill[j];
            }

            return result;
        }
    }
}