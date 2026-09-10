using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Rist
{
    /// <summary>
    /// The experience bar, built by cloning one of the game's own upright bars.
    ///
    /// The first version drew two flat IMGUI rectangles. It sat in the right place and still
    /// read as a mod: a vanilla bar carries a frame sprite, a bevelled inner track, softened
    /// ends, and a second bar behind the first that lags a change - none of which survives
    /// being approximated with a 1x1 texture. Cloning the real thing inherits all of it at
    /// once, the same argument as borrowing a material rather than authoring a texture.
    ///
    /// The donor is the eitr bar, with the stamina bar behind it. Both are an ordinary
    /// GuiBar pair turned on their side: GuiBar only ever resizes its fill on
    /// RectTransform.Axis.Horizontal, so an upright vanilla bar is a *rotated* horizontal
    /// one, and cloning is the only way to get that geometry without rebuilding it.
    ///
    /// XpBar stays behind this as the fallback. A HUD hierarchy is scene data rather than
    /// API, so this can be wrong in ways ilspy cannot warn about; falling back to a bar that
    /// certainly draws beats falling back to an empty corner.
    /// </summary>
    internal static class HudBar
    {
        private const float BorderBuffer = 16f;   // Hud.m_staminaBarBorderBuffer, which every
                                                  // upright bar adds to its root's length.

        private static GameObject _root;
        private static RectTransform _rect;
        /// <summary>
        /// The two fills, held as the RectTransform and Image they really are rather than as
        /// GuiBar components - because the GuiBars are destroyed at build time. See Build.
        /// </summary>
        private static RectTransform _fastBar, _slowBar;
        private static Image _fastFill, _slowFill;

        /// <summary>Hides the bar without ever deactivating it. See Update.</summary>
        private static CanvasGroup _group;

        private static TMP_Text _text;
        private static Animator _animator;
        private static Canvas _canvas;


        /// <summary>True while Announce is driving the fill colour, so it knows to put it back.</summary>
        private static bool _pulsing;
        private static bool _failed;
        private static int _shownLevel = -1;
        private static int _shownPercent = -1;
        private static bool _shownOwed;

        private static float _sizedAt;

        /// <summary>Where the trailing fill currently sits, 0..1. Negative until first drawn,
        /// so a fresh bar starts level with the real one rather than draining in from empty.</summary>
        private static float _trail = -1f;

        /// <summary>How fast the trailing fill catches up, in fractions of the bar per second.</summary>
        private const float TrailSpeed = 0.5f;
        private static bool _uprightAt;
        private static string _tintedAt;

        /// <summary>True while the cloned bar exists, which is what XpBar stands down for.</summary>
        internal static bool Live => _root != null;

        /// <summary>Whether the clone brought a text along to put the level in.</summary>
        internal static bool HasText => _text != null;

        /// <summary>
        /// Half the bar's on-screen length in pixels, so IMGUI can hang a note off a bar that
        /// lives in canvas units. The canvas scale is the whole conversion: the bar's length
        /// is set in canvas units and the scaler multiplies it to pixels.
        /// </summary>
        /// <summary>
        /// Where the bar actually is, in IMGUI screen pixels, or null while it is not up.
        ///
        /// IMGUI measures y from the top and Unity's screen space from the bottom, so the
        /// flip is done here rather than at every call site.
        ///
        /// This exists because the note beside the bar was drawn from BarPosX/BarPosY, which
        /// are only where the bar is when BarFollowStamina is off - and it has defaulted to on
        /// since the bar started following the stamina bar. The note was left at the old fixed
        /// point and drifted away from the thing it labels.
        /// </summary>
        internal static Vector2? ScreenCentre
        {
            get
            {
                if (_rect == null) return null;

                var cam = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? _canvas.worldCamera
                    : null;

                var point = RectTransformUtility.WorldToScreenPoint(cam, _rect.position);
                return new Vector2(point.x, Screen.height - point.y);
            }
        }

        /// <summary>Reused so a per-frame OnGUI read does not allocate four corners each time.</summary>
        private static readonly Vector3[] Corners = new Vector3[4];

        /// <summary>Last measured top, so the Verbose line prints on change rather than per frame.</summary>
        private static float _loggedTop = float.NaN;

        /// <summary>
        /// The bar's top edge in IMGUI screen pixels, or null while it is not up.
        ///
        /// Measured from the rect's own world corners rather than derived from a centre and a
        /// half-height. The first attempt did the latter and put the note on top of the bar:
        /// the clone's root is the whole borrowed eitr panel, whose height is not the visible
        /// bar's thickness, so the offset it produced was too small to clear it.
        ///
        /// Taking the smallest IMGUI y across all four corners also makes this correct when
        /// BarUpright rotates the bar ninety degrees, where "height" and "thickness" swap over
        /// and any single-axis arithmetic would be wrong.
        /// </summary>
        internal static float? ScreenTop
        {
            get
            {
                if (_rect == null) return null;

                var cam = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? _canvas.worldCamera
                    : null;

                var top = float.MaxValue;

                // Every child, not just the root. The root's own rect is a thin strip and the
                // panel's frame and track are children that extend past it, so measuring the
                // root alone returned a line that runs through the middle of what you can see
                // - which put the note on the bar twice running.
                //
                // GetComponentsInChildren(true) rather than the active ones: nothing here is
                // ever deactivated now, but a rect that is off still occupies space and a note
                // that jumps when something toggles is worse than one placed slightly high.
                foreach (var rect in _rect.GetComponentsInChildren<RectTransform>(true))
                {
                    rect.GetWorldCorners(Corners);

                    foreach (var corner in Corners)
                    {
                        var point = RectTransformUtility.WorldToScreenPoint(cam, corner);
                        var y = Screen.height - point.y;   // IMGUI measures from the top
                        if (y < top) top = y;
                    }
                }

                if (top == float.MaxValue) return null;

                if (RistConfig.Verbose.Value && !Mathf.Approximately(top, _loggedTop))
                {
                    _loggedTop = top;
                    RistPlugin.Log.LogInfo("Bar top edge measured at y=" + Mathf.RoundToInt(top)
                                           + " of " + Screen.height + ", canvas scale "
                                           + Scale.ToString("0.00") + ".");
                }

                return top;
            }
        }

        /// <summary>
        /// The HUD canvas's scale factor, or 1 while there is no canvas.
        ///
        /// Every pixel number in this mod's config is written against this. The bar's position
        /// is a server setting - the host decides where it sits, so that everyone on a server
        /// sees the same layout - and a raw pixel offset cannot deliver that: 70 pixels below
        /// the stamina bar is a different place on a 1080p screen, a 1440p screen and a player
        /// running the HUD at 1.4. The scale factor is what Valheim's own CanvasScaler applies
        /// to the HUD, so multiplying by it turns one imposed number into the same *visual*
        /// position on every screen.
        ///
        /// The bar's length needs no such treatment: BarSize is set in canvas units through
        /// SetSizeWithCurrentAnchors, and the scaler is already applying this to it.
        /// </summary>
        internal static float Scale
        {
            get { return _canvas != null ? _canvas.scaleFactor : 1f; }
        }

        internal static float HalfLength
        {
            get
            {
                var scale = _canvas != null ? _canvas.scaleFactor : 1f;
                return Mathf.Max(16f, RistConfig.BarSize.Value) * 0.5f * scale;
            }
        }

        internal static void Update()
        {
            if (!RistConfig.Enabled.Value || !RistConfig.ShowXpBar.Value || !RistConfig.VanillaBar.Value)
            {
                Drop();
                return;
            }

            // The Hud is rebuilt with every world, taking our child with it. Both halves of
            // that are handled by treating a missing root as "build one".
            if (Hud.instance == null || Player.m_localPlayer == null)
            {
                Drop();
                return;
            }

            if (_root == null && !Build()) return;

            // Hidden by alpha, never by SetActive, and this is the whole bug of 2026-09-10.
            //
            // Disabling a GameObject disables its Animator, and Unity's
            // keepAnimatorStateOnDisable defaults to false - so re-enabling REBINDS the
            // animator, resetting every parameter to its controller default. The clone's
            // entire appearance came from one SetBool("Visible", true) written once in Build,
            // and vanilla's hide clip deactivates the bar's children by name through
            // AnimationObjectToggle. So the first time this bar was hidden it came back with
            // Visible false, the show clip never replayed, the children stayed inactive, and
            // the bar was gone for the rest of the session.
            //
            // It was reported as a dedicated-server bug because ClientState.Known is false for
            // the first frames after a remote join, so Visible() returned false immediately
            // after Build and killed the bar before anyone saw it. It was never server-only:
            // pressing the hide-HUD key twice, or dying once, did it in singleplayer too.
            // Confirmed by doing exactly that.
            //
            // A CanvasGroup has none of that history. Nothing is ever disabled, so nothing
            // rebinds.
            var wanted = Visible();
            if (_group != null) _group.alpha = wanted ? 1f : 0f;
            if (!wanted) return;

            Place();

            // Length and colour are re-read rather than set once at build. Both are numbers
            // that get nudged, and a bar you have to restart the game to re-measure is a bar
            // that stays slightly wrong.
            if (!Mathf.Approximately(_sizedAt, RistConfig.BarSize.Value)) Size();
            if (_uprightAt != RistConfig.BarUpright.Value) Lay();
            if (_tintedAt != RistConfig.BarColour.Value) Colour();

            var progress = Mathf.Clamp01(Levels.Progress(ClientState.Xp));
            Fill(progress);

            // Level and percentage together. The level alone said nothing about how close the
            // next one was, and the bar alone is hard to read at a glance when it is a thin
            // upright sliver - a number is the only thing that answers "how far" exactly.
            var percent = Mathf.Clamp(Mathf.RoundToInt(progress * 100f), 0, 100);

            var owed = ClientState.HasPick;

            if (_text != null && (_shownLevel != ClientState.Level || _shownPercent != percent || _shownOwed != owed))
            {
                _shownLevel = ClientState.Level;
                _shownPercent = percent;
                _shownOwed = owed;
                // A star when a pick is waiting, on the bar itself rather than beside it. The
                // note that used to sit alongside was drawn at a fixed screen position and
                // ended up over the guardian power once the bar started following stamina.
                _text.text = _shownLevel + " · " + percent + "%" + (ClientState.HasPick ? " ★" : "");
            }

            Announce();
        }

        /// <summary>
        /// Always up, unlike the bars it was cloned from.
        ///
        /// Vanilla hides stamina and eitr a second after they fill, because they are things
        /// you glance at while they are moving. Experience is the opposite - it is a slow
        /// number you want to be able to check at any moment - so the only things that hide
        /// it are the two that hide everything: the player pressing the hide-HUD key, and
        /// being dead.
        ///
        /// It no longer hides behind the inventory either. That window does not cover this
        /// corner, and having it vanish while you were reading it was the reason to change.
        /// </summary>
        private static bool Visible()
        {
            if (!ClientState.Known) return false;
            if (Player.m_localPlayer.IsDead()) return false;

            // Hud parks its whole root at x=10000 during a cutscene rather than hiding it, and
            // Place() re-pins this bar to a screen point every frame - so without this the bar
            // is being positioned against a HUD that is not where it appears to be.
            if (Player.m_localPlayer.InCutscene()) return false;

            return !Hud.instance.m_userHidden;
        }

        /// <summary>
        /// A waiting card flashes the bar, using the animator trigger the eitr bar already
        /// has for the same job. The centre message fades after a few seconds and one missed
        /// during a fight would otherwise leave a card unclaimed with nothing on screen to
        /// say so.
        /// </summary>
        private static void Announce()
        {
            // By hand, because the donor's animator is destroyed at build time now and its
            // Flash trigger went with it. That is the right trade: the trigger was reached
            // through the same borrowed controller that was hiding the whole bar.
            //
            // A slow brightening of the leading fill rather than a blink. It has to be undone
            // as well as applied, so the pulse writes the fill directly and Colour() is asked
            // to restore the flat colour on the frame the pick is spent - otherwise the bar
            // keeps whatever brightness the pulse was at when the star went away.
            if (_fastFill == null) return;

            if (!ClientState.HasPick)
            {
                if (_pulsing) { _pulsing = false; Colour(); }
                return;
            }

            _pulsing = true;

            var period = Mathf.Max(1f, RistConfig.BarFlashSeconds.Value);
            var phase = Mathf.PingPong(Time.time / period * 2f, 1f);
            var tint = RistConfig.BarTint();

            _fastFill.color = Color.Lerp(tint, Color.white, phase * 0.5f);
        }

        private static bool Build()
        {
            if (_failed) return false;

            var donor = Hud.instance.m_eitrBarRoot != null
                ? Hud.instance.m_eitrBarRoot
                : Hud.instance.m_staminaBar2Root;

            if (donor == null || donor.parent == null)
            {
                Fail("no upright bar to clone from - falling back to the plain bar.");
                return false;
            }

            // Same parent, so canvas scale, sort order and the HUD's own show/hide all come
            // along. Position is overridden below; everything else is inherited on purpose.
            var go = Object.Instantiate(donor.gameObject, donor.parent);
            go.name = "RistXpBar";
            go.SetActive(true);

            _rect = go.GetComponent<RectTransform>();
            _canvas = go.GetComponentInParent<Canvas>();

            // Which bar is which is read off the components rather than off child names: the
            // trailing one is the one told to smooth. Names are scene data and would be a
            // guess, m_smoothDrain is public API and is not.
            GuiBar fast = null, slow = null;

            foreach (var bar in go.GetComponentsInChildren<GuiBar>(true))
            {
                if (bar.m_smoothDrain || bar.m_smoothFill) { if (slow == null) slow = bar; }
                else if (fast == null) fast = bar;
            }

            if (fast == null) fast = slow;
            if (slow == null) slow = fast;

            if (fast == null)
            {
                Object.Destroy(go);
                Fail("the cloned bar has no GuiBar - falling back to the plain bar.");
                return false;
            }

            // Take what the GuiBars point at, then destroy the GuiBars themselves.
            //
            // Rist already drives both fills by hand and the comment on Fill explains why: the
            // component caches m_barImage in Awake and re-reads m_width on the first SetValue,
            // and on a clone neither has happened. That comment also says GuiBar.LateUpdate
            // "never runs, because the donor's parts are inactive" - which was true, and stops
            // being true the moment Unfade activates them below. Then LateUpdate runs
            // SetBar(m_smoothValue) -> m_bar.SetSizeWithCurrentAnchors(Horizontal, m_width * i)
            // AFTER this class has written the width, with an m_width captured from a donor
            // whose fills are zero wide for a character with no eitr. The bar would come back
            // and immediately be squashed to nothing.
            //
            // Destroying the component removes the LateUpdate overwrite, the Awake-cached
            // image and the Awake-captured width in one move, permanently, rather than racing
            // all three. DestroyImmediate because the fields below are read in this same frame
            // and a deferred Destroy leaves them alive until the end of it.
            _fastBar = fast.m_bar;
            _slowBar = slow.m_bar;
            _fastFill = _fastBar != null ? _fastBar.GetComponent<Image>() : null;
            _slowFill = _slowBar != null ? _slowBar.GetComponent<Image>() : null;

            if (slow != fast) Object.DestroyImmediate(slow);
            Object.DestroyImmediate(fast);

            _text = go.GetComponentInChildren<TMP_Text>(true);

            _animator = go.GetComponent<Animator>();
            if (_animator == null) _animator = go.GetComponentInChildren<Animator>(true);

            Size();
            Colour();
            Lay();

            // Always by hand, never by driving the donor's animator.
            //
            // This used to be "if (_hasVisible) _animator.SetBool("Visible", true);" and that
            // one write was the only thing making the whole bar visible - frame, track, both
            // fills and text. It is a borrowed controller whose entire job is to hide this
            // panel when the player has no eitr, and it takes the panel back down at the first
            // opportunity. Handing a permanent bar's visibility to it was never going to hold.
            //
            // Unfade destroys the animator and brings the parts up directly, which is what
            // this bar wants in every case rather than only when the parameter is missing.
            _group = go.GetComponent<CanvasGroup>();
            if (_group == null) _group = go.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;
            _group.interactable = false;

            Unfade(go);

            _root = go;
            _shownLevel = -1;
            _trail = -1f;   // A rebuilt bar starts level, not draining in from empty.

            RistPlugin.Log.LogInfo("Experience bar cloned from " + donor.name + ".");
            return true;
        }

        /// <summary>
        /// The rescue for a donor whose animator we cannot drive: stop it animating and undo
        /// whatever fade it was frozen mid-way through.
        ///
        /// Only alphas that are *effectively invisible* are raised. Several of these pieces
        /// are meant to be semi-transparent - the darkened track especially - so forcing
        /// everything to opaque would trade an invisible bar for a wrong-looking one.
        /// </summary>
        private static void Unfade(GameObject go)
        {
            if (_animator != null) Object.Destroy(_animator);

            // Gone with it goes the donor's Flash trigger; Announce drives the pulse itself.
            _animator = null;

            // The children first, and this is the half that was missing. Vanilla's hide clip
            // does not fade this panel out, it DEACTIVATES its parts by name through
            // AnimationObjectToggle - so for a character with no eitr the donor's fills are
            // inactive GameObjects at the moment they are cloned, and no amount of alpha
            // raising brings back an object that is switched off.
            //
            // The clone is ours and every part of it is meant to be up, so this is
            // unconditional rather than a repair of specific names.
            foreach (var child in go.GetComponentsInChildren<Transform>(true))
                if (!child.gameObject.activeSelf) child.gameObject.SetActive(true);

            foreach (var group in go.GetComponentsInChildren<CanvasGroup>(true))
                if (group.alpha < 0.05f) group.alpha = 1f;

            foreach (var graphic in go.GetComponentsInChildren<Graphic>(true))
            {
                var c = graphic.color;
                if (c.a >= 0.05f) continue;

                c.a = 1f;
                graphic.color = c;
            }

            if (RistConfig.Verbose.Value)
                RistPlugin.Log.LogInfo("Cloned bar detached from the donor's animator and brought up by hand.");
        }

        /// <summary>
        /// Length, in canvas units, set the way Hud.SetEitrBarSize sets it: the root carries
        /// the border buffer and the two fills do not. Vanilla sizes these from max stamina
        /// or max eitr, which is meaningless for a bar that is always 0..1, so it is config
        /// with a starting stamina bar's 64 as the default.
        /// </summary>
        private static void Size()
        {
            _sizedAt = RistConfig.BarSize.Value;
            var length = Mathf.Max(16f, _sizedAt);

            _rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, length + BorderBuffer);

            // No SetWidth. The length is held here and applied by Fill, for the reasons below.
            if (RistConfig.Verbose.Value) RistPlugin.Log.LogInfo("Bar sized to " + length + ".");
        }

        /// <summary>
        /// Set both fills directly, rather than through GuiBar's value machinery.
        ///
        /// Three separate bugs in this bar came from that machinery, all the same shape: it
        /// caches or defers something at a moment a freshly cloned, still-inactive object is
        /// not in.
        ///
        ///   - m_barImage is cached in Awake, so SetColor silently did nothing and the bar
        ///     wore the eitr donor's purple whatever BarColour said.
        ///   - m_width is re-read from the fill's own size on the first SetValue, discarding
        ///     what SetWidth was told, so 43% drew as 43% of the donor's 64 on a track of 240.
        ///   - and the one that made the last fix worse: SetValue after the first does not
        ///     draw anything at all. It stores the value, and SetBar is reached from
        ///     LateUpdate - which never runs, because the donor's parts are inactive for a
        ///     character with no eitr. Spending the first SetValue on zero therefore left the
        ///     bar permanently empty.
        ///
        /// SetBar is one line, and every one of those failures is a way of not reaching it:
        ///
        ///     m_bar.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, m_width * i);
        ///
        /// So the fill is written here instead. Nothing is cached, nothing waits for a
        /// lifecycle callback, and the trailing bar is lagged by hand - which it has to be
        /// anyway, since GuiBar's own smoothing also lives in the LateUpdate that never runs.
        /// </summary>
        private static void Fill(float progress)
        {
            var length = Mathf.Max(16f, RistConfig.BarSize.Value);

            // The trailing fill only lags downward, which is what makes a level-up read as the
            // old bar draining away rather than the whole thing blinking back to empty.
            if (_trail < 0f || progress > _trail) _trail = progress;
            else _trail = Mathf.MoveTowards(_trail, progress, Time.deltaTime * TrailSpeed);

            Draw(_slowBar, length * Mathf.Max(_trail, progress));
            Draw(_fastBar, length * progress);
        }

        private static void Draw(RectTransform bar, float width)
        {
            if (bar == null) return;
            bar.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(0f, width));
        }

        /// <summary>
        /// Upright or flat.
        ///
        /// Vanilla builds its upright bars by rotating a horizontal one - GuiBar only ever
        /// resizes on RectTransform.Axis.Horizontal, so there is no other way to make one
        /// stand up. Undoing that rotation is all it takes to lay ours flat, and a flat bar is
        /// what length is useful on: stood on end, every pixel added runs further down the
        /// screen and off the bottom.
        ///
        /// Set in world terms rather than local, because the rotation may live on the donor's
        /// parent rather than on the donor - this way it does not matter which.
        /// </summary>
        private static void Lay()
        {
            _uprightAt = RistConfig.BarUpright.Value;
            if (_rect == null) return;

            _rect.rotation = _uprightAt ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.identity;
        }

        private static void Colour()
        {
            _tintedAt = RistConfig.BarColour.Value;
            var tint = RistConfig.BarTint();

            // Only the fill is tinted, so the borrowed frame and track keep their own colours.
            // The trailing bar is the same hue held back, which is how vanilla distinguishes
            // the pair.
            Tint(_fastFill, tint);
            Tint(_slowFill, new Color(tint.r * 0.55f, tint.g * 0.55f, tint.b * 0.55f, tint.a));

            if (_text != null) _text.color = tint;
        }

        /// <summary>
        /// Colour one bar's fill, without going through GuiBar.SetColor.
        ///
        /// SetColor writes m_barImage, and m_barImage is cached in Awake:
        ///
        ///     private void Awake() { m_barImage = m_bar.GetComponent&lt;Image&gt;(); ... }
        ///     public void SetColor(Color c) { if ((bool)m_barImage) m_barImage.color = c; }
        ///
        /// The bars are collected with GetComponentsInChildren(true), which reaches inactive
        /// ones on purpose - and the eitr bar sits hidden for a character with no eitr, so
        /// Awake has never run on its parts. m_barImage is null, SetColor returns having done
        /// nothing, and it does so silently. The fill then keeps the donor's own colour, which
        /// is the whole reason an experience bar cloned from the eitr bar came out purple no
        /// matter what BarColour said.
        ///
        /// m_bar is a public field, so its Image is reachable without waiting for Awake.
        /// SetColor is still called first: once Awake has run it is the same write, and doing
        /// both keeps this correct if the caching ever moves.
        /// </summary>
        private static void Tint(Image image, Color colour)
        {
            if (image == null) return;

            // Worth keeping behind Verbose: the first time this ran it printed
            // RGBA(1.000, 0.294, 0.939) - the donor's own magenta, showing through because the
            // tint was reaching nothing. A fill wearing a colour nobody chose is what this line
            // is for.
            if (RistConfig.Verbose.Value && image.color != colour)
                RistPlugin.Log.LogInfo("Bar fill '" + image.name + "' was " + image.color +
                                       " (sprite " + (image.sprite != null ? image.sprite.name : "none") +
                                       "); set to " + colour + ".");

            image.color = colour;
        }

        /// <summary>
        /// Placed by screen point rather than by copying the donor's anchoredPosition, which
        /// Hud rewrites every frame (0,130 normally, 0,285 with the build or ship HUD up) and
        /// is therefore never a stable thing to read. Going through
        /// ScreenPointToWorldPointInRectangle also sidesteps having to know whether the
        /// rotation that makes these bars upright sits on the bar or on its parent - the
        /// answer is scene data, and this works either way.
        /// </summary>
        private static void Place()
        {
            var parent = _rect.parent as RectTransform;
            if (parent == null) return;

            // Vanilla lifts its bars out of the way of the build panel. Ours is pinned in
            // screen space, so it has to make the same move by hand or it sits under the
            // piece selection.
            var raised = (Hud.instance.m_buildHud != null && Hud.instance.m_buildHud.activeSelf) ||
                         (Hud.instance.m_shipHudRoot != null && Hud.instance.m_shipHudRoot.activeSelf);

            var cam = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? _canvas.worldCamera
                : null;

            var scale = Scale;

            var point = new Vector2(RistConfig.BarPosX.Value * scale,
                                    (RistConfig.BarPosY.Value + (raised ? RistConfig.BarBuildRaise.Value : 0f)) * scale);

            // Following the stamina bar is worth more than two pixel numbers: "below the
            // stamina bar" is then true at every resolution and HUD scale rather than on the
            // machine the numbers were measured on, and it inherits vanilla's own shove upward
            // when the build panel opens instead of having to repeat it.
            if (RistConfig.BarFollowStamina.Value)
            {
                var anchor = Hud.instance.m_staminaBar2Root;
                if (anchor != null)
                {
                    // The anchor is already in real screen pixels, so only the offsets need
                    // scaling - they are the part written down in the config.
                    var onScreen = RectTransformUtility.WorldToScreenPoint(cam, anchor.position);
                    point = new Vector2(onScreen.x + RistConfig.BarOffsetX.Value * scale,
                                        onScreen.y - RistConfig.BarOffsetY.Value * scale);
                }
            }

            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, point, cam, out var world))
                _rect.position = world;
        }

        private static void Fail(string why)
        {
            _failed = true;
            RistPlugin.Log.LogWarning("Rist: " + why);
        }

        internal static void Drop()
        {
            if (_root != null) Object.Destroy(_root);

            _root = null;
            _rect = null;
            _fastBar = null;
            _slowBar = null;
            _fastFill = null;
            _slowFill = null;
            _group = null;
            _pulsing = false;
            _text = null;
            _animator = null;
            _canvas = null;
            _shownLevel = -1;
            _trail = -1f;   // A rebuilt bar starts level, not draining in from empty.
            _shownPercent = -1;
            _sizedAt = 0f;
            _tintedAt = null;

            // Forgiven on a world change rather than for the process. A failure costs one log
            // line per world, and the alternative is that a single bad frame during a load
            // leaves the bar plain until the game is restarted.
            _failed = false;
        }
    }
}
