using System.Collections.Generic;
using UnityEngine;

namespace Rist
{
    /// <summary>
    /// The rist panel: a full screen of runestones, one per card, each carved one more mark
    /// per rank taken.
    ///
    /// Three designs came before this. A draft window that dealt three cards at random; a
    /// board of tiles; then the same board dressed in the game's own wooden sprites. The last
    /// one is why this exists - borrowing a window frame is imitation, and it read as a
    /// different game no matter how close the sprites got, because IMGUI cannot draw with the
    /// game's shaders and every copied sprite had to survive a colour-space round trip that
    /// was guessed wrong three times.
    ///
    /// A runestone imitates nothing. It is Valheim's own subject matter, it is a shape rather
    /// than a material, and the marks cut into it are text in the game's own rune face - so
    /// the whole panel is one generated disc and two borrowed fonts, with nothing that can
    /// arrive the wrong colour.
    ///
    /// The stones stand in ættir: towers two wide and up to four tall under a carved heading,
    /// side by side. It used to be one even field on a fixed 1120px board, a set number of columns
    /// across, and every new rist added height. At twenty-one it ran off the bottom of a
    /// 1009px window, with eight more on the way. A tower grows sideways instead, into the
    /// width a wide screen has spare, and a rist is found by what it is for rather than by
    /// where it happened to land in the catalogue.
    ///
    /// Nothing about the size is fixed any more. Fit walks a ladder of states from the full
    /// one down - full tiles first, standing and then lying four wide, each at 100px stones and
    /// then 78; then tiles without their value line, standing and then lying, at 100, 78 and
    /// 64 - and the first that fits the screen is drawn. There is still no scroll view; if
    /// nothing fits, the smallest state is drawn anyway and the log says so.
    /// </summary>
    internal static class RistPanel
    {
        private const float SideMargin = 24f;

        /// <summary>
        /// The panel hangs from here rather than being centred on the screen. Centred, a short
        /// field of three-tall towers sat a third of the way down a tall window with a band of
        /// empty void above it, which read as a dialog floating in space rather than a page.
        /// Top-anchored is also where the eye starts, and it stays put as ættir grow.
        /// </summary>
        private const float TopMargin = 40f;
        private const float BottomMargin = 20f;
        private const float HeaderH = 57f;

        private const float DetailWidth = 268f;
        private const float DetailPad = 22f;

        /// <summary>
        /// The least the detail column can be drawn in: ætt, name, a flavour that wraps once,
        /// three rows, the carved-at track, and the take and close lines at its foot. A short
        /// field is stretched to this rather than the column being clipped.
        ///
        /// 376 rather than 340 since the rows wrap: NEXT and CAPSTONE can both take a second
        /// line on the same stone, and each costs about 18px. Only a short field ever reaches
        /// this - five standing towers are twice as tall.
        /// </summary>
        private const float DetailMinH = 376f;

        /// <summary>
        /// The three stone sizes and what is cut into each. Snapped rather than any integer so
        /// that each size's sigil, marks and mark radius are proportions chosen by eye rather
        /// than whatever a scale factor happens to round to, and so there are three states to
        /// mock up and check rather than a continuum.
        /// </summary>
        private static readonly int[] StoneSizes = { 100, 78, 64 };
        private static readonly int[] SigilFont = { 34, 27, 22 };
        private static readonly float[] SigilBox = { 52f, 41f, 34f };
        private static readonly int[] MarkFont = { 17, 13, 12 };
        private static readonly float[] MarkBox = { 22f, 17f, 16f };
        private static readonly float[] MarkRadius = { 31f, 24f, 20f };

        /// <summary>
        /// One rung of the fit ladder. Lying lays each ætt down four wide and two tall so ættir
        /// can stack in bands; Compact drops the value line from every tile, leaving it in the
        /// detail column.
        /// </summary>
        private struct Rung
        {
            internal readonly bool Lying;
            internal readonly bool Compact;
            internal readonly int SizeIndex;

            internal Rung(bool lying, bool compact, int sizeIndex)
            {
                Lying = lying;
                Compact = compact;
                SizeIndex = sizeIndex;
            }

            internal string Label =>
                (Lying ? (Compact ? "D" : "C") : (Compact ? "B" : "A")) + StoneSizes[SizeIndex];
        }

