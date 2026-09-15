# Rist

Rist adds a character level beside Valheim's skills. Every level gives you a pick, and a pick
carves one more rank into a runestone you choose. Vanilla skills are read and never written,
rescaled or reskinned.

Built against Valheim 1.0.7, Unity 6000.0.75, BepInEx 5.4.23.5, Harmony 2.9. One DLL plus one
text file, no asset bundle.

## Features

- A character level with its own curve, fed by skill level-ups.
- One pick per level. Picks bank; nothing expires.
- Runestones raise a stat by a fixed amount per rank, to rank 5 by default. Every fifth rank
  grants a capstone, a second and different effect.
- No runestone carries a drawback.
- A panel on the compendium bar showing every runestone, what it is worth now, what the next
  rank adds, and which character level bought each rank.
- An experience bar on the HUD, cloned from one of the game's own bars, that flashes while a
  pick is unspent.
- A line above other players' heads: level, total XP, days since their last death.
- Dying no longer costs skill progress. On by default, and switchable.
- The catalogue is a plain text file. Adding a runestone is one line and no rebuild.
- On a server, progress is kept in the server's own ledger rather than in the character file.

## How it works

### XP and levels

XP comes from skill level-ups and from nothing else. Almost everything in Valheim raises some
skill, so building, sneaking, sailing, cooking and fishing all pay in without Rist keeping a
list of what counts.

A level-up is worth the skill level it reached, not a flat amount, multiplied by that skill's
weight. Vanilla's own skill cost curve is `pow(level+1, 1.5)`, so early levels are nearly free;
a flat rate would hand out runestones in the first hours and dry up when the deep ranks start
to matter.

The weight is what stops one activity carrying the level. On the live server, a character at
Rist level 12 held 1346 XP of which WoodCutting alone was 666, against 293 for everything
earned in a fight. Default weights:

| Weight | Skills |
| --- | --- |
| `1` | Swords, Knives, Clubs, Polearms, Spears, Axes, Bows, Crossbows, Unarmed, Blocking, ElementalMagic, BloodMagic |
| `0.5` | Dodge, Sneak, Swim |
| `0.25` | WoodCutting, Pickaxes, Crafting, Farming, Cooking, Fishing, Run, Jump, Ride |

Full price for a skill raised by something that can kill you, a fraction for repetition that
cannot. The same character comes out at level 6 under this table, with over half of it earned
fighting. `SkillWeights` is meant to be edited, and on a server the host's table applies to
everyone.

Cumulative XP for level N is `LevelBaseXp * N^LevelExponent`, which is `40 * N^1.4` by default.

Changing the weights affects new XP only. To re-price characters that already exist, change
`WeightGeneration` to any different text: every character is then recomputed once, on its next
login, from the skill levels the server has watched it reach. Runestones already taken are
kept, so a character re-priced from level 12 to level 6 still holds all twelve ranks and earns
no new pick until it passes level 12 again.

### Spending picks

The panel is a fifth tab on the compendium bar, beside the raven and the trophy. There is no
keybind; the tab is the only way in.

Each runestone carries its own outline, rock and runes, seeded off its id, so it looks the same
in every session and on every machine. A rank cuts one more rune into the rim. The column beside
the field follows the cursor and says what the stone is worth now, what the next rank would make
it, what sits at the bottom of its track, and the character level that bought each rank.

The stones stand in five ættir, Combat, Survival, Endurance, Stealth and Utility: towers two
stones wide and up to four tall, each under its name and a count of how many in it are carved. A
new theme adds a tower to the right rather than a row at the bottom, so the panel grows into the
width a wide screen has spare. It used to be one field a fixed number of columns across, and at twenty-one runestones it
ran off the bottom of a short window.

The panel works out from the screen how to fit. On a wide or 1920x1080 screen the towers stand
with full-size stones and every tile shows its value. On a smaller one it tries smaller stones,
then lays each ætt down four wide, and only after that drops the value line from the tiles (it
stays in the column beside). It never scrolls. If a taskbar covers the bottom of the game window,
`PanelBottomInset` keeps that strip clear, and it stays your own setting on a server.

