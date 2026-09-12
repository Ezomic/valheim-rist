using UnityEngine;
using UnityEngine.UI;

namespace Rist
{
    /// <summary>
    /// Redraws the ship's wind ring so its dead-zone black ends where the boat actually stalls.
    ///
    /// The ring is a polar chart of sailing efficiency around the boat, painted into two sprites:
    /// solid black at the bow, fading to white at a beam reach and dark again toward the stern.
    /// It is a Simple image, so the black is baked into the texture rather than drawn from any
    /// number, and once Weatherly narrowed the dead zone the ring went on showing vanilla's. A
    /// player sailed with the wind pointer sitting in the black while the boat moved perfectly
    /// well, which is the HUD telling them the card does nothing.
    ///
    /// Fixed by warping the painting, not by recolouring it. On the bow half only, the painted black
    /// is pulled in so its edge lands where the sail is half filled, and the grey-to-white
    /// gradient is stretched out to fill the gap, so a beam reach stays at 90 degrees and the whole
    /// stern half is untouched. Every colour on screen is still the original artwork; only where the
    /// black ends has moved. See PaintedBlackEdgeLeft for why it anchors on the painted edge, and on
    /// the middle of the fill rather than either end of it.
    ///
    /// A mesh effect on the game's own Image rather than a new texture. The atlas is BC7 and not
    /// CPU-readable, and reading it back through a render target is a colour-space round trip this
    /// codebase has got wrong three times. Here the vanilla Image keeps its sprite, material,
    /// tint and rotation; this only replaces its quad with a ring whose texture coordinates are
    /// warped, so the GPU samples the original atlas with the game's own material and the colours
    /// cannot drift. Nothing is allocated per frame, and nothing is touched at no narrowing.
    /// </summary>
    internal sealed class RingWarp : BaseMeshEffect
    {
        /// <summary>Angular segments round the ring. Plenty for a 130px ring to read as smooth.</summary>
        private const int Segments = 240;

        /// <summary>
        /// The band drawn, in sprite pixels from the centre of a 256px sprite. The painted ring
        /// sits at 117 to 125; the margin either side takes in its anti-aliased edges, and
        /// anything beyond the band is transparent in the texture, so drawing it costs nothing
        /// visible.
        /// </summary>
        private const float InnerRadius = 100f;
        private const float OuterRadius = 127f;

        /// <summary>
        /// Where vanilla's painted black ends, in degrees off the bow, measured off a rip of both ring
        /// sprites: 43.4 on the left and 44.7 on the right, the same at every radius across the band.
        ///
        /// Not a physics angle, and the difference is the whole reason this constant exists. Vanilla's
        /// painting is a hard black step at about 44 degrees, while vanilla's boat is fully dead only
        /// within 36.9 and at full sail beyond 41.4 - so even the unmodded ring shows black over
        /// headings that sail. The warp therefore maps this painted edge onto a physics angle rather
        /// than scaling the painting around one.
        ///
        /// Which physics angle took two rounds of play to settle, because the sail does not switch on
        /// at an edge - it fills over about three degrees. At rank 5 thrust is 0% at 25.8, 31% at 27,
        /// 58% at 28 and 87% at 29. The first version put the black out at 32.4, where the boat was at
        /// nearly full sail with the pointer in the black. The second ended it at the fully dead edge,
        /// 25.8, which painted gold over headings where the sail was barely drawing, and the player
        /// called it over-corrected. It ends halfway through the fill now: just inside the black the
        /// sail is mostly empty, just outside it is mostly full. See Warp.
        ///
        /// One value per side, because the painting is not symmetric. A single value, the wider side's,
        /// left the black ending at 41% thrust on the right and 20% on the left, so the ring felt right
        /// turning one way and over-corrected turning the other. Per side, both land on a half-filled
        /// sail within half a degree of each other. Measured on the gold ring, the one on top.
        /// </summary>
        private const float PaintedBlackEdgeLeft = 43.4f;
        private const float PaintedBlackEdgeRight = 44.3f;

        private float _narrowing;

        /// <summary>Sets the narrowing to draw, rebuilding the mesh only when it actually changes.</summary>
        internal void SetNarrowing(float narrowing)
        {
            if (Mathf.Approximately(narrowing, _narrowing)) return;

            _narrowing = narrowing;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || vh == null) return;

            var scale = 1f - Mathf.Clamp(_narrowing, 0f, Horizon.MaxNarrowingValue);

            // No narrowing: leave vanilla's own quad exactly as the Image built it.
            if (scale >= 0.9999f) return;