        /// <summary>
        /// Richest first. A full tile at a smaller stone beats a bare tile at a big one, and
        /// lying ættir keep full tiles, so both come before Compact. Standing width counts
        /// ættir, not stones: a 2560 window holds seven full ættir (56 stones) on the first
        /// rung and a 1920x1080 screen five (40), with cells up to 146px wide. The log line on
        /// open names the rung each screen actually took. Everything below the first rung is for
        /// smaller screens.
        /// </summary>
        private static readonly Rung[] Ladder =
        {
            new Rung(false, false, 0), new Rung(false, false, 1),
            new Rung(true, false, 0), new Rung(true, false, 1),
            new Rung(false, true, 0), new Rung(false, true, 1), new Rung(false, true, 2),
            new Rung(true, true, 0), new Rung(true, true, 1), new Rung(true, true, 2),
        };

        private sealed class Layout
        {
            internal Rung Rung;
            internal bool Fits;

            internal float CellW, CellH, ColGap, RowGap, HeadingH, RuleAt, Gutter, BandGap;
            internal int BlockCols, BlockRows, PerBand;
            internal float BlockW, BlockH, FieldW, FieldH, BoardW, ContentH, TotalH;
            internal float X0, Y0;
        }

        private static Layout _layout;
        private static int _layoutW, _layoutH, _layoutCount, _layoutAetts;
        private static float _layoutInset = -1f;

        // Cell widths, measured from the real strings once the fonts exist. 142 and 112 are
        // floors, not guesses: the widest value line and the widest name set the cell, so a
        // longer one added later widens every tile rather than clipping.
        private static float _cellWFull = 142f;
        private static float _cellWCompact = 112f;

        private static bool _built;
        private static bool _open;
        private static int _selected;

        private static GUIStyle _void, _title, _sub, _spend, _name, _nameDim, _nameSel, _now, _nowDim,
                                _dname, _dflav, _label, _dnow, _dnext, _dcap, _dAett,
                                _slotOn, _slotOff, _take, _foot, _bar, _barTrack, _rule,
                                _aettName, _aettNameCompact, _aettCount;

        private static readonly GUIStyle[] _sigils = new GUIStyle[3];
        private static readonly GUIStyle[] _sigilsDim = new GUIStyle[3];
        private static readonly GUIStyle[] _marks = new GUIStyle[3];

        private static readonly Color Void = new Color(0.035f, 0.031f, 0.027f, 0.97f);
        private static readonly Color Gold = new Color(0.83f, 0.663f, 0.29f, 1f);
        private static readonly Color Cream = new Color(0.91f, 0.863f, 0.753f, 1f);
        private static readonly Color Muted = new Color(0.659f, 0.612f, 0.518f, 1f);
        private static readonly Color Faint = new Color(0.588f, 0.553f, 0.482f, 1f);
        private static readonly Color Green = new Color(0.498f, 0.62f, 0.541f, 1f);
        private static readonly Color Silver = new Color(0.682f, 0.717f, 0.788f, 1f);
        private static readonly Color Carve = new Color(0.106f, 0.090f, 0.063f, 1f);
        private static readonly Color CarveDim = new Color(0.165f, 0.153f, 0.137f, 1f);
        private static readonly Color Track = new Color(0.165f, 0.149f, 0.125f, 1f);
        private static readonly Color Edge = new Color(0.184f, 0.169f, 0.141f, 1f);

        // Uncarved rock: the same stone, held back rather than replaced.
        private static readonly Color Unworked = new Color(0.42f, 0.41f, 0.40f, 0.92f);

        internal static bool IsOpen => _open && Usable;

        private static bool Usable
        {
            get
            {
                if (!RistConfig.Enabled.Value) return false;
                if (Player.m_localPlayer == null || Player.m_localPlayer.IsDead()) return false;
                return !InventoryGui.IsVisible() && !Menu.IsVisible();
            }
        }

        /// <summary>
        /// Opened by the compendium tab, and by nothing else.
        ///
        /// There was a Toggle beside this, for the keybind, and it went with it: a tab is a
        /// click rather than a switch, so a toggle has no caller and no meaning here.
        ///
        /// Never opens by itself either. It used to appear the moment a level landed, which
        /// put a modal over the screen and took the mouse mid-fight - a good way to be killed
        /// by your own reward.
        /// </summary>
        internal static void Open()
        {
            _open = true;
        }

