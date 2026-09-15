using TMPro;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The game's own typefaces, borrowed.
    ///
    /// This was once a whole borrowed skin - wood panel, slot, selection frame, separator,
    /// bar track - baked out of the game's atlases so an IMGUI window could pretend to be one
    /// of Valheim's. It never convinced anyone: IMGUI cannot draw with the game's shaders, so
    /// every copied sprite had to survive a colour space round trip that was guessed wrong
    /// three times, and even correct it read as an imitation of a window rather than a window.
    ///
    /// The panel is runestones now, which imitate nothing, and all that borrowing went with
    /// it. What is left is the part that always worked: the fonts. Nothing is shipped - the
    /// fonts are the game's own, loaded from its Resources, because a font merely found in
    /// memory can be unloaded out from under the panel. See Ensure.
    /// </summary>
    internal static class Skin
    {
        /// <summary>Body text. AveriaSerifLibre is what the game sets nearly everything in.</summary>
        internal static Font Face;

        /// <summary>Headings and names, in the same family a weight heavier.</summary>
        internal static Font HeadFace;

        /// <summary>
        /// The rune face, which is what makes a carved mark free: an inscription is text.
        ///
        /// It is Latin-mapped - type F, get the rune - so the marks and sigils are written as
        /// Latin letters. Runic code points come out of it as empty boxes, which is a whole
        /// screen of tofu squares learned the hard way.
        /// </summary>
        internal static Font RuneFace;

        private static bool _tried;

        private static readonly string[] FaceNames = { "AveriaSerifLibre-Regular", "AveriaSerifLibre-Light" };
        private static readonly string[] HeadFaceNames = { "AveriaSerifLibre-Bold", "Norsebold", "Norse" };
        private static readonly string[] RuneFaceNames = { "rune", "Norsebold", "Norse" };

        // The fonts' Resources paths. Valheim's fonts sit under a TextMesh Pro "Resources" folder,
        // so Unity built a second, permanent copy of each into resources.assets beside the one in
        // the soft-reference bundles - the resource list in globalgamemanagers names all three.
        // That copy belongs to no bundle and is never unloaded while it is referenced.
        private const string FacePath = "Fonts/Averia_Serif_Libre/AveriaSerifLibre-Regular";
        private const string HeadFacePath = "Fonts/Averia_Serif_Libre/AveriaSerifLibre-Bold";
        private const string RuneFacePath = "Fonts/Rune/rune";

        private const string RuneName = "rune";

        // How often a panel stuck on a stand-in rune face looks for the real one again.
        private const float RetrySeconds = 5f;
        private static float _nextRetry;

        /// <summary>
        /// True when the panel's fonts need finding again: a font it was built with has since been
        /// destroyed, or the rune face is a stand-in and it is time to look for the real one.
        ///
        /// Destroyed is the reported bug. A Font from an unloaded bundle compares equal to null
        /// while the C# reference is still set, and IMGUI silently swaps in its default font, so
        /// every sigil and rim mark came out as the Latin letter it is written as. A font that
        /// was never found at all is not counted as destroyed, so a machine without one does not
        /// rebuild the panel every frame; the stand-in retry is rate-limited for the same reason.
        /// </summary>
        internal static bool Lost
        {
            get
            {
                if (Destroyed(Face) || Destroyed(HeadFace) || Destroyed(RuneFace)) return true;
                if (RuneFace != null && RuneFace.name == RuneName) return false;
                if (Time.realtimeSinceStartup < _nextRetry) return false;

                _nextRetry = Time.realtimeSinceStartup + RetrySeconds;
                return true;
            }
        }

        private static bool Destroyed(Font font)
        {
            return !ReferenceEquals(font, null) && font == null;
        }

        /// <summary>Forget the fonts so the next Ensure finds them again.</summary>
        internal static void Reset()
        {
            _tried = false;
            Face = HeadFace = RuneFace = null;
        }

        internal static void Ensure()
        {
            if (_tried) return;
            _tried = true;

            // Resources first, whatever is lying in memory second.
            //
            // The bug this ordering fixes: rune.ttf's bundle copy lives in bundle d59cfac, which
            // main.unity depends on and start.unity does not. Logging out to the menu swaps the
            // scenes, main.unity's reference count reaches zero, and every bundle only it held is
            // unloaded with unloadAllLoadedObjects - destroying the rune Font. The Averia fonts'
            // bundle is held by both scenes, which is why only the runes broke. Rist found its
            // fonts once per process by name, so after one logout the panel drew plain letters
            // until the game was restarted - "sometimes", from the player's side.
            //
            // Holding the bundle copy through SoftReferenceableAssets was tried first and dropped:
            // it only resolves when some mod has called MakeAllAssetsLoadable, and it would keep a
            // 106 MB bundle of locations and UI loaded at the main menu. The Resources copy costs
            // nothing and needs no other mod.
            //
            // Plain null tests, never ??: a destroyed Font is Unity-null but not C#-null.
            Face = Load(FacePath);
            if (Face == null) Face = FindFace(FaceNames);

            HeadFace = Load(HeadFacePath);
            if (HeadFace == null) HeadFace = FindFace(HeadFaceNames);
            if (HeadFace == null) HeadFace = Face;

            RuneFace = Load(RuneFacePath);
            var route = RuneFace != null ? "resources" : "found in memory";
            if (RuneFace == null) RuneFace = FindFace(RuneFaceNames);
            if (RuneFace == null) RuneFace = HeadFace;
            if (RuneFace == null || RuneFace.name != RuneName) route = "stand-in, will retry";

            RistPlugin.Log.LogInfo("Fonts: body=" + Name(Face) + ", heading=" + Name(HeadFace) +
                                   ", rune=" + Name(RuneFace) + " (" + route + ").");

            // Every Font and TMP font asset the game has loaded, once, behind Verbose. Kept for
            // the next time the runes come out as letters: it separates "the rune face was not
            // applied" from "the face applied is not the runic one", and it counts duplicates,
            // since every font exists twice - the Resources copy and the bundle copy.
            //
            // An earlier note here blamed a stale dynamic-font atlas. That does not hold on
            // Unity 6, where IMGUI draws through one TextCore font asset per Font instance; the
            // plain letters were the destroyed Font above, replaced by IMGUI's default.
            if (!RistConfig.Verbose.Value) return;

            var seen = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font == null || string.IsNullOrEmpty(font.name)) continue;
                seen.TryGetValue(font.name, out var n);
                seen[font.name] = n + 1;
            }

            var names = new System.Collections.Generic.List<string>();
            foreach (var kv in seen) names.Add(kv.Value > 1 ? kv.Key + " x" + kv.Value : kv.Key);
            names.Sort(System.StringComparer.Ordinal);
            RistPlugin.Log.LogInfo("  Font objects available (" + names.Count + " names): " +
                                   string.Join(", ", names.ToArray()));

            var tmp = new System.Collections.Generic.List<string>();
            foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (asset == null || string.IsNullOrEmpty(asset.name)) continue;
                if (!tmp.Contains(asset.name)) tmp.Add(asset.name);
            }

            tmp.Sort(System.StringComparer.Ordinal);
            RistPlugin.Log.LogInfo("  TMP font assets (" + tmp.Count + "): " +
                                   string.Join(", ", tmp.ToArray()));
        }

        private static Font Load(string path)
        {
            try
            {
                return Resources.Load<Font>(path);
            }
            catch (System.Exception e)
            {
                RistPlugin.Log.LogWarning("Could not load the font at Resources/" + path + " (" + e.Message + ").");
                return null;
            }
        }

        private static string Name(Font font)
        {
            return font != null ? font.name : "default";
        }

        /// <summary>
        /// A real Font, not a TMP_FontAsset - IMGUI cannot use the latter. The game ships both,
        /// which is the only reason any of this works without shipping a font file.
        /// </summary>
        private static Font FindFace(string[] preferred)
        {
            var all = Resources.FindObjectsOfTypeAll<Font>();

            foreach (var name in preferred)
            {
                foreach (var font in all)
                {
                    if (font != null && font.name == name) return font;
                }
            }

            // Anything that is not the default is still closer than the default.
            foreach (var font in all)
            {
                if (font == null || font.name == null) continue;
                if (font.name.StartsWith("Arial") || font.name.StartsWith("Liberation")) continue;
                return font;
            }

            return null;
        }
    }
}