### Death

`RemoveDeathSkillLoss` skips `Skills.OnDeath` entirely. Valheim's own world modifier is not the
same thing: `Skills.OnDeath` calls `LowerAllSkills(m_DeathLowerFactor * Game.m_skillReductionRate)`,
and inside that method only the level loss is scaled by the factor.

```csharp
m_level -= m_level * factor;   // scaled: a zero factor means no loss
m_accumulator = 0f;            // not scaled: always wiped
// ...and "$msg_skills_lowered" is shown regardless
```

So setting the world's skill reduction to zero still discards partial progress toward the next
level in every skill, and still announces that skills were lowered. Skipping the method removes
all three.

### The bar and the plate

The experience bar is a clone of one of the game's own upright bars, so it carries the real
frame, track and fill sprites at whatever HUD scale you run. It drains rather than snapping to
empty on a level-up, and flashes while a pick is waiting. If the clone ever fails against a
game update it falls back to a plain drawn bar.

The plate writes level, total XP and days since the last death into the game's own nameplate,
so it inherits the plate's font, fade and distance rules. Each client publishes its own three
numbers onto its own character's network object; the server cannot write them, because a write
to a network object you do not own is discarded. A player without Rist shows a plain name.

Days alive counts from the day the plate first saw the character, not from the day it was
created. The game keeps no creation date, so every existing character reads 0 when this first
runs and grows from there. It resets on death and is counted in world days.

`PlateFormat` is the whole of the presentation: the fields, their order, their colours and
whether it wraps to a second line. It is a TextMeshPro label, so rich text works.

## Installation

1. Install [BepInEx 5.4.2350](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).
   Rist uses the BepInEx 5 API and does not run on BepInEx 6.