        internal static void Close()
        {
            _open = false;
        }

        internal static void Draw()
        {
            if (!IsOpen) return;

            Build();

            // The whole screen, so nothing behind it competes and there is no frame that has
            // to hold its own beside the game's windows.
            GUI.Label(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none, _void);

            var layout = CurrentLayout();

            var x = layout.X0;
            var y = layout.Y0;

            DrawHead(x, ref y, layout.BoardW);

            var fieldTop = y;
            DrawField(layout, x, fieldTop);
            DrawDetail(new Rect(x + layout.FieldW + DetailPad, fieldTop, DetailWidth, layout.ContentH));
        }

        /// <summary>
        /// The layout for this screen, worked out again only when something it depends on
        /// changed. Screen size is the usual one - a window dragged, a resolution changed.
        /// </summary>
        private static Layout CurrentLayout()
        {
            var inset = Mathf.Max(0f, RistConfig.PanelBottomInset.Value);

            if (_layout != null && _layoutW == Screen.width && _layoutH == Screen.height &&
                _layoutCount == Cards.All.Count && _layoutAetts == Cards.Aetts.Count &&
                Mathf.Approximately(_layoutInset, inset))
                return _layout;

            _layoutW = Screen.width;
            _layoutH = Screen.height;
            _layoutCount = Cards.All.Count;
            _layoutAetts = Cards.Aetts.Count;
            _layoutInset = inset;
            _layout = Fit(Screen.width, Screen.height, inset);

            // Once per change rather than per frame. Every rung below the first is a path the
            // author's own screen never takes, so the log is how anyone learns which one ran.
            if (_layout.Fits)
                RistPlugin.Log.LogInfo("Rist panel at " + Screen.width + "x" + Screen.height + ": " +
                                       _layout.Rung.Label + ", " + Cards.Aetts.Count + " aettir, board " +
                                       Mathf.RoundToInt(_layout.BoardW) + "x" + Mathf.RoundToInt(_layout.TotalH) + ".");
            else
                RistPlugin.Log.LogWarning("Rist panel does not fit " + Screen.width + "x" + Screen.height +
                                          " with " + Cards.All.Count + " rists in " + Cards.Aetts.Count +
                                          " aettir even at its smallest, so part of it is off screen. " +
                                          "There is no scroll view on purpose; a bigger window, or " +
                                          "PanelBottomInset lowered, is the fix.");

            return _layout;
        }

        private static Layout Fit(float screenW, float screenH, float inset)
        {
            var maxCount = 1;
            foreach (var aett in Cards.Aetts) maxCount = Mathf.Max(maxCount, aett.Indices.Count);

            Layout last = null;
            foreach (var rung in Ladder)
            {
                last = Measure(rung, screenW, screenH, inset, Mathf.Max(1, Cards.Aetts.Count), maxCount);
                if (last.Fits) return last;
            }

            return last;
        }

