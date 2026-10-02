using HarmonyLib;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Making the rist panel actually usable takes four patches, not one - the same set
    /// devkit needed, and for the same reasons.
    ///
    /// Blocking input stops the player swinging an axe while choosing. Tripping
    /// InInventoryEtc stops the camera swinging. Neither frees the mouse: GameCamera
    /// re-locks and hides the cursor every frame unless one of ten named vanilla interfaces
    /// is visible, and a modded window is in none of them. Without the fourth patch this is
    /// a window you can look at and cannot click.
    /// </summary>
    internal static class UiInput
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "TakeInput")]
        private static void BlockInput(ref bool __result)
        {
            // Composes with other mods doing the same: both return false, and false wins.
            if (RistPanel.IsOpen) __result = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        private static void BlockController(ref bool __result)
        {
            if (RistPanel.IsOpen) __result = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerController), "InInventoryEtc")]
        private static void HoldLookStill(ref bool __result)
        {
            if (RistPanel.IsOpen) __result = true;
        }

        /// <summary>
        /// A postfix rather than a prefix, because the method must still do its normal work
        /// for every other case - this only overrides the outcome while the window is up.
        /// </summary>
        /// <summary>
        /// Only written when the cursor is not already free. The compendium frees it by itself,
        /// so on most frames there is nothing to do, and an assignment that changes nothing on
        /// Windows is not harmless everywhere: a SteamOS player's pointer stuck to the middle of
        /// the screen on this page and no other, which is the one place that wrote both values
        /// sixty times a second. Reported 2026-10-01 (LHM-64) and not reproduced on Windows.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        private static void FreeCursor()
        {
            if (!RistPanel.IsOpen) return;

            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        /// <summary>
        /// Escape closes the window rather than opening the game's menu. An unspent pick
        /// stays owed, and the same three come back, so nothing is lost by walking away.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        private static bool EscapeCloses()
        {
            if (!RistPanel.IsOpen) return true;

            RistPanel.Close();
            return false;
        }
    }
}