2. Install Rist from
   [Thunderstore](https://thunderstore.io/c/valheim/p/Ezomic/Rist/) or with a mod manager, or
   drop `Rist.dll` and `cards.txt` into `BepInEx\plugins\Rist`.

`cards.txt` must sit beside the DLL. It ships with the mod and is not optional: without it no
runestone can be taken, and the startup log says `NOT READY` rather than `ready`.

Install it on the client and on the server. The server decides every number, the client reports
skill-ups and applies the effects, and neither half does anything useful alone.

[Longhouse Core](https://thunderstore.io/c/valheim/p/Ezomic/Longhouse_Core/) is an optional soft
dependency. See [Multiplayer](#multiplayer) for what Rist gives up without it.

## The catalogue

`cards.txt` is read once at load. Edit it and restart the game; there is no reload.

One runestone per line, eight pipe-separated fields:

```
id | Name | flavour text | effect | value-per-rank | capstone effect | capstone value | sigil
```

The last three are optional. A line with five fields is still a valid runestone.

- **id** is the key the ledger stores. It is permanent: change it and every rank anyone bought
  in that runestone is a rank in a runestone that no longer exists.
- **effect** is the literal name of a public float field on the game's own `SE_Stats`, or one of
  the specials below. Valheim already sums about forty-five of these across active status
  effects (carry weight, armour, the six stamina costs, stealth, fall damage, regen rates,
  skill gain), so naming one turns it into a runestone with no code change.
- **value-per-rank** accumulates: rank 3 of a 15-per-rank line gives 45.
- **capstone effect** and **capstone value** are granted once per `BonusEvery` ranks, which at
  the default `MaxRank` 5 means once, on the final rank. A capstone can name anything an effect
  can, including a special.
- **sigil** is the rune cut into the middle of the stone, written as a Latin letter. The game's
  rune font is a Latin-mapped decorative face, so a real runic code point renders as an empty
  box. Omit it and the first letter of the id is used.

Specials are effects with no `SE_Stats` field behind them, handled in code:

| Special | Effect |
| --- | --- |
| `*stamina:move` | Run, jump, dodge, swim and sneak all cost less |
| `*stamina:fight` | Attacking and blocking both cost less |
| `*attackspeed:melee` | Faster swings with a weapon |
| `*attackspeed:tools` | Faster swings with a pickaxe, axe, hammer, hoe or cultivator |
| `*attackspeed:ranged` | A bow reaches full draw sooner, a crossbow reloads sooner, and both fire faster |
| `*attackspeed:magic` | A staff casts faster. A staff firing several projectiles per cast has its burst timing sped to match; a looping staff that pays eitr once per hold stays at vanilla speed |
| `*damage:ranged` | More damage from bows and crossbows only. A fraction: `0.05` is 5% |
| `*unbroken` | A hit cannot stagger you out of a staff cast before the spell fires. A flag: write `1` |
| `*answer` | A perfect dodge arms your next hit on an enemy within 4 seconds. A fraction of that hit's damage |
| `*answer:stagger` | Chance that the answering blow staggers anything a parry would, never a boss. A fraction: `0.30` is 30% |
| `*lowdraw` | Seconds of a bow draw from a crouch that still count as sneaking, and the shot that ends such a draw. The character still rises to draw |
| `*lowdraw:silent` | Arrows loosed from a crouch make no noise where they land, so a miss does not alert the animals around it. A creature the arrow hits is still alerted by the hit. A flag: write `1` |
| `*sneakattack` | Raises a weapon's own sneak attack multiplier. A fraction: `0.10` is 10% more. Weapons with no sneak attack gain none |
| `*sneakattack:stagger` | A sneak attack on an unalerted, non-boss creature staggers it, at most once per creature per 300 seconds. A flag: write `1` |
| `*mead:duration` | Buff meads last longer. A fraction. Every mead except the ones that restore health, stamina or eitr, so the Lingering meads, Tonic of Ratatosk and Lightfoot Mead are included |
| `*mead:fullcask` | Chance a buff mead is not used up when drunk, rolled per drink. A fraction: `0.25` is 25% |
| `*power:cooldown` | Shortens the forsaken power's cooldown. Negative is shorter, and it cannot go below half |
| `*power:duration` | Seconds added to the forsaken power for every player it reaches when you cast it. A power cast by someone without it lasts the normal time for everyone. Applies to every forsaken power |
| `*exploreradius` | The map reveals a wider circle as you walk. A fraction: `0.05` is 5% further |
| `*windcone` | Narrows the sailing dead zone and turns the sail's push toward the bow inside the arc it opens, so you can sail closer to the wind. Sailing dead upwind still stalls. A fraction of the zone, capped at `0.8`. Only applies to a ship you are steering, and the wind ring's dead zone narrows to match |
| `*rowspeed` | Rows faster, forward and back. A fraction of top rowing speed: `0.20` is 20% faster. Only applies to a ship you are steering |
| `*inventoryrow` | Adds rows to the player inventory grid. Not used by the default catalogue, since Valheim 1.0 sells rows from the trader, but still recognised for custom ones |

The header comment in `cards.txt` carries the sign conventions, which are the game's rather than
Rist's. The stamina-use modifiers are fractions where negative means cheaper, so `-0.05` is a 5%
discount per rank. Four fields count from 1 rather than 0 (the three regen multipliers and
`m_damageModifier`); write the plain fraction and Rist adds the 1. Which fields those are is
read off a fresh `SE_Stats` at load rather than hardcoded, and any disagreement with the four
Rist was written against is logged.

An unknown field name or unknown special is logged and the line skipped, so a typo costs one
runestone rather than the catalogue. A blank id, a duplicate id or an unparseable number is
skipped the same way. An effect with no readable display name still works, and is named in the
log at load so it can be added to the label table.

## Configuration

`BepInEx\config\ezomic.valheim.rist.cfg`

BepInEx writes every entry to disk on first run and the saved value beats a new default in code.
If a change appears to do nothing, check the file.

### General

| Key | Default | Effect |
| --- | --- | --- |
| `Enabled` | `true` | Off leaves levels and runestones recorded but stops granting and applying them |
| `Verbose` | `false` | Log every XP grant, rejection and runestone applied, plus a dump of the vanilla UI this mod clones |
| `ShowInfoTab` | `true` | Add the runestone tab to the compendium bar. There is no keybind, so off means no way in |

### Levelling

| Key | Default | Effect |
| --- | --- | --- |
| `XpPerSkillLevel` | `1` | XP per skill level-up, multiplied by the level reached and the skill's weight |
| `LevelBaseXp` | `40` | Cumulative XP for character level 1 |
| `LevelExponent` | `1.4` | Cumulative XP for level N is `LevelBaseXp * N^LevelExponent` |
| `SkillWeights` | see table above | `Skill=multiplier` pairs, comma separated. Names are `Skills.SkillType` names and are case-insensitive; a raw type number also works. Negatives are clamped to zero |
| `DefaultSkillWeight` | `1` | What a skill not named in `SkillWeights` is worth |
| `WeightGeneration` | `1` | Change to any different text to recompute every character once, on its next login |

### Cards

| Key | Default | Effect |
| --- | --- | --- |
| `MaxRank` | `5` | How deep one runestone goes, and how many slots its track shows |
| `BonusEvery` | `5` | Ranks between capstones |
| `PanelBottomInset` | `0` | Pixels at the bottom of the screen the panel keeps clear, for a taskbar over the game window. About `48` clears a Windows taskbar. Stays local on a server |
| `AttackSpeedMax` | `1` | Ceiling on the attack-speed specials, as a fraction. `1` means a swing or cast can at most run at double speed, and a bow draw or crossbow reload can at most take half its time |
| `ReconcileMaxLoss` | `0.34` | How much of the ledger's runestone history may vanish from `cards.txt` in one go before the server refuses to reconcile at all, as a fraction of the distinct ids players hold. Server-side only |

`ReconcileMaxLoss` is worth understanding before you edit the catalogue. Removing a runestone
from `cards.txt` hands its picks back and drops the ranks bought in it, which is correct when it
was meant and is a permanent wipe when the catalogue merely failed to load. Over the threshold,
nothing is returned and nothing is removed; the server logs why on every login and leaves every
record alone until you fix the catalogue or raise this and restart. A catalogue that is missing,
unreadable or empty is refused whatever this is set to.

### Death

| Key | Default | Effect |
| --- | --- | --- |
| `RemoveDeathSkillLoss` | `true` | Skip `Skills.OnDeath`, which removes the level loss, the accumulator wipe and the message together |

### Gate

These bound what a client's skill-up report can be worth. Skills live on the client, so a report
cannot be verified, only bounded. None of them detects a cheat, none disconnects anyone, and
nothing is ever taken away. They are inert in single player.

| Key | Default | Effect |
| --- | --- | --- |
| `CheckSkillBaseline` | `true` | On login, take the character's skill list as the baseline this server pays from. Off also turns off `MaxSkillLevelJump` |
| `CreditExistingSkills` | `true` | Pay a joining character for the skills it already has, so it arrives at the level those skills are worth with the picks to spend. Off counts only level-ups watched on this server |
| `MaxSkillUpsPerMinute` | `30` | Ceiling on accepted skill-up reports per player |
| `MaxSkillLevelJump` | `1` | Levels above the baseline a single report may claim. Raise it only if you run a mod that grants skill levels in bulk |
| `MaxXpPerMinute` | `600` | XP paid per player per minute of connected time. `0` or less turns it off |
| `XpBurst` | `1800` | How much unspent allowance banks, so an honest burst is still paid in full |
| `CappedMessage` | see cfg | Shown once per session, centre screen, when the earning cap withholds XP |

Crediting a joining character has a cost: a maxed vanilla character walks in at Rist level 219
with the picks to take most of the catalogue. If that matters on your server, restrict who can
join rather than withholding XP. [Dyrr](https://github.com/Ezomic/valheim-dyrr) does that.

### Plate

| Key | Default | Effect |
| --- | --- | --- |
| `ShowPlate` | `true` | The Rist line above other players' heads. Off also stops this character publishing its own numbers |
| `PlateFormat` | see cfg | Tokens `{lvl}` `{xp}` `{days}` `{name}`, rich text, `\n` for a second line. Drop a token to drop that field |

### Bar

| Key | Default | Effect |
| --- | --- | --- |
| `ShowXpBar` | `true` | Show the experience bar. It hides with the rest of the interface |
| `VanillaBar` | `true` | Clone one of the game's own bars. Off draws a plain rectangle, which is also the automatic fallback if the clone fails |
| `BarUpright` | `false` | Stand the bar on end like stamina and eitr. Off lays it flat, which is what a long bar needs |
| `BarFollowStamina` | `true` | Place the bar relative to the stamina bar. Off falls back to `BarPosX` / `BarPosY`, as does a stamina bar that cannot be found |
| `BarOffsetX` / `BarOffsetY` | `0` / `70` | Pixels right of and below the stamina bar's centre, when following it |
| `BarPosX` / `BarPosY` | `172` / `105` | Pixels from the left edge and the bottom to the bar's centre, when not following |
| `BarSize` | `240` | Length in canvas units, the units vanilla sizes its own bars in. 64 is a starting stamina bar. Thickness comes from the borrowed sprite and is not settable |
| `BarBuildRaise` | `155` | Pixels to lift the bar while the build or ship panel is open |
| `BarColour` | `4FB3A5` | `RRGGBB`. The trailing fill is the same hue held back. `E4DCC4` for bone. An unparseable value falls back to gold |
| `BarFlashSeconds` | `4` | How often the cloned bar pulses while a pick is waiting |
| `BarNoteGap` | `6` | Gap between the top of the bar and the "rist waiting" note above it |
| `BarX` / `BarBottom` / `BarThickness` / `BarLength` | `168` / `75` / `10` / `60` | Place the **fallback** bar only |

Every pixel number in the `[Bar]` block is multiplied by your HUD's canvas scale, so one value
is the same visual position on every screen. `BarSize` is in canvas units and is already scaled
by the game.

## Multiplayer

Progress is kept on the server, in `BepInEx\config\rist-ledger.txt`, keyed by the platform
identity of the connection and the character being played. Valheim character files live on the
player's own disk, so nothing that decides rewards can be stored there. The client reports only
that a skill went up; every number is derived server-side.

Single player takes the same path with no special casing, and the throttles are inert there.

### With Longhouse Core

Core is a soft dependency. With it installed on both ends:

- Client mod versions and build ids are checked on connect, and mismatches are rejected.
- A hash of `cards.txt` travels with that check, so two ends running the same build over
  different catalogues are reported.
- The host's Rist config is applied on connected clients, in memory only. Your own config file
  is not modified, and your values come back when you disconnect.
- Inventory row claims are arbitrated, so Rist and any other row-granting mod stack instead of
  overwriting each other.

### Without Core

Rist installs and runs on its own, and solo you give up nothing. On a server you give up three
things:

**The catalogue is no longer checked.** `cards.txt` names what every rank is worth, effects are
applied client-side from it, and the server only ever verifies the rank. A client that edits its
own catalogue gets whatever it wrote.

**The host's curve is not applied.** A client with a different `LevelBaseXp` reads a different
level out of the same XP, and every number on its screen disagrees with the server deciding
them. The `.cfg` files have to be matched by hand.

**Inventory rows are claimed without an arbiter.** `Inventory.m_height` is a single private int
with no owner, so two mods that both want rows each write it and the last writer wins. Rist's
standalone owner is correct on its own and conflicts with any other mod that grants rows, with
the winner decided by frame ordering. Install Core if you run one.

Rist logs a warning at startup when it starts without Core, naming all three.

## Compatibility

- Requires BepInEx 5. Not compatible with BepInEx 6.
- 1.2.0 and later require Valheim 1.0. Earlier versions do not run on 1.0, and this one does not
  run on pre-1.0 Valheim.
- Conflicts with other mods that add player inventory rows unless Longhouse Core is installed.
  With Core, they stack.
- Vanilla skills are read and never written, so mods that change skill gain, add skills or
  rebalance the skill curve work alongside this. A mod that grants skill levels in bulk will
  trip `MaxSkillLevelJump` on a server; raise it.
- The three attack-speed specials patch `Attack.Start` and set the animator speed through
  `CharacterAnimEvent.Speed`. Another mod driving the same number will fight over it.

### Updating a server

The ledger format is `v4` as of Rist 1.1.0. A `v4` line does not parse on 1.0.1 or earlier and
is dropped rather than erroring, so copy `rist-ledger.txt` aside before updating a server you
care about. `v1` to `v3` lines are still read and come back as never re-priced.

## Troubleshooting

**A config change did nothing.** BepInEx writes the whole file on first run and the saved value
wins over a new default. Edit `BepInEx\config\ezomic.valheim.rist.cfg`. On a server with Core
installed, the host's values are applied over yours while you are connected.

**An edit to `cards.txt` did nothing.** It is read once at load. Restart the game.

**The startup log says `NOT READY`.** `cards.txt` is missing, unreadable, or every line was
rejected. The warnings above that line name each rejected line. Reinstall if the file is gone:
it ships with the mod.

**There is no tab on the compendium bar.** Check `ShowInfoTab`, then look for "No compendium tab
to clone" in the log. There is no keybind, so that message means the panel has no way in and is
a bug worth reporting.

**Level is 0 on a server.** Check Rist is installed on the server as well as the client, and
check `CreditExistingSkills`.

**"You are earning faster than this world will pay for."** The server's earning cap is
withholding XP. `MaxXpPerMinute` and `XpBurst` control it.

**The server log says it is refusing to reconcile.** More of the runestone history than
`ReconcileMaxLoss` allows is missing from `cards.txt`. Nothing has been lost; fix the catalogue
or raise the setting and restart.

## Known limitations

- Nothing in the runestone catalogue has been tuned against anything but reasoning and a small
  amount of live play. The curve has been retuned twice off real ledger data; the per-rank values
  have not.
- With a free choice the strongest runestone is always available, so balance rests on the
  per-rank values and on `MaxRank`.
- The plate is published by each client onto its own character, so a modded client can put
  whatever it likes there. The server's ledger is still the only thing that grants a pick.
- Ranks taken before ledger `v3` have no recorded level and show a dash permanently. Everything
  taken since is exact.
- `SE_Stats` has no max health, stamina or eitr field, since those come from food in Valheim, so
  no max-pool runestone is possible without a different mechanism.

## Design notes

Why picks are chosen rather than dealt, why a capstone sits at the end of a track, how the bar
is built out of a vanilla one, and why the ledger lives where it does:
[DESIGN.md](https://github.com/Ezomic/valheim-rist/blob/main/DESIGN.md).

## Bug reports

[The Discord](https://discord.gg/hJzAVaZ5wb) is the fastest route, and the right one if you are
not sure whether what you are seeing is a bug. Issues on
[the repo](https://github.com/Ezomic/valheim-rist) suit anything long.

Attach `BepInEx\LogOutput.log`, and say whether you were on a server or in single player. For a
runestone behaving wrongly, include the line from `cards.txt`. If a vanilla mechanic broke,
`AppData\LocalLow\IronGate\Valheim\Player.log` is where gameplay exceptions land, and it is a
different file from the one BepInEx writes.

## Discord

[discord.gg/hJzAVaZ5wb](https://discord.gg/hJzAVaZ5wb) is where mod information, updates,
support and bug reports go.

## Server

There is a small EU server running the pack if you want somewhere to play: hard combat
difficulty, resources at 1x, everything else vanilla, no application and no activity
requirements. Connection details are in the Discord.

## Part of Longhouse

Rist is part of [Longhouse](https://thunderstore.io/c/valheim/p/Ezomic/Longhouse/), a pinned set
of the Ezomic mods that installs in one click. You do not need the pack to use Rist, and it
behaves the same on its own.

## Credits

Rist is an original mod by **Robbin Thijssen** (Thijssen Software).
Copyright (c) 2026 Robbin Thijssen. MIT licensed. See `LICENSE`.