        /// <summary>
        /// The geometry of one rung on one screen. Pure arithmetic over the constants and the
        /// measured cell widths, so a rung can be checked on paper against its mockup.
        /// </summary>
        private static Layout Measure(Rung rung, float screenW, float screenH, float inset, int aetts, int maxCount)
        {
            var s = StoneSizes[rung.SizeIndex];
            var l = new Layout { Rung = rung };

            if (rung.Compact)
            {
                l.CellW = _cellWCompact;
                l.CellH = s + 22f;          // stone, 2 gap, name 20
                l.ColGap = 8f;
                l.RowGap = 10f;
                l.HeadingH = 26f;
                l.RuleAt = 20f;
                l.Gutter = 20f;
                l.BandGap = 14f;
            }
            else
            {
                l.CellW = _cellWFull;
                l.CellH = s + 42f;          // stone, 4 gap, name 20, value 18

                // 10 and 18, not the mockup's 12 and 24. The cell is measured from the widest
                // value line, which came out 146 rather than 142 in game, and at 146 five standing
                // towers needed 1616px of a 1920 screen's 1582 - so 1080p players were quietly
                // dropped to lying 78px stones, the one layout the towers were chosen to avoid.
                // These gaps fit five towers at a 146 cell exactly, and at 142 with 40px to spare.
                l.ColGap = 10f;
                l.RowGap = 14f;
                l.HeadingH = 32f;
                l.RuleAt = 22f;
                l.Gutter = 18f;
                l.BandGap = 20f;
            }

            l.BlockCols = rung.Lying ? 4 : 2;
            l.BlockRows = Mathf.Max(1, Mathf.CeilToInt(maxCount / (float)l.BlockCols));
            l.BlockW = l.BlockCols * l.CellW + (l.BlockCols - 1) * l.ColGap;
            l.BlockH = l.HeadingH + l.BlockRows * l.CellH + (l.BlockRows - 1) * l.RowGap;

            var availW = screenW - SideMargin * 2f - DetailPad - DetailWidth;
            var availH = screenH - TopMargin - BottomMargin - inset;

            // Standing ættir are one band, side by side. Lying ones take as many to a band as
            // the width holds, and stack the bands.
            l.PerBand = rung.Lying
                ? Mathf.Clamp(Mathf.FloorToInt((availW + l.Gutter) / (l.BlockW + l.Gutter)), 1, aetts)
                : aetts;
            var bands = Mathf.CeilToInt(aetts / (float)l.PerBand);

            l.FieldW = l.PerBand * l.BlockW + (l.PerBand - 1) * l.Gutter;
            l.FieldH = bands * l.BlockH + (bands - 1) * l.BandGap;
            l.BoardW = l.FieldW + DetailPad + DetailWidth;
            l.ContentH = Mathf.Max(l.FieldH, DetailMinH);
            l.TotalH = HeaderH + l.ContentH;

            l.Fits = l.FieldW <= availW && l.TotalH <= availH;

            l.X0 = Mathf.Floor((screenW - l.BoardW) * 0.5f);
            l.Y0 = TopMargin;
            return l;
        }

        private static void DrawHead(float x, ref float y, float width)
        {
            GUI.Label(new Rect(x, y, width * 0.5f, 32f), "YOUR RISTS", _title);

            var held = ClientState.Ranks.Count;
            var marks = 0;
            foreach (var kv in ClientState.Ranks) marks += kv.Value;

            GUI.Label(new Rect(x + 200f, y + 9f, Mathf.Max(360f, width - 480f), 20f),
                      "Level " + ClientState.Level + " · " + held + " of " + Cards.All.Count +
                      " carved · " + marks + " of " + Cards.All.Count * RistConfig.MaxRank.Value +
                      " marks", _sub);

            if (ClientState.HasPick)
            {
                var owed = ClientState.Owed;
                GUI.Label(new Rect(x, y + 7f, width, 22f),
                          owed == 1 ? "1 rist to spend" : owed + " rists to spend", _spend);
            }

            y += 34f;

            GUI.Label(new Rect(x, y, width, 5f), GUIContent.none, _barTrack);
            GUI.Label(new Rect(x, y, width * Mathf.Clamp01(Levels.Progress(ClientState.Xp)), 5f),
                      GUIContent.none, _bar);

            y += 5f + 18f;
        }

        /// <summary>
        /// Every ætt as a block: its heading, a rule, then its stones row by row. Empty
        /// trailing slots in a short ætt are simply not drawn.
        /// </summary>
        private static void DrawField(Layout l, float left, float top)
        {
            for (var a = 0; a < Cards.Aetts.Count; a++)
            {
                var aett = Cards.Aetts[a];
                var bx = left + (a % l.PerBand) * (l.BlockW + l.Gutter);
                var by = top + (a / l.PerBand) * (l.BlockH + l.BandGap);

                DrawHeading(l, aett, bx, by);

                for (var j = 0; j < aett.Indices.Count; j++)
                {
                    var index = aett.Indices[j];
                    var cell = new Rect(bx + (j % l.BlockCols) * (l.CellW + l.ColGap),
                                        by + l.HeadingH + (j / l.BlockCols) * (l.CellH + l.RowGap),
                                        l.CellW, l.CellH);

                    DrawStone(Cards.All[index], index, cell, l.Rung.SizeIndex, l.Rung.Compact);
                }
            }
        }

