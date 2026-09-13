using System.Collections.Generic;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// One runestone per rist, generated on first sight and kept for the session.
    ///
    /// Authoring a texture is normally the wrong answer in this repo - the game has art and
    /// borrowing it matches by construction. It is right here for one reason: a stone is a
    /// *shape*, not a material. There is nothing in the game to match it against, and
    /// generating it sidesteps the problem that cost three attempts on the wooden panel,
    /// because pixels written here are drawn in the space they were written in.
    ///
    /// Two mistakes are recorded in the shading below, both from looking at it in game. Perlin
    /// grain, meant to stop a stone reading as a gradient, dimpled at 0.09 frequency
    /// downsampled from 128 to 100 - twenty-five golf balls. And the gradient ran to the far
    /// corner of the texture rather than the far edge of the disc, so its two darkest stops
    /// fell outside the circle and every stone came out pale.
    ///
    /// Everything varies off one seed, the card's own id: the outline, the rock it is cut
    /// from, and which runes are cut into it. Seeded rather than random so a rist looks the
    /// same in every session and on every machine - the stone becomes part of how you
    /// recognise a card, which only works if it never changes.
    /// </summary>
    internal static class Stones
    {
        private const int Size = 128;

        // Room inside the texture for the stone to cast a shadow without being clipped by its
        // own bounds, and for the outline to bulge into.
        private const float Margin = 9f;

        private static readonly Dictionary<string, Texture2D> _stones = new Dictionary<string, Texture2D>();

        /// <summary>
        /// The rocks a stone can be cut from. Each is a gradient from its lit face to its
        /// shadowed edge, and they are deliberately far apart - a field of twenty-five is only
        /// worth scanning if two stones beside each other are obviously different rock.
        /// </summary>
        private static readonly Color[][] Rocks =
        {
            // granite - the grey of the mockup
            new[] { Hex(0x7C, 0x77, 0x6F), Hex(0x60, 0x5C, 0x55), Hex(0x42, 0x3F, 0x3A), Hex(0x32, 0x2F, 0x2A) },
            // basalt - cold and dark
            new[] { Hex(0x5A, 0x5E, 0x66), Hex(0x44, 0x48, 0x4F), Hex(0x2E, 0x31, 0x37), Hex(0x21, 0x23, 0x28) },
            // sandstone - warm, the lightest of them
            new[] { Hex(0x9A, 0x88, 0x6B), Hex(0x7E, 0x6D, 0x53), Hex(0x5A, 0x4C, 0x39), Hex(0x42, 0x37, 0x28) },
            // slate - blue and flat
            new[] { Hex(0x66, 0x6E, 0x76), Hex(0x4E, 0x55, 0x5C), Hex(0x36, 0x3B, 0x41), Hex(0x26, 0x2A, 0x2E) },
            // greenstone - mossy, the odd one out
            new[] { Hex(0x6C, 0x77, 0x63), Hex(0x54, 0x5E, 0x4C), Hex(0x3A, 0x42, 0x35), Hex(0x2A, 0x30, 0x26) },
            // ironstone - rusted, warm dark
            new[] { Hex(0x77, 0x62, 0x55), Hex(0x5D, 0x4B, 0x40), Hex(0x40, 0x33, 0x2B), Hex(0x2E, 0x25, 0x1F) },
        };

        private static readonly float[] Stops = { 0f, 0.44f, 0.78f, 1f };

        /// <summary>
        /// The pool the marks around a stone are drawn from. Latin letters, because the game's
        /// "rune" face is a Latin-mapped decorative font - type F, get the rune - and runic
        /// code points come out of it as empty boxes.
        /// </summary>
        private static readonly string[] Pool =
        {
            "F", "U", "TH", "A", "R", "K", "G", "W", "H", "N", "I", "J",
            "P", "Z", "S", "T", "B", "E", "M", "L", "NG", "D", "O", "Y",
        };

        /// <summary>The stone this rist is cut into, built the first time it is drawn.</summary>
        internal static Texture2D For(Card card)
        {
            if (card == null) return null;

            if (_stones.TryGetValue(card.Id, out var tex) && tex != null) return tex;

            tex = Build(Seed(card));
            _stones[card.Id] = tex;
            return tex;
        }

        /// <summary>
        /// The runes cut into this rist, one per rank, in the order they are cut. Drawn from
        /// the same seed as the stone, so a rist's marks are as much a part of recognising it
        /// as its shape - and distinct within a stone, because the same rune twice would read
        /// as a mistake.
        /// </summary>
        internal static string[] MarksFor(Card card, int count)
        {
            var rng = new System.Random(Seed(card) ^ 0x5EED);
            var pool = new List<string>(Pool);
            var marks = new string[count];

            for (var i = 0; i < count; i++)
            {
                if (pool.Count == 0) pool.AddRange(Pool);

                var pick = rng.Next(pool.Count);
                marks[i] = pool[pick];
                pool.RemoveAt(pick);
            }

            return marks;
        }

        private static int Seed(Card card)
        {
            return card.Id.GetStableHashCode();
        }

        private static Texture2D Build(int seed)
        {
            var tex = New();

            var rng = new System.Random(seed);
            var rock = Rocks[rng.Next(Rocks.Length)];

            // Three harmonics with seeded phases. Enough to make an outline that is clearly
            // not a circle, and few enough that it stays a boulder rather than becoming a
            // blob - the amplitudes are small on purpose, because the marks are placed on a
            // fixed radius and a deep dent would push one outside the stone.
            var phase1 = (float)rng.NextDouble() * Mathf.PI * 2f;
            var phase2 = (float)rng.NextDouble() * Mathf.PI * 2f;
            var phase3 = (float)rng.NextDouble() * Mathf.PI * 2f;
            var lean = (float)rng.NextDouble() * Mathf.PI * 2f;

            var baseRadius = Size * 0.5f - Margin;
            var centre = new Vector2(Size * 0.5f, Size * 0.5f);

            // Up and left of centre, as the mockup's "circle at 34% 28%", nudged per stone so
            // the light does not land identically on all twenty-five.
            var origin = centre + new Vector2(-baseRadius * 0.38f, -baseRadius * 0.44f) +
                         new Vector2(Mathf.Cos(lean), Mathf.Sin(lean)) * baseRadius * 0.06f;

            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    // SetPixel counts y from the bottom and GUI.DrawTexture draws from the
                    // top, so the work is done in flipped space. Without this the light comes
                    // from below and a stone reads as a hole.
                    var p = new Vector2(x + 0.5f, Size - 1 - y + 0.5f);
                    var offset = p - centre;
                    var d = offset.magnitude;

                    var angle = Mathf.Atan2(offset.y, offset.x);
                    var radius = baseRadius * (1f
                        + 0.055f * Mathf.Sin(angle * 3f + phase1)
                        + 0.032f * Mathf.Sin(angle * 5f + phase2)
                        + 0.020f * Mathf.Sin(angle * 7f + phase3));

                    // The gradient ends at the far edge of the stone, not of the texture. It
                    // was measured to the texture corner once, and the two darkest stops fell
                    // outside the outline and never drew - every stone came out pale.
                    var far = Vector2.Distance(origin, centre) + radius;
                    var colour = Sample(rock, Mathf.Clamp01(Vector2.Distance(p, origin) / far));

                    var intoRim = Mathf.Clamp01((radius - d) / (radius * 0.2f));
                    var vertical = offset.y / radius;

                    // Weight along the lower inside edge, and a thin lit face along the top.
                    colour = Color.Lerp(colour, Color.black, Mathf.Clamp01(vertical) * (1f - intoRim) * 0.55f);
                    colour = Color.Lerp(colour, Color.white, Mathf.Clamp01(-vertical) * (1f - intoRim) * 0.10f);

                    var stone = Mathf.Clamp01(radius - d);

                    var shadow = Mathf.Clamp01(1f - (Vector2.Distance(p, centre + new Vector2(0f, -3f)) - radius) / 6f) *
                                 0.5f * (1f - stone);

                    colour.a = stone;
                    if (stone < 1f) colour = Color.Lerp(new Color(0f, 0f, 0f, shadow), colour, stone);

                    tex.SetPixel(x, y, colour);
                }
            }

            // With mipmaps, because the panel draws this 128px disc at 100, 78 or 64 depending
            // on the screen, and a single 128px level shimmers when drawn small. See New() for
            // why the bias matters as much as the mips.
            tex.Apply(true);
            return tex;
        }

        /// <summary>Multi-stop gradient sampling, the way a CSS gradient interpolates.</summary>
        private static Color Sample(Color[] colours, float t)
        {
            for (var i = 1; i < Stops.Length; i++)
            {
                if (t > Stops[i]) continue;

                var span = Stops[i] - Stops[i - 1];
                var k = span <= 0f ? 0f : (t - Stops[i - 1]) / span;
                return Color.Lerp(colours[i - 1], colours[i], k);
            }

            return colours[colours.Length - 1];
        }

        private static Color Hex(int r, int g, int b)
        {
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }

        private static Texture2D New()
        {
            return new Texture2D(Size, Size, TextureFormat.RGBA32, true)
            {
                filterMode = FilterMode.Bilinear,

                // Bilinear picks the nearest mip level rather than blending two, and at 78px the
                // nearest is the 64px level, stretched - softer than the 128 it replaced. A small
                // negative bias makes each drawn size take the level at or above its own size:
                // 100 and 78 sample 128, and 64 samples 64 at one to one.
                mipMapBias = -0.3f,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }
    }
}
