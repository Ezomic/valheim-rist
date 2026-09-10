using BepInEx.Configuration;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// Everything tunable, per the repo rule that a rejected number should be a config
    /// edit rather than a rebuild.
    ///
    /// Note the standing BepInEx trap: every entry is written to disk on first run and the
    /// saved value beats a new default in code. Changing a default here does nothing on a
    /// machine that has already run the plugin - edit
    /// BepInEx\config\ezomic.valheim.rist.cfg as part of the same change.
    /// </summary>
    internal static class RistConfig
    {
        internal static ConfigEntry<bool> Enabled;

        internal static ConfigEntry<float> XpPerSkillLevel;
        internal static ConfigEntry<float> LevelBaseXp;
        internal static ConfigEntry<float> LevelExponent;
        internal static ConfigEntry<string> SkillWeights;
        internal static ConfigEntry<float> DefaultSkillWeight;
        internal static ConfigEntry<string> WeightGeneration;

        internal static ConfigEntry<int> MaxRank;
        internal static ConfigEntry<int> BonusEvery;
        internal static ConfigEntry<float> ReconcileMaxLoss;
        internal static ConfigEntry<float> AttackSpeedMax;
        internal static ConfigEntry<int> PanelColumns;

        internal static ConfigEntry<bool> RemoveDeathSkillLoss;

        internal static ConfigEntry<bool> CheckSkillBaseline;
        internal static ConfigEntry<bool> CreditExistingSkills;
        internal static ConfigEntry<float> MaxSkillUpsPerMinute;
        internal static ConfigEntry<float> MaxSkillLevelJump;
        internal static ConfigEntry<float> MaxXpPerMinute;
        internal static ConfigEntry<float> XpBurst;
        internal static ConfigEntry<string> CappedMessage;


        internal static ConfigEntry<bool> Verbose;

        internal static ConfigEntry<bool> ShowInfoTab;
        internal static ConfigEntry<bool> ShowXpBar;
        internal static ConfigEntry<bool> VanillaBar;
        internal static ConfigEntry<bool> BarUpright;
        internal static ConfigEntry<bool> BarFollowStamina;
        internal static ConfigEntry<float> BarOffsetX;
        internal static ConfigEntry<float> BarOffsetY;
        internal static ConfigEntry<float> BarPosX;
        internal static ConfigEntry<float> BarPosY;
        internal static ConfigEntry<float> BarSize;
        internal static ConfigEntry<float> BarBuildRaise;
        internal static ConfigEntry<string> BarColour;
        internal static ConfigEntry<float> BarFlashSeconds;
        internal static ConfigEntry<float> BarNoteGap;
        internal static ConfigEntry<float> BarX;
        internal static ConfigEntry<float> BarBottom;
        internal static ConfigEntry<float> BarThickness;
        internal static ConfigEntry<float> BarLength;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true,
                "Off leaves levels and cards recorded but stops granting or applying them.");

            Verbose = cfg.Bind("General", "Verbose", false,
                "Log every XP grant, every rejected report and every runestone applied.\n" +
                "Also dumps what the game's own UI is made of where this mod clones it - the " +
                "compendium tab's layers and button colours, and the bar's fill colour and " +
                "length. Both were written while chasing a cloned widget that drew the wrong " +
                "thing, and they are the fastest way back to an answer if one ever does again.");

            // The character level is its own number with its own curve. It is fed by skill
            // level-ups but is deliberately not a restatement of total skill level - the
            // weighting below is what makes it a separate track rather than a second view
            // of the skill list.
            XpPerSkillLevel = cfg.Bind("Levelling", "XpPerSkillLevel", 1f,
                "XP granted per skill level-up, multiplied by the skill level reached. " +
                "Weighting by level reached is deliberate: a flat rate would stall late " +
                "progression exactly when the deep card ranks matter, and would make " +
                "grinding a fresh cheap skill from zero the fastest way to farm cards.");

            LevelBaseXp = cfg.Bind("Levelling", "LevelBaseXp", 40f,
                "Cumulative XP needed for character level 1. Later levels scale by LevelExponent.\n" +
                "Together with LevelExponent this is the whole pacing of the mod. The pair was " +
                "60 and 1.5 until it was played, and that asked for sixteen skill level-ups per " +
                "rist by character level 20 - the reward simply stopped arriving. 40 and 1.4 " +
                "roughly halves that at every level.");

            LevelExponent = cfg.Bind("Levelling", "LevelExponent", 1.4f,
                "Cumulative XP for level N is LevelBaseXp * N^LevelExponent. Above 1 means " +
                "each level costs more than the last. Vanilla's own skill curve is already " +
                "front-loaded, so this counteracts a flood of early cards.\n" +
                "This is the shape rather than the scale: lowering it helps the late game far " +
                "more than the early one, which is where the old 1.5 hurt. It is also what " +
                "keeps the catalogue scarce - there are 95 picks in it at MaxRank 5, and a " +
                "curve flat enough to hand out all of them turns a free choice into an order " +
                "of purchase. At 1.4 a long playthrough lands near 67 of the 95.");

            // The whole table, not only the reductions. This is the tuning surface for the
            // mod's pacing and it should be possible to see every skill and its price in one
            // place without knowing which ones were left out.
            SkillWeights = cfg.Bind("Levelling", "SkillWeights",
                "Swords=1, Knives=1, Clubs=1, Polearms=1, Spears=1, Axes=1, Bows=1, " +
                "Crossbows=1, Unarmed=1, Blocking=1, ElementalMagic=1, BloodMagic=1, " +
                "Dodge=0.5, Sneak=0.5, Swim=0.5, " +
                "WoodCutting=0.25, Pickaxes=0.25, Crafting=0.25, Farming=0.25, " +
                "Cooking=0.25, Fishing=0.25, Run=0.25, Jump=0.25, Ride=0.25",
                "What each skill is worth toward the character level, as Skill=multiplier " +
                "pairs. A skill not named here is worth DefaultSkillWeight.\n" +
                "The principle is risk: full price for a skill raised by something that can " +
                "kill you, a fraction for one raised by repetition that cannot. This is not a " +
                "guess at what felt wrong - the live ledger said it. A character at level 12 " +
                "held 1346 xp of which WoodCutting alone was 666, Run, Jump and Crafting " +
                "another 361, and everything earned in a fight came to 293. The level was " +
                "mostly a record of trees felled in the Meadows. Under this table that same " +
                "character is level 6 and over half of it came from fighting.\n" +
                "Raising LevelBaseXp or LevelExponent cannot do this. Both scale the curve " +
                "uniformly, so the ratio survives and the late game pays for it - which is " +
                "what happened at 60 and 1.5 and had to be walked back.\n" +
                "Names are the Skills.SkillType names and are case-insensitive; a raw type " +
                "number also works, for a skill this game version has no name for. Negative " +
                "weights are clamped to zero. Changing this affects new xp only until you " +
                "also bump WeightGeneration.");

            DefaultSkillWeight = cfg.Bind("Levelling", "DefaultSkillWeight", 1f,
                "What a skill not named in SkillWeights is worth. One, so that a skill added " +
                "by a game update or another mod is paid normally rather than silently going " +
                "unpaid - an unnamed skill is far more likely to be an oversight than a " +
                "deliberate zero.");

            WeightGeneration = cfg.Bind("Levelling", "WeightGeneration", "1",
                "Bump this - to any different text - to recompute every player's xp from the " +
                "skill levels this server has watched them reach, under the weights standing " +
                "now. Each character is recomputed once per value, on its next login, and the " +
                "value it was last done at is written into the ledger line.\n" +
                "This is the only thing that may move xp *downward*, and it is why it is a " +
                "stamp rather than a switch. The routine per-login credit is deliberately " +
                "one-way: it must never take levels away, or a skill lost to a death penalty " +
                "would cost a card. A recompute has to be able to, so it is made a one-shot " +
                "with a record of having run rather than something that fires every login.\n" +
                "Cards already taken are kept. Someone recomputed from level 12 to level 6 " +
                "keeps all twelve picks and earns no new ones until they pass twelve again, " +
                "which is the honest reading - the picks were spent, and handing them back " +
                "would be a free rebuild of the whole character rather than a re-pricing.");

            // A thing on screen rather than a key, which is the standing preference here,
            // and now the only way in - the keybind is gone rather than merely unbound.
            ShowInfoTab = cfg.Bind("General", "ShowInfoTab", true,
                "Add a fifth tab to the compendium bar, beside the raven and the trophy, that " +
                "opens your rists. Cloned from a tab already there, so it carries the game's " +
                "own frame, hover and click sound.");


            ShowXpBar = cfg.Bind("Bar", "ShowXpBar", true,
                "Show the experience bar beside the health bar. It hides itself with the rest " +
                "of the interface, including when the HUD is hidden by hand.");

            // On by default, because a hand-drawn bar never matched: vanilla bars carry a
            // frame, a bevelled track, softened ends and a trailing second fill, and none of
            // that survives being approximated with a flat rectangle.
            VanillaBar = cfg.Bind("Bar", "VanillaBar", true,
                "Build the bar by cloning one of the game's own upright bars, so it carries " +
                "the same frame, fill sprite and trailing fill as stamina and eitr.\n" +
                "Off draws a plain rectangle instead. That is also the automatic fallback if " +
                "the clone fails, since the HUD hierarchy is scene data rather than API and " +
                "can change under a game update.");

            // The donor is a rotated bar - GuiBar only ever resizes on the horizontal axis, so
            // vanilla builds its upright bars by turning a horizontal one on its side. Laying
            // ours flat again is one line, and it is what a long bar wants: upright, length
            // runs down the screen and off the bottom.
            BarUpright = cfg.Bind("Bar", "BarUpright", false,
                "Stand the bar on end like stamina and eitr. Off lays it flat, which is what a " +
                "long bar needs - upright, extra length runs off the bottom of the screen.");

            // Anchored to the stamina bar rather than to the screen, so "below the stamina
            // bar" stays true at any resolution and HUD scale instead of being two numbers
            // that happen to be right on one machine. It also follows vanilla's own shove
            // upward when the build panel opens, which BarBuildRaise otherwise has to
            // duplicate by hand.
            BarFollowStamina = cfg.Bind("Bar", "BarFollowStamina", true,
                "Place the bar relative to the stamina bar instead of at fixed screen pixels. " +
                "Off falls back to BarPosX and BarPosY, which is also what happens if the " +
                "stamina bar cannot be found.");

            BarOffsetX = cfg.Bind("Bar", "BarOffsetX", 0f,
                "Pixels right of the stamina bar's centre, when following it.");

            BarOffsetY = cfg.Bind("Bar", "BarOffsetY", 70f,
                "Pixels below the stamina bar's centre, when following it.");

            // Screen pixels, not the donor's anchoredPosition: Hud rewrites that every frame
            // (0,130 normally, 0,285 with the build HUD up), so it is never a stable thing to
            // copy. The defaults put the bar where the hand-drawn one was tuned to sit.
            BarPosX = cfg.Bind("Bar", "BarPosX", 172f,
                "Pixels from the left edge of the screen to the centre of the cloned bar.");

            BarPosY = cfg.Bind("Bar", "BarPosY", 105f,
                "Pixels from the bottom of the screen to the centre of the cloned bar. Depends " +
                "on your resolution and HUD scale, so it will probably need nudging once.");

            // Vanilla sizes these bars from max stamina or max eitr, which means nothing for a
            // bar that is always 0..1. 64 is what a starting stamina bar measures (50/25*32).
            BarSize = cfg.Bind("Bar", "BarSize", 240f,
                "Length of the cloned bar in canvas units, the same units vanilla sizes the " +
                "stamina and eitr bars in. 64 is what a starting stamina bar measures; 240 is a " +
                "bar you can read a percentage off at a glance. Thickness comes " +
                "from the borrowed sprite and is not settable.");

            BarBuildRaise = cfg.Bind("Bar", "BarBuildRaise", 155f,
                "Pixels to lift the cloned bar while the build or ship panel is open. Vanilla " +
                "moves its own bars up by the same amount to clear that panel; ours is pinned " +
                "in screen space, so it has to make the move by hand.");

            BarColour = cfg.Bind("Bar", "BarColour", "4FB3A5",
                "Bar colour as RRGGBB hex. The trailing fill is the same hue held back, the " +
                "way vanilla tells its bar pairs apart. Unparseable values fall back to gold.\n" +
                "Verdigris - aged bronze. Picked from a five-way comparison against the bars " +
                "already on the HUD, which spend red, yellow, blue and orange between them, so " +
                "a fifth in any of those reads as one of them.\n" +
                "It is cool like eitr but far enough round the wheel never to be taken for it, " +
                "and it suits a Norse metal palette in a way the green it replaced did not: a " +
                "green bar reads as health or poison from habit, whatever it is measuring.\n" +
                "Bone is E4DCC4 if you would rather have pale stone. That was the original " +
                "choice and it was never actually seen - for a long time no value here reached " +
                "the fill at all, and the bar simply showed the eitr donor's own purple.");

            BarFlashSeconds = cfg.Bind("Bar", "BarFlashSeconds", 4f,
                "How often the bar pulses while a pick is waiting to be spent, in seconds. " +
                "Only applies to the cloned bar.");

            BarNoteGap = cfg.Bind("Bar", "BarNoteGap", 6f,
                "Pixels between the top of the bar and the 'rist waiting' note above it. " +
                "Measured from the bar's own edge rather than from a fixed screen position, " +
                "so it holds at any resolution and HUD scale and follows the bar when the " +
                "build panel shoves it upward.");

            // Pixels rather than an anchor to the real health bar: converting a scaled Canvas
            // RectTransform into IMGUI screen space breaks differently at every HUD scale, and
            // two numbers anyone can nudge are easier to correct than a formula that is subtly
            // wrong. These four place the fallback bar only; the cloned one uses BarPos*.
            BarX = cfg.Bind("Bar", "BarX", 168f,
                "Pixels from the left edge of the screen to the left edge of the bar. The " +
                "default aims to sit just right of the health bar.");

            // Measured from the bottom, because that is the corner it belongs in. Anchoring to
            // the top would move it every time the resolution changed.
            BarBottom = cfg.Bind("Bar", "BarBottom", 75f,
                "Pixels from the bottom of the screen to the bottom of the bar. Depends on " +
                "your resolution and HUD scale, so it will probably need nudging once.");

            // Thickness and length rather than width and height, because the bar is upright:
            // reusing the old names would have let the horizontal values already written to
            // the cfg carry over as a very wide, very short vertical bar.
            BarThickness = cfg.Bind("Bar", "BarThickness", 10f, "Bar thickness in pixels.");
            BarLength = cfg.Bind("Bar", "BarLength", 60f, "Bar height in pixels.");

            // A capstone every fifth rank rather than only at the last one, so raising MaxRank
            // to 10 gives two of them rather than moving the single one further away.
            BonusEvery = cfg.Bind("Cards", "BonusEvery", 5,
                "Ranks between capstones. A card that names a bonus effect in its last two " +
                "cards.txt fields grants it once per this many ranks. At the default MaxRank " +
                "of 5 that means once, on the final upgrade.");

            // A floor under the one operation that can delete progress. Removing a card from
            // cards.txt hands its picks back and drops the ranks bought in it, which is right
            // when it was meant and is a permanent server-side wipe when the catalogue simply
            // failed to load - and from inside the ledger the two look identical.
            ReconcileMaxLoss = cfg.Bind("Cards", "ReconcileMaxLoss", 0.34f,
                "How much of the ledger's card history may vanish from cards.txt in one go " +
                "before Rist refuses to reconcile at all, as a fraction of the distinct card " +
                "ids players actually hold.\n" +
                "A third, because a pack or two being retired is a normal edit and losing " +
                "half of what everyone is carrying is not. Over this, nothing is returned and " +
                "nothing is removed: the server logs why on every login and leaves every " +
                "record untouched until you either fix the catalogue or raise this and " +
                "restart. Stranded picks are recoverable, a deleted history is not.\n" +
                "Judged once, when the ledger is read. A catalogue that is missing, unreadable " +
                "or empty is refused whatever this says - there is no edit that legitimately " +
                "empties it, so 1 does not disable that half.\n" +
                "Server-side only; the ledger exists nowhere else.");

            AttackSpeedMax = cfg.Bind("Cards", "AttackSpeedMax", 1f,
                "Ceiling on the attack-speed cards, as a fraction. 1 means the animation can " +
                "at most run at double speed.\n" +
                "This multiplies an animation rather than a number in a table, and animation " +
                "events are what land the hit, so a mis-typed catalogue line could otherwise " +
                "run the whole character at twenty times speed.");

            // Twenty-four cards no longer fit three across without scrolling, and both of
            // these are the kind of number that wants nudging rather than rebuilding.

            PanelColumns = cfg.Bind("Cards", "PanelColumns", 4,
                "Tiles across the rists panel. Fewer means wider tiles and a taller panel; the " +
                "panel scrolls vertically once it would pass 88% of the screen height.");

            MaxRank = cfg.Bind("Cards", "MaxRank", 5,
                "How deep a single card can be taken. A card at this rank stops being " +
                "pickable, and is also how many slots its track shows.");


            // ProtectCharacter used to be bound here. It refused to start a local world with a
            // character belonging to another one, which is protection against being locked out
            // by a door Rist no longer owns - so it moved to Dyrr along with the door,
            // and the bindings moved from rist-home.txt to dyrr-home.txt. The old key may
            // still be sitting in existing config files doing nothing.

            RemoveDeathSkillLoss = cfg.Bind("Death", "RemoveDeathSkillLoss", true,
                "Skip Skills.OnDeath entirely. The vanilla world modifier is not enough on " +
                "its own: the accumulator wipe and the 'skills lowered' message sit outside " +
                "the reduction factor, so zeroing it still discards partial progress toward " +
                "the next level in every skill.");

            // The anti-cheat is a question about XP, not about admission. Rist used to refuse
            // the connection outright, which meant a levelling mod decided who could play -
            // and when it did, the player got Valheim's generic kick screen with the reason
            // only in the server's log. The "has this character been to other worlds" half of
            // that judgement now lives in Dyrr, where turning people away is the whole
            // job and is done openly. What is left here is strictly about payment.
            CheckSkillBaseline = cfg.Bind("Gate", "CheckSkillBaseline", true,
                "On login, take the character's skill list as the baseline this server pays " +
                "from. Nothing is compared and nobody is judged: a character that turns up " +
                "higher than this world last saw it gained that elsewhere and is simply not " +
                "paid for it, because XP only ever comes from a level-up watched from here.\n" +
                "Off leaves the baseline to fill in from observed play alone, which also turns " +
                "off MaxSkillLevelJump - without a complete list, a skill never used cannot be " +
                "told from one never seen.\n" +
                "This replaces WithholdUntrustedXp, UntrustedMessage and SkillDriftAllowance, " +
                "which marked such a character untrusted and paid it nothing 'until they line " +
                "up again'. They never could: the withholding stopped the baseline advancing, " +
                "and the baseline was the only thing that could have cleared it. Those keys may " +
                "still be sitting in your config file doing nothing and can be deleted. Keeping " +
                "a character out of a world altogether is a door policy - that is Dyrr.");

            CreditExistingSkills = cfg.Bind("Gate", "CreditExistingSkills", true,
                "Pay a joining character for the skills it already has, so a character that " +
                "turns up worth twenty levels arrives at twenty levels with the picks to " +
                "spend. Off starts every character at Rist level 0 no matter what it is " +
                "carrying, and only level-ups watched from here earn anything.\n" +
                "Not an estimate: XP is the skill level reached, so a skill at N has already " +
                "produced 1+2+...+N. Summing that over every skill is exactly what the " +
                "character would hold if all of it had happened here - which also makes it " +
                "safe to re-run on every login, since a character that earned everything here " +
                "is owed nothing and a skill lost to death cannot take levels away.\n" +
                "On by default because the level is meant to sit beside the skills; without " +
                "this it records which server you were standing on instead. If you would " +
                "rather a well-travelled character could not join at all, that is a door " +
                "policy rather than a payment one - see Dyrr.");

            MaxSkillUpsPerMinute = cfg.Bind("Gate", "MaxSkillUpsPerMinute", 30f,
                "Server-side ceiling on accepted skill-up reports per player. A backstop " +
                "only: skills live on the client, so reports are self-reported and this " +
                "caps the damage rather than verifying anything.");

            // MaxSkillUpsPerMinute caps how many reports are accepted, which is a different
            // question from what a report is worth. XP is the level reached, so thirty reports
            // a minute each claiming a level-100 skill used to be worth 3000 XP - a character
            // level every ten seconds. These two bound the worth rather than the count.
            MaxSkillLevelJump = cfg.Bind("Gate", "MaxSkillLevelJump", 1f,
                "How far above the highest level this server has watched a skill reach a " +
                "single skill-up report may claim.\n" +
                "One, because that is what the game does: skills level one at a time and fire " +
                "one callback each, so a report of 87 from a server that last saw 12 did not " +
                "come from playing. Raise it only if you run a mod that grants skill levels " +
                "in bulk, which would otherwise trip this on every grant.\n" +
                "Needs CheckSkillBaseline on, since without a login baseline an unseen skill " +
                "cannot be told from an unused one and every first report would look forged.");

            MaxXpPerMinute = cfg.Bind("Gate", "MaxXpPerMinute", 600f,
                "Ceiling on XP paid per player per minute of connected time. Zero or less " +
                "turns it off.\n" +
                "This is the only cap that does not depend on believing the client: however " +
                "convincing a report is, time connected is a quantity the server measures for " +
                "itself. 600 is far above honest play - it is thirty level-ups a minute, the " +
                "report limit, on a skill at level 20 - and is meant to bound a flood rather " +
                "than to shape progression. Use LevelBaseXp for that.");

            XpBurst = cfg.Bind("Gate", "XpBurst", 1800f,
                "How much unspent earning allowance may bank, in XP. Three minutes' worth by " +
                "default, and the allowance starts full so joining is never a wait.\n" +
                "Without a bank the cap would be a per-minute tripwire that an honest burst of " +
                "level-ups trips - clearing a crypt levels four skills at once - and the " +
                "player would silently stop earning mid-fight.");

            CappedMessage = cfg.Bind("Gate", "CappedMessage",
                "You are earning faster than this world will pay for. Rists will resume shortly.",
                "Shown once per session, centre-screen, when the earning cap withholds XP. " +
                "A cap that simply stops paying is invisible, so it says so once: " +
                "invisible, and an honest player who hits one deserves to know why.");
        }

        private static readonly Color Gold = new Color(0.83f, 0.663f, 0.29f, 1f);
        private static string _tintFrom;
        private static Color _tint = Gold;

        /// <summary>
        /// BarColour parsed, cached against the string it came from so the bar can re-read it
        /// every frame without parsing every frame. A bad value keeps the gold rather than
        /// throwing, the way an unknown card effect is skipped rather than fatal.
        /// </summary>
        internal static Color BarTint()
        {
            var text = BarColour != null ? BarColour.Value : null;
            if (_tintFrom == text) return _tint;

            _tintFrom = text;
            _tint = Gold;

            if (string.IsNullOrEmpty(text)) return _tint;
            if (text[0] != '#') text = "#" + text;

            if (ColorUtility.TryParseHtmlString(text, out var parsed)) _tint = parsed;
            return _tint;
        }

    }
}