        private static void DrawHeading(Layout l, Aett aett, float x, float y)
        {
            var carved = 0;
            foreach (var index in aett.Indices)
                if (ClientState.RankOf(Cards.All[index].Id) > 0) carved++;

            var nameH = l.Rung.Compact ? 20f : 22f;

            if (!string.IsNullOrEmpty(aett.Name))
                GUI.Label(new Rect(x, y, l.BlockW, nameH), aett.Name.ToUpperInvariant(),
                          l.Rung.Compact ? _aettNameCompact : _aettName);

            GUI.Label(new Rect(x, y, l.BlockW, nameH), carved + " of " + aett.Indices.Count + " carved", _aettCount);

            var previous = GUI.color;
            GUI.color = Edge;
            GUI.Label(new Rect(x, y + l.RuleAt, l.BlockW, 1f), GUIContent.none, _rule);
            GUI.color = previous;
        }

        private static void DrawStone(Card card, int index, Rect cell, int size, bool compact)
        {
            var rank = ClientState.RankOf(card.Id);
            var maxRank = Mathf.Max(1, RistConfig.MaxRank.Value);
            var maxed = rank >= maxRank;
            var canTake = ClientState.HasPick && !maxed;

            var s = (float)StoneSizes[size];
            var disc = new Rect(cell.x + (cell.width - s) * 0.5f, cell.y, s, s);
            var hovered = disc.Contains(Event.current.mousePosition);

            // Hovering selects, so the detail column follows the cursor without a click, and a
            // click only ever spends a pick. Two gestures that never overlap.
            if (hovered) _selected = index;

            var texture = Stones.For(card);
            if (texture == null) return;

            // The outline differs per stone, so a highlight cannot be a circle drawn over the
            // top. It is the stone itself, drawn slightly larger and tinted behind - which
            // follows any shape for free and needs no second texture.
            var previous = GUI.color;

            if (maxed || (hovered && canTake))
            {
                GUI.color = maxed ? Gold : new Color(Gold.r, Gold.g, Gold.b, 0.55f);
                GUI.DrawTexture(Grow(disc, maxed ? 3f : 4f), texture);
            }

            // Uncarved rock is the same stone held well back, rather than a second texture:
            // the shape is how a rist is recognised, and it should not change when it is
            // taken.
            GUI.color = rank > 0 ? Color.white : Unworked;
            GUI.DrawTexture(disc, texture);
            GUI.color = previous;

            var cx = disc.x + s * 0.5f;
            var cy = disc.y + s * 0.5f;
            var box = SigilBox[size];

            GUI.Label(new Rect(cx - box * 0.5f, cy - box * 0.5f, box, box), Sigil(card),
                      rank > 0 ? _sigils[size] : _sigilsDim[size]);

            var marks = Stones.MarksFor(card, maxRank);
            var markBox = MarkBox[size];

            for (var m = 0; m < rank && m < marks.Length; m++)
            {
                var angle = m / (float)maxRank * Mathf.PI * 2f;
                var px = cx + Mathf.Sin(angle) * MarkRadius[size];
                var py = cy - Mathf.Cos(angle) * MarkRadius[size];

                GUI.Label(new Rect(px - markBox * 0.5f, py - markBox * 0.5f, markBox, markBox),
                          marks[m], _marks[size]);
            }

            if (canTake && hovered && Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                Take(card.Id);
                Event.current.Use();
            }

            // The selected stone's name goes gold, so the stone the detail column is talking
            // about stays findable once the cursor has moved across to read it.
            var nameStyle = index == _selected ? _nameSel : rank > 0 ? _name : _nameDim;

            var y = disc.yMax + (compact ? 2f : 4f);
            GUI.Label(new Rect(cell.x, y, cell.width, 20f), card.Name, nameStyle);

            if (compact) return;

            // No "at rank 1" suffix on an untaken stone: it pushed every line past the edge of
            // its cell, so they read "...stamina at ranl". What a stone is worth uncarved is its
            // first rank by definition.
            GUI.Label(new Rect(cell.x, y + 20f, cell.width, 18f),
                      card.Describe(Mathf.Max(1, rank)),
                      rank > 0 ? _now : _nowDim);
        }