            var image = graphic as Image;
            if (image == null || image.sprite == null || vh.currentVertCount == 0) return;

            // The Image's own vertex colour, so its tint and any fade it applies carry over.
            var sample = new UIVertex();
            vh.PopulateUIVertex(ref sample, 0);
            var colour = sample.color;

            var sprite = image.sprite;
            var rect = image.rectTransform.rect;
            var outer = UnityEngine.Sprites.DataUtility.GetOuterUV(sprite);   // xMin, yMin, xMax, yMax
            var padding = UnityEngine.Sprites.DataUtility.GetPadding(sprite);  // left, bottom, right, top

            var spriteW = sprite.rect.width;
            var spriteH = sprite.rect.height;
            var drawnW = spriteW - padding.x - padding.z;
            var drawnH = spriteH - padding.y - padding.w;
            if (drawnW <= 0f || drawnH <= 0f) return;

            var toLocalX = rect.width / spriteW;
            var toLocalY = rect.height / spriteH;
            var centreX = spriteW * 0.5f;
            var centreY = spriteH * 0.5f;

            vh.Clear();

            for (var i = 0; i <= Segments; i++)
            {
                // Angle round the ring from the bow, clockwise positive, in degrees.
                var angle = -180f + 360f * i / Segments;
                var warped = Warp(angle, scale) * Mathf.Deg2Rad;
                var drawn = angle * Mathf.Deg2Rad;

                AddVertex(vh, InnerRadius, drawn, warped, rect, toLocalX, toLocalY, centreX, centreY,
                          padding, drawnW, drawnH, outer, colour);
                AddVertex(vh, OuterRadius, drawn, warped, rect, toLocalX, toLocalY, centreX, centreY,
                          padding, drawnW, drawnH, outer, colour);
            }

            for (var i = 0; i < Segments; i++)
            {
                var a = i * 2;
                vh.AddTriangle(a, a + 1, a + 3);
                vh.AddTriangle(a, a + 3, a + 2);
            }
        }

        /// <summary>
        /// One vertex: drawn at its real angle, textured from the warped one.
        ///
        /// The sampled point is clamped inside the sprite's own texels. Without that, the outer
        /// edge of the band at the bow reaches past the sprite's trimmed border and samples the
        /// next sprite on the atlas.
        /// </summary>
        private static void AddVertex(VertexHelper vh, float radius, float drawn, float warped, Rect rect,
                                      float toLocalX, float toLocalY, float centreX, float centreY,
                                      Vector4 padding, float drawnW, float drawnH, Vector4 outer, Color32 colour)
        {
            var position = new Vector3(
                rect.center.x + radius * Mathf.Sin(drawn) * toLocalX,
                rect.center.y + radius * Mathf.Cos(drawn) * toLocalY,
                0f);

            var sx = Mathf.Clamp(centreX + radius * Mathf.Sin(warped), padding.x, padding.x + drawnW);
            var sy = Mathf.Clamp(centreY + radius * Mathf.Cos(warped), padding.y, padding.y + drawnH);

            var uv = new Vector2(
                Mathf.Lerp(outer.x, outer.z, (sx - padding.x) / drawnW),
                Mathf.Lerp(outer.y, outer.w, (sy - padding.y) / drawnH));

            vh.AddVert(position, colour, uv);
        }

        /// <summary>
        /// Where on vanilla's painting a point at <paramref name="angle"/> should be read from.
        ///
        /// The bow half only. The painted black, which ends at PaintedBlackEdgeLeft and -Right, is mapped onto the
        /// middle of the band where the sail fills - halfway between the narrowed dead edge and the
        /// narrowed live edge - so the black ends where the sail is half drawn. The rest of the bow
        /// half is stretched so a beam reach still reads 90 degrees, and the stern half is not
        /// touched. Continuous at the join.
        ///
        /// Not called at no narrowing: there the Image draws vanilla's own quad, black step and all.
        /// </summary>
        private static float Warp(float angle, float scale)
        {
            var a = Mathf.Abs(angle);
            if (a > 90f) return angle;

            var edge = (Horizon.VanillaDeadAngleDeg + Horizon.VanillaLiveAngleDeg) * 0.5f * scale;
            var painted = angle < 0f ? PaintedBlackEdgeLeft : PaintedBlackEdgeRight;

            var read = a <= edge
                ? a * (painted / edge)
                : painted + (a - edge) * (90f - painted) / (90f - edge);

            return angle < 0f ? -read : read;
        }
    }
}
