using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The fallback experience bar: two flat rectangles, drawn only when HudBar has not
    /// managed to clone one of the game's own upright bars, or when the clone is switched
    /// off. It never matched vanilla and was not going to - a borrowed bar brings a frame, a
    /// bevelled track, softened ends and a trailing fill that a 1x1 texture cannot fake - but
    /// it draws under any HUD hierarchy, which is exactly what a fallback is for.
    ///
    /// It also keeps the two pieces of text in either mode. The level goes under the bar
    /// unless the clone brought a text of its own along, and the waiting note is drawn here
    /// in both, positioned off whichever bar is actually up.
    ///
    /// Position is config rather than computed. Anchoring to the real health bar would mean
    /// converting a scaled Canvas RectTransform into IMGUI screen space, which breaks
    /// differently at every HUD scale and resolution; a pair of numbers anyone can nudge is
    /// both simpler and easier to correct when it lands wrong.
    /// </summary>
    internal static class XpBar
    {
        private static Texture2D _track, _fill;

        /// <summary>The canvas scale the styles were built at, so a HUD-scale change rebuilds them.</summary>
        private static float _builtAt = float.NaN;
        private static GUIStyle _label, _waiting;

        private static readonly Color TrackColour = new Color(0.227f, 0.188f, 0.145f, 0.9f);
        private static Color FillColour = new Color(0.83f, 0.663f, 0.29f, 1f);

        internal static void Draw()
        {
            if (!RistConfig.Enabled.Value || !RistConfig.ShowXpBar.Value) return;
            if (!ClientState.Known) return;

            var player = Player.m_localPlayer;
            if (player == null || player.IsDead()) return;

            // Follow the game's own idea of whether the interface is up: hidden while a menu
            // or a container is open, and while the player has pressed the hide-HUD key.
            if (InventoryGui.IsVisible() || Menu.IsVisible()) return;
            if (Hud.instance != null && Hud.instance.m_userHidden) return;

            Build();

            // The clone draws itself in canvas space; all that is left here is the text it
            // cannot carry. Its length is in canvas units, so the offsets come back from
            // HudBar already converted to pixels.
            if (HudBar.Live)
            {
                // Asked of the bar rather than recomputed from config. BarPosX/BarPosY are
                // only where the bar is when BarFollowStamina is off, and it has defaulted to
                // on since the bar started following the stamina bar - so this drew the note at
                // a point the bar had long since left, which is what put it over the guardian
                // power. ScreenCentre is wherever Place() actually put it, at any resolution,
                // HUD scale, and with the build panel up.
                var centre = HudBar.ScreenCentre;
                var centreX = centre.HasValue ? centre.Value.x : RistConfig.BarPosX.Value;
                var centreY = centre.HasValue ? centre.Value.y : Screen.height - RistConfig.BarPosY.Value;
                var half = HudBar.HalfLength;

                if (!HudBar.HasText)
                {
                    var s = HudBar.Scale;

                    _label.alignment = TextAnchor.UpperCenter;
                    GUI.Label(new Rect(centreX - 24f * s, centreY + half + 3f * s, 48f * s, 18f * s),
                              ClientState.Level.ToString(), _label);
                }

                if (ClientState.HasPick)
                {
                    // Centred above the bar, on one line. Beside it meant reserving room next
                    // to a bar whose length is config and whose neighbours are vanilla's; above
                    // it there is nothing to collide with, and it reads as belonging to the bar
                    // rather than floating next to it.
                    // Off the bar's measured top edge, not off its centre. Deriving the
                    // offset from the root's height put the note on top of the bar, because
                    // the clone's root is the whole borrowed eitr panel and its height is not
                    // the visible bar's thickness.
                    var scale = HudBar.Scale;
                    var barTop = HudBar.ScreenTop ?? centreY;
                    var noteBottom = barTop - Mathf.Max(0f, RistConfig.BarNoteGap.Value) * scale;

                    // The box scales with the text it has to hold, or the label clips on a
                    // screen where the font has grown.
                    var w = 240f * scale;
                    var h = 22f * scale;

                    _waiting.alignment = TextAnchor.LowerCenter;
                    GUI.Label(new Rect(centreX - w * 0.5f, noteBottom - h, w, h),
                              "rist waiting", _waiting);
                }

                return;
            }

            var x = RistConfig.BarX.Value;
            var thickness = Mathf.Max(3f, RistConfig.BarThickness.Value);
            var length = Mathf.Max(10f, RistConfig.BarLength.Value);

            // Upright, to stand beside the health bar rather than lie under it. IMGUI measures
            // from the top, so the bottom-anchored offset is subtracted and the bar is drawn
            // upward from there.
            var bottom = Screen.height - RistConfig.BarBottom.Value;
            var top = bottom - length;

            GUI.DrawTexture(new Rect(x, top, thickness, length), _track);

            // Fills from the bottom up, the way the health bar beside it does.
            var progress = Mathf.Clamp01(Levels.Progress(ClientState.Xp));
            if (progress > 0f)
            {
                var filled = length * progress;
                GUI.DrawTexture(new Rect(x, bottom - filled, thickness, filled), _fill);
            }

            // Just the level. The exact numbers live on the rists panel, which is what it
            // is for - a permanent "24 / 60" is noise you stop reading by the second hour.
            _label.alignment = TextAnchor.UpperCenter;
            GUI.Label(new Rect(x - 14f, bottom + 3f, thickness + 28f, 18f),
                      ClientState.Level.ToString(), _label);

            // Only while something is actually waiting. The centre message announcing a rist
            // fades after a few seconds, and one missed during a fight would otherwise leave a
            // card unclaimed with nothing on screen to say so.
            if (!ClientState.HasPick) return;

            _waiting.alignment = TextAnchor.MiddleLeft;
            GUI.Label(new Rect(x + thickness + 6f, top, 200f, length),
                      "rist\nwaiting", _waiting);
        }

        private static void Build()
        {
            // Rebuilt when the colour changes, so nudging it in the cfg shows up without a
            // restart - the same reason HudBar re-reads its own size and tint.
            var tint = RistConfig.BarTint();
            var scale = HudBar.Scale;

            if (_label != null && FillColour == tint && Mathf.Approximately(_builtAt, scale)) return;

            FillColour = tint;
            _builtAt = scale;
            _track = Solid(TrackColour);
            _fill = Solid(FillColour);

            // 12 is the floor rather than the size: it is the smallest that reads on this
            // setup, and on a screen where the HUD is scaled up a fixed 12 would be a speck
            // beside a bar that grew with it.
            var font = Mathf.Max(12, Mathf.RoundToInt(12f * scale));

            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = font,
                normal = { textColor = FillColour },
                wordWrap = false,
                richText = false,
            };

            _waiting = new GUIStyle(_label) { fontSize = font, wordWrap = false };
        }

        private static Texture2D Solid(Color colour)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, colour);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