        /// <summary>
        /// The detail column, the way the crafting window pairs a list with a panel. It shows
        /// whatever the cursor is over, so a full screen of stones stays readable without
        /// every stone having to state its own case.
        /// </summary>
        private static void DrawDetail(Rect rect)
        {
            if (_selected < 0 || _selected >= Cards.All.Count) return;

            var card = Cards.All[_selected];
            var rank = ClientState.RankOf(card.Id);
            var maxRank = Mathf.Max(1, RistConfig.MaxRank.Value);
            var maxed = rank >= maxRank;

            var previousRule = GUI.color;
            GUI.color = Edge;
            GUI.Label(new Rect(rect.x - DetailPad * 0.5f, rect.y, 1f, rect.height), GUIContent.none, _rule);
            GUI.color = previousRule;

            var y = rect.y;
            var w = rect.width;

            // Which ætt the stone stands in, above its name, so the heading it sits under on
            // the field is also said here when the eye has moved across to read.
            if (!string.IsNullOrEmpty(card.Aett))
            {
                GUI.Label(new Rect(rect.x, y, w, 18f), card.Aett.ToUpperInvariant(), _dAett);
                y += 18f;
            }

            GUI.Label(new Rect(rect.x, y, w, 28f), card.Name, _dname);
            y += 30f;

            var flavourH = _dflav.CalcHeight(new GUIContent(card.Flavour), w);
            GUI.Label(new Rect(rect.x, y, w, flavourH), card.Flavour, _dflav);
            y += flavourH + 16f;

            y = Row(rect.x, y, w, "NOW", rank > 0 ? card.Describe(rank) : "Not yet carved", _dnow);
            y = Row(rect.x, y, w, "NEXT",
                    maxed ? "Fully carved" : card.Describe(rank + 1) + " at rank " + (rank + 1), _dnext);

            if (card.HasBonus)
            {
                var times = Card.BonusTimes(rank);
                var at = Mathf.Max(1, RistConfig.BonusEvery.Value) * (times + 1);

                y = Row(rect.x, y, w, "CAPSTONE",
                        times > 0 ? "★ " + card.DescribeBonus(times)
                                  : "★ " + card.DescribeBonus(1) + " at rank " + at, _dcap);
            }

            GUI.Label(new Rect(rect.x, y, w, 18f), "CARVED AT", _label);
            y += 21f;

            // Sorted for display only. The stored order is the order the ranks were bought,
            // which stops being ascending the moment a pick is returned and respent - a card
            // deleted from the catalogue hands back its picks with their original levels, so
            // a track came out reading "11 12 8 9 10". Every number is true; ascending is
            // simply how a set of levels reads.
            var levels = ClientState.LevelsOf(card.Id);
            var shown = levels == null ? null : new List<int>(levels);
            if (shown != null) shown.Sort();

            for (var i = 0; i < maxRank; i++)
            {
                var slot = new Rect(rect.x + i * 35f, y, 30f, 22f);
                Frame(slot, i < rank ? Gold : Edge);

                if (i < rank)
                {
                    // A 0 is a rank taken before levels were recorded, and cannot be dated.
                    var level = shown != null && i < shown.Count ? shown[i] : 0;
                    GUI.Label(slot, level > 0 ? level.ToString() : "—", _slotOn);
                }
                else
                {
                    // Deliberately empty. It used to show the rank number, which put a level
                    // and a rank side by side in one row with nothing to tell them apart -
                    // "11 12 3 4 5" reads as one sequence and is two. Every number in this row
                    // is a level now, and how many are left is the count of empty slots.
                    GUI.Label(slot, "", _slotOff);
                }
            }

            GUI.Label(new Rect(rect.x, rect.yMax - 52f, w, 30f),
                      maxed ? "Fully carved"
                            : ClientState.HasPick ? "Click the stone to carve it"
                                                  : "No rist to spend", _take);

            // Moved here from a footer row under the field. The field's height now changes
            // with the screen and the ættir, and a footer tracking it was a line that moved
            // every time; the foot of this column is always in the same place relative to it.
            GUI.Label(new Rect(rect.x, rect.yMax - 18f, w, 18f), "Escape to close", _foot);
        }

