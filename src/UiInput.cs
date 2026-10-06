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
        /// Takes over from the game while the page is up, instead of fixing up after it.
        ///
        /// The page hides the inventory, and the inventory is what kept the game's capture
        /// check from firing: with it gone, the original locks and hides the cursor every
        /// frame. Rist used to undo that in a postfix, so the cursor was locked and released
        /// again sixty times a second. On Windows that is invisible. On Linux a lock puts the
        /// pointer back in the middle of the window, so it was re-centred every frame and never
        /// got anywhere: stuck to the middle on this page and no other (LHM-64). The 1.7.1 fix
        /// wrote the cursor only when it differed, which changed nothing, because the game's
        /// own write was what made it differ.
        ///
        /// So the original does not run. This is the branch it takes for any of the game's own
        /// windows, the one that frees the cursor, written only when it changes anything.
        /// ZCursor.Show rather than Cursor.visible, so the OS pointer stays hidden while a
        /// gamepad is the active device and the page draws its own.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        private static bool FreeCursor()
        {
            if (!RistPanel.IsOpen) return true;

            if (Cursor.lockState != CursorLockMode.None) ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();
            return false;
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
