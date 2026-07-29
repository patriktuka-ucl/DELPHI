using UnityEngine;
using UnityEngine.UI;

namespace Delphi.Motion
{
    /// <summary>
    /// Bends a single flat uGUI Text into a closed horizontal circle around its
    /// own origin, so one string wraps the whole 360° instead of being cut into
    /// panels.
    ///
    /// WHY A MESH MODIFIER RATHER THAN A RING OF CANVASES:
    ///
    ///   A ring built from N flat panels is N transforms, N canvases and N
    ///   copies of the string, and it only looks circular from the exact centre
    ///   — lean out of the seat and the facets show. Worse, each panel holds
    ///   its own copy of the message, so the thing the participant reads is
    ///   stored in N places and a partial edit leaves the ring saying two
    ///   different things in two different directions.
    ///
    ///   This takes the mesh uGUI already generated for ONE Text and remaps the
    ///   vertices. Every character keeps its own quad, so the curve is as
    ///   smooth as the string is long, and the message exists exactly once.
    ///
    /// THE MAPPING: horizontal position becomes angle. A vertex at fraction t
    /// along the laid-out string goes to (R·sin 2πt, y, R·cos 2πt) — the full
    /// turn, whatever the string's natural width, so the ends always meet.
    /// Letters therefore stretch or condense to fill the circle exactly, which
    /// is the behaviour a wrapped banner wants: the alternative is a wedge of
    /// empty ring in whichever direction the arithmetic did not come out even.
    ///
    /// Height is untouched — a cylinder is flat vertically, so the glyphs are
    /// not distorted, only repositioned.
    ///
    /// The vertices land in front of, behind and to both sides of the Text's
    /// own origin, so the transform this sits on must be AT the centre of the
    /// intended ring — i.e. at the viewer — not out at its radius.
    /// </summary>
    [RequireComponent(typeof(Text))]
    [DisallowMultipleComponent]
    public class RingTextWrap : BaseMeshEffect
    {
        [Tooltip("Ring radius in CANVAS PIXELS, not metres — the canvas's own " +
                 "scale converts it. Set from SafetyStopOverlay.")]
        public float radiusPixels = 600f;

        /// <summary>Changes the radius and regenerates the mesh. Silent no-op
        /// if nothing moved, so it is safe to call every frame.</summary>
        public void SetRadiusPixels(float px)
        {
            if (Mathf.Approximately(px, radiusPixels)) return;
            radiusPixels = px;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || vh.currentVertCount == 0) return;

            // The span comes from the GLYPHS, not from the RectTransform: with
            // horizontal overflow on, the text is wider than its rect and the
            // rect's width has nothing to do with where the characters ended
            // up. Measuring the rect instead would put the seam in the wrong
            // place and leave the ring open.
            var v = default(UIVertex);
            float xMin = float.MaxValue, xMax = float.MinValue;
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                if (v.position.x < xMin) xMin = v.position.x;
                if (v.position.x > xMax) xMax = v.position.x;
            }

            float span = xMax - xMin;
            if (span < 1e-3f) return;         // one glyph, or none — nothing to bend

            float r = Mathf.Max(1e-3f, radiusPixels);
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                float phi = (v.position.x - xMin) / span * Mathf.PI * 2f;
                v.position = new Vector3(r * Mathf.Sin(phi), v.position.y, r * Mathf.Cos(phi));
                vh.SetUIVertex(v, i);
            }
        }

        /// <summary>Metres of arc one repeat would occupy at its natural width,
        /// given the laid-out string width and how many times it repeats. Used
        /// by SafetyStopOverlay to warn when the letters are being squeezed or
        /// stretched hard enough to notice.</summary>
        public static float FitFactor(float circumference, float naturalWidth) =>
            naturalWidth <= 1e-3f ? 1f : circumference / naturalWidth;
    }
}