        /// <summary>
        /// A labelled line in the detail column.
        ///
        /// The boxes are 18 and 22 rather than 14 and 20 because the borrowed serif sits
        /// taller than the Arial these were first cut for - at the smaller numbers every
        /// label lost its bottom half and the capstone line lost its descenders. Third time
        /// this exact mistake has been made in this file; the fix is always the same one.
        ///
        /// The value wraps and the row grows to fit it. It was one fixed 22px line, which held
        /// every value while they were "+5% carry weight" and cut off the 1.5 capstones:
        /// "★ arrows from a crouch land silent at rank 5" is wider than the 268px column.
        /// </summary>
        private static float Row(float x, float y, float w, string label, string value, GUIStyle style)
        {
            GUI.Label(new Rect(x, y, w, 18f), label, _label);
            var h = Mathf.Max(22f, style.CalcHeight(new GUIContent(value), w));
            GUI.Label(new Rect(x, y + 19f, w, h), value, style);
            return y + 23f + h;
        }

        /// <summary>
        /// The rune in the middle of a stone. Authored per card in the catalogue's last field,
        /// because a card's identity should be legible before its name is read - and falling
        /// back to the first letter of the id keeps a card written before sigils existed from
        /// coming out blank.
        /// </summary>
        private static string Sigil(Card card)
        {
            if (!string.IsNullOrEmpty(card.Sigil)) return card.Sigil;
            return card.Id.Length > 0 ? card.Id.Substring(0, 1).ToUpperInvariant() : "·";
        }

        /// <summary>A one-pixel outline as four thin rects, tinted through GUI.color.</summary>
        private static void Frame(Rect r, Color colour)
        {
            var previous = GUI.color;
            GUI.color = colour;

            GUI.Label(new Rect(r.x, r.y, r.width, 1f), GUIContent.none, _rule);
            GUI.Label(new Rect(r.x, r.yMax - 1f, r.width, 1f), GUIContent.none, _rule);
            GUI.Label(new Rect(r.x, r.y, 1f, r.height), GUIContent.none, _rule);
            GUI.Label(new Rect(r.xMax - 1f, r.y, 1f, r.height), GUIContent.none, _rule);

            GUI.color = previous;
        }

        private static Rect Grow(Rect r, float by)
        {
            return new Rect(r.x - by, r.y - by, r.width + by * 2f, r.height + by * 2f);
        }

        /// <summary>
        /// How to open this, said in terms of the only way in there is. There was a keybind
        /// once and this named it; it is gone rather than unbound, so naming a key would send
        /// people to one that does not exist.
        /// </summary>
        internal static string OpenHint()
        {
            return "A rist to spend — see your rists in the inventory";
        }

        private static void Take(string id)
        {
            // Predicted before the send. ZRoutedRpc handles a message addressed to yourself
            // inline, so on a host the server has already answered by the time SendPick
            // returns - predicting afterwards stacked a second rank on top of the answer.
            ClientState.PredictTake(id);
            Net.SendPick(id);
        }

