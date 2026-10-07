using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Far sight's capstone: hold a key and the view zooms far ahead, like binoculars.
    ///
    /// "The land gives up its shape" to the eye as well as to the map. It replaces Lookahead (a second
    /// circle of map ahead of the way you travel), whose code is gone. The zoom is the camera's field of
    /// view eased down to a quarter of its normal value (SpyglassZoom, 0.25) while the key is held, and eased
    /// back when it is let go. The key is SpyglassKey, Z by default: the vanilla bindings use W A S D E R F G
    /// T V X C Q M, Tab, Space, Ctrl, Shift, Enter and F5, the Devkit audit lists F6 and F10 and the other mods
    /// here use B, H, J, Alt, Shift and the keypad, so Z clashes with nothing. It is a KeyCode, which Core
    /// never imposes, and it is declared Local anyway so a host cannot override a player's own key.
    ///
    /// The game sets the camera's field of view from GameCamera.m_fov at the top of UpdateCamera every frame,
    /// and other things move m_fov (the grappling hook ramps it as a temporary FOV, other mods set it). So
    /// this does not touch m_fov: a postfix on UpdateCamera multiplies whatever the game just wrote to the
    /// cameras by an eased factor of its own, which composes with all of them and leaves nothing to restore.
    /// While the factor is 1 the camera is not written at all.
    ///
    /// When it works. The key is read the way the game reads one (ZInput, no log warning) and refused while
    /// chat, the console or a text input has focus, and while any window is up: the inventory, the menu, the
    /// map, a store, the build piece menu or the radial, a popup, or Rist's own panel. It is off while a piece is
    /// in hand to place (build mode), in a cutscene, in free-fly and when dead. In ordinary play, first or
    /// third person, on foot, on a ship or on a mount, it works. Letting go inside a window eases the zoom
    /// back as usual.
    ///
    /// Turning. The mouse look is scaled by the same factor (SpyglassSlowTurn), because a quarter of the field
    /// of view at the normal sensitivity swings four times as far on the screen, and a binocular that
    /// whips across the horizon cannot be aimed. The scale is the eased factor, so it comes and goes with the
    /// zoom. Only the local player.
    /// </summary>
    internal static class Spyglass
    {
        internal const string Key = "*sight:spyglass";

        private const float Ease = 10f;

        private static float _factor = 1f;
        private static bool _forced;
        private static bool _held;

        private static AccessTools.FieldRef<GameCamera, Camera> _camera;
        private static bool _bound, _bindFailed;

        private static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;

            try
            {
                _camera = AccessTools.FieldRefAccess<GameCamera, Camera>("m_camera");
                _bound = true;
            }
            catch (System.Exception e)
            {
                _bindFailed = true;
                RistPlugin.Log.LogError("Spyglass could not reach the game's camera and is off for this session: " + e.Message);
            }

            return _bound;
        }

        private static float Zoom => Mathf.Clamp(RistConfig.SpyglassZoom.Value, 0.05f, 1f);

        private static bool Carved => RistConfig.Enabled.Value && Effects.Cached(Key) > 0f;

        private static bool WindowOpen()
        {
            if (RistPanel.IsOpen || Console.IsVisible() || TextInput.IsVisible()) return true;
            if (Chat.instance != null && Chat.instance.HasFocus()) return true;

            return InventoryGui.IsVisible() || Menu.IsVisible() || Minimap.IsOpen() || StoreGui.IsVisible()
                   || Hud.IsPieceSelectionVisible() || Hud.InRadial() || UnifiedPopup.IsVisible();
        }

        private static bool Wanted()
        {
            var player = Player.m_localPlayer;
            if (player == null || !Carved || GameCamera.InFreeFly()) return false;
            if (player.IsDead() || player.InCutscene() || player.InPlaceMode() || player.IsTeleporting()) return false;
            if (WindowOpen()) return false;

            var key = RistConfig.SpyglassKey.Value;
            _held = _forced || (key != KeyCode.None && ZInput.GetKey(key, false));
            return _held;
        }

        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        internal static class Look
        {
            [HarmonyPostfix]
            private static void Zoomed(GameCamera __instance, float dt)
            {
                _held = false;
                var target = Wanted() ? Zoom : 1f;

                var step = 1f - Mathf.Exp(-Ease * Mathf.Max(dt, 0.0001f));
                _factor = Mathf.Lerp(_factor, target, step);
                if (Mathf.Abs(_factor - target) < 0.002f) _factor = target;

                if (_factor >= 1f || GameCamera.InFreeFly() || !Bind()) return;

                var camera = _camera(__instance);
                if (camera == null) return;

                camera.fieldOfView *= _factor;
                if (__instance.m_skyCamera != null) __instance.m_skyCamera.fieldOfView = camera.fieldOfView;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SetMouseLook))]
        internal static class Turn
        {
            [HarmonyPrefix]
            private static void Slower(Player __instance, ref Vector2 mouseLook)
            {
                if (_factor >= 1f || !RistConfig.SpyglassSlowTurn.Value) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer)) return;

                mouseLook *= _factor;
            }
        }

        internal static void Forget()
        {
            _factor = 1f;
            _forced = false;
        }

        /// <summary>Holds the spyglass as the key does, for `rist spyglass on|off`.</summary>
        internal static string Hold(bool on)
        {
            if (!on || Carved) _forced = on;
            return Carved
                ? "rist: spyglass " + (on ? "held" : "let go") + ", give the zoom a second."
                : "rist: Far sight is not carved, so there is no spyglass to hold.";
        }

        internal static string Probe()
        {
            if (!Carved) return "spyglass: not carved";

            var normal = GameCamera.instance != null ? GameCamera.instance.m_fov : 0f;
            var live = Camera.main != null ? Camera.main.fieldOfView : 0f;

            return "spyglass: carved, key " + RistConfig.SpyglassKey.Value + ", held " + (_held ? "yes" : "no")
                   + ", zoom x" + _factor.ToString("0.00", CultureInfo.InvariantCulture)
                   + " of a target x" + Zoom.ToString("0.00", CultureInfo.InvariantCulture)
                   + ", target fov " + (normal * Zoom).ToString("0.0", CultureInfo.InvariantCulture)
                   + " of " + normal.ToString("0.0", CultureInfo.InvariantCulture)
                   + ", camera fov " + live.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