        private static void Build()
        {
            // A font the styles were built with has been destroyed - logging out to the menu
            // unloads the rune font's bundle - or the rune face is a stand-in due a retry. Build
            // again rather than draw plain letters for the rest of the session.
            if (_built && Skin.Lost)
            {
                RistPlugin.Log.LogInfo("The rists panel's fonts need finding again; rebuilding its styles.");
                Skin.Reset();
                _built = false;
            }

            if (_built) return;
            _built = true;

            Skin.Ensure();

            _void = new GUIStyle { normal = { background = Solid(Void) } };
            _barTrack = new GUIStyle { normal = { background = Solid(Track) } };
            _bar = new GUIStyle { normal = { background = Solid(Gold) } };
            _rule = new GUIStyle { normal = { background = Solid(Color.white) } };

            _title = Head(26, Gold);

            _sub = Body(14, Muted);

            _spend = Head(15, Gold);
            _spend.alignment = TextAnchor.UpperRight;

            _name = Body(14, Cream);
            _name.alignment = TextAnchor.UpperCenter;
            _nameDim = Body(14, Faint);
            _nameDim.alignment = TextAnchor.UpperCenter;
            _nameSel = Body(14, Gold);
            _nameSel.alignment = TextAnchor.UpperCenter;

            _now = Body(12, Green);
            _now.alignment = TextAnchor.UpperCenter;
            _nowDim = Body(12, new Color(0.451f, 0.502f, 0.471f, 1f));
            _nowDim.alignment = TextAnchor.UpperCenter;

            _aettName = Head(15, Cream);
            _aettNameCompact = Head(14, Cream);
            _aettCount = Body(12, Muted);
            _aettCount.alignment = TextAnchor.UpperRight;

            // The inscription is cut into the stone rather than written on it, so it is dark
            // against the granite - which is all a carve is at this size. One style per stone
            // size, built once, rather than a fontSize changed on a shared style every frame.
            for (var i = 0; i < StoneSizes.Length; i++)
            {
                _sigils[i] = Rune(SigilFont[i], Carve);
                _sigilsDim[i] = Rune(SigilFont[i], CarveDim);
                _marks[i] = Rune(MarkFont[i], Carve);
            }

            _dname = Head(21, Gold);

            _dflav = Body(13, Muted);
            _dflav.wordWrap = true;
            _dflav.fontStyle = FontStyle.Italic;

            _dAett = Body(12, Faint);

            // 12, not 11: 11 was under the smallest size this panel is allowed to print.
            _label = Body(12, Faint);
            // Wrapped, so a long value takes a second line rather than losing its end. See Row.
            _dnow = Body(14, Green);
            _dnow.wordWrap = true;
            _dnext = Body(14, Gold);
            _dnext.wordWrap = true;
            _dcap = Body(14, Silver);
            _dcap.wordWrap = true;

            _slotOn = Body(12, Gold);
            _slotOn.alignment = TextAnchor.MiddleCenter;
            _slotOn.normal.background = Solid(new Color(0.83f, 0.663f, 0.29f, 0.12f));

            _slotOff = Body(12, Faint);
            _slotOff.alignment = TextAnchor.MiddleCenter;
            _slotOff.normal.background = Solid(new Color(0.129f, 0.118f, 0.098f, 1f));

            _take = Head(16, Gold);
            _take.alignment = TextAnchor.MiddleCenter;

            _foot = Body(12, Faint);

            MeasureCells();
        }

        /// <summary>
        /// Cell widths from the strings that will actually be drawn in them, now that the styles
        /// carry the real fonts. The floors are the mockup's cell widths - 142 is also what the
        /// old field's tile came to at five columns, 112 is the compact tile it introduced; a longer
        /// name or value line widens every cell, which Fit then accounts for, instead of being
        /// clipped mid-word the way "at rank" once was.
        /// </summary>
        private static void MeasureCells()
        {
            var maxRank = Mathf.Max(1, RistConfig.MaxRank.Value);
            var widestName = 0f;
            var widestValue = 0f;

            foreach (var card in Cards.All)
            {
                widestName = Mathf.Max(widestName, _name.CalcSize(new GUIContent(card.Name)).x);

                for (var r = 1; r <= maxRank; r++)
                    widestValue = Mathf.Max(widestValue, _now.CalcSize(new GUIContent(card.Describe(r))).x);
            }

            _cellWFull = Mathf.Max(142f, Mathf.Ceil(Mathf.Max(widestName, widestValue) + 8f));
            _cellWCompact = Mathf.Max(112f, Mathf.Ceil(widestName + 8f));
            _layout = null;
        }

        private static GUIStyle Body(int size, Color colour)
        {
            var style = Text(size, colour);
            if (Skin.Face != null) style.font = Skin.Face;
            return style;
        }

        private static GUIStyle Head(int size, Color colour)
        {
            var style = Text(size, colour);
            if (Skin.HeadFace != null) style.font = Skin.HeadFace;
            return style;
        }

        private static GUIStyle Rune(int size, Color colour)
        {
            var style = Text(size, colour);
            style.alignment = TextAnchor.MiddleCenter;
            if (Skin.RuneFace != null) style.font = Skin.RuneFace;
            return style;
        }

        private static GUIStyle Text(int size, Color colour)
        {
            return new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                normal = { textColor = colour },
                wordWrap = false,
                richText = false,
            };
        }

        // Keyed by colour so a rebuild after a lost font reuses them instead of leaking a fresh
        // set of 1x1 textures each time.
        private static readonly Dictionary<Color, Texture2D> _solids = new Dictionary<Color, Texture2D>();

        private static Texture2D Solid(Color colour)
        {
            if (_solids.TryGetValue(colour, out var cached) && cached != null) return cached;
            var made = MakeSolid(colour);
            _solids[colour] = made;
            return made;
        }

        private static Texture2D MakeSolid(Color colour)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, colour);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
