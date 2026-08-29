# Changelog

Notable changes to Rist. Format follows [Keep a Changelog](https://keepachangelog.com),
and the mod uses [semantic versioning](https://semver.org).

## [1.1.1] - 2026-08-29

### Fixed

- **The plugin announced the wrong version of itself.** 1.1.0 moved the csproj, the
  manifest and the toml and missed the `BepInPlugin` constant, which is the version the
  plugin actually reports and half of what Core's gate compares - so the package read
  1.1.0 on Thunderstore and introduced itself as 1.0.1 on every connection. Harmless
  while everyone runs identical builds, and not harmless the moment they do not.

## [1.1.0] - 2026-08-18

XP is now weighted by **which** skill it came from, not only by how far that skill got.

### Why

The live server's ledger asked for this rather than a hunch. A character sitting at Rist level
12 held 1346 XP, of which WoodCutting alone was 666 - half the character was felled trees, and
everything earned in a fight came to 293, under a quarter of the whole. The level had stopped
describing the character and started describing how long they had stood in the Meadows with an
axe.

The existing weighting compounded the wrong way. XP being the level reached is right on its
own, but the safest repeatable activity in the game is also the one that climbs highest, so it
was collecting the most level-ups *and* the most XP per level-up.

Raising `LevelBaseXp` or `LevelExponent` cannot fix that, and it is the obvious reach, so it is
worth saying why not: both scale the whole curve, so the ratio survives untouched and the late
game pays for a problem that lives in the first hour. That is exactly what happened when the
pair was `60` and `1.5`, and it had to be walked back.

### Added

- **`SkillWeights`** - a multiplier per skill, as `Skill=multiplier` pairs. The default table
  is built on risk: full price for a skill raised by something that can kill you, half for one
  that is situational, a quarter for repetition that cannot hurt you. Synced from the host, so
  a server sets it for everyone on it.
- **`DefaultSkillWeight`** - what a skill not named in the table is worth. `1`, so a skill
  added by a game update or another mod is paid normally rather than silently going unpaid.
- **`WeightGeneration`** - bump it to any different text and every character is re-priced once,
  on its next login, from the skill levels this server watched it reach.

### Changed

- **Existing characters are re-priced on their next login.** The bundled generation is `1` and
  no existing ledger line carries a stamp, so this happens once, automatically. The character
  above goes from level 12 to level 6, with 53% of it now earned fighting.
- **Runestones already taken are kept.** A character re-priced from 12 to 6 holds all twelve
  and earns no new pick until it passes twelve again. Refunding them would turn a re-pricing
  into a free rebuild of the whole character.
- The join-time credit is weighted by the same function as the re-pricing, deliberately shared
  so the two can never drift apart.

### Ledger format

The ledger is now `v4`, carrying the generation each record was last re-priced at. `v1`-`v3`
are still read and come back as never re-priced, which is what they are.

**This is a one-way door.** A `v4` line does not parse on 1.0.1 or earlier, which drops the
record rather than erroring, so copy `rist-ledger.txt` aside before updating a server you care
about.

## [1.0.1] - 2026-08-18

Documentation and the text players read, with no change to what the mod does.

### Threshold is Dyrr

The companion mod that decides who may join a world was renamed, and Rist pointed at the old
name in two places that matter: the config descriptions, which are read by anyone who opens
`rist.cfg`, and the notes on what Rist deliberately does not do. Both now say Dyrr.

### The README is shorter

The design argument and the technical notes moved to `DESIGN.md`, leaving the README as what
someone reads before installing. The reasoning is all still there, one link away, rather than
in front of a reader who only wanted to know what the mod is.

Its two links are absolute now. Thunderstore builds the package page from the README alone, so
a sibling-folder path pointed at nothing there and `DESIGN.md` is not in the zip at all - both
would have rendered as dead links on the store page and nowhere else, which is the kind of
fault you only see after publishing.

Em-dashes are gone from the docs throughout.

## [1.0.0] - 2026-08-16

First release. There is no 0.1.0 entry below it because 0.1.0 was never published. It was
this same work under a version that had not yet been judged ready, and folding it in beats
inventing a release nobody could have installed.

### The line this sits on

> **It adds a level beside the skills. It never reads, writes, rescales or reskins a vanilla skill.**

Valheim has skills but no character level. Rist adds one that runs alongside them. Vanilla
skills are read and never written.

### Levels and picks

- Every character level earns one **pick**, spent on any runestone in the catalogue to raise it a
  rank, up to five.
- **Picks bank.** Nothing expires and the panel waits.
- **No drawbacks.** A runestone is a reward or it is nothing. A cost attached to a reward makes
  a pick something you can regret, and regretting a permanent choice is a reason to stop
  playing rather than a reason to think harder.

### Free choice, not a dealt hand

An earlier version dealt three runestones per level, seeded so that quitting on a bad offer and
returning re-offered the same three, since otherwise the pick is theatre, since anyone can reroll
until they get what they want.

Choosing freely deletes that problem rather than defending against it: no roll to reroll, no
offer to store in the ledger or on the wire, no second window with its own visibility rules.
What it costs is the tension of a hand you are dealt. A free choice means the strongest runestone
is always available, so balance now lives in the runestones themselves and in `MaxRank`.

The panel carries the weight for this: choosing between nineteen runestones is only a real choice
if each stone says both what it is worth now and what the pick would make it.

### Every fifth rank is a capstone

Taking a runestone the whole way grants something the earlier ranks did not hint at, rather
than one more slice of the same number. It is what makes depth a decision against breadth
instead of a rounding error: five ranks of the same small bonus is the same reward as five
different small bonuses, so without this there was no reason to ever finish anything.

### Capacity is a capstone, not a runestone of its own

This began as an inventory problem: late-biome kit eats the grid and leaves nothing for what
you went out to fetch. The cheap fix is more rows, and that is exactly what
[Hoard](../hoard) argues against.

It was a runestone once, called **Deep pack**, and it was the strongest thing in the catalogue:
taken first, every time, which is not a choice. A row now sits at the bottom of Ox-backed and
of Sure hand, so capacity is the reward for taking a hauling runestone all the way rather than a
purchase anyone makes on their first pick.

### The panel

A fifth tab on the compendium bar, beside the raven and the trophy, opens a full screen of
runestones, one per rist, each with its own outline, rock and marks, all seeded off the card
id so a rist looks the same in every session and on every machine. A rank cuts one more mark
into the rim, and the mark holds **the level that bought it**.

Three designs came before it: a draft window dealing three at random, a board of tiles, and
that same board dressed in the game's own wooden sprites. The last is why this one exists.
Borrowing a window frame is imitation, and it read as a different game no matter how close the
sprites got. A runestone imitates nothing.

### The bar

A fifth bar under stamina, cloned from one of the game's own so it carries the same frame, fill
and trailing fill. It follows the stamina bar rather than sitting at fixed pixels, so "below
stamina" stays true at any resolution and HUD scale, and it flashes while a pick is waiting.

Verdigris, because the HUD already spends red, yellow, blue and orange between its four bars
and a fifth in any of those reads as one of them.

### What the server decides

The ledger is held by the server and keyed to the platform identity plus the character, never
to the character file. A character file sits on the player's own disk, so nothing that decides
rewards may live there. The client reports only that a skill went up; every number is derived
server-side.

Three ceilings bound what a report can be worth, because skills live on the client and a report
is therefore a claim that cannot be verified, only bounded:

- **Reports per minute**, which caps how many claims are accepted.
- **A one-level step**, which caps how much a single claim may say. Skills level one at a time
  and fire one callback each, so a report naming a level far above the baseline this server
  watched that skill reach did not come from playing. This also protects the baseline, which
  would otherwise adopt the forged level and give every later claim an alibi.
- **XP per minute of connected time**, which is the only cap that does not depend on believing
  the client at all. It banks three minutes so an honest burst, clearing a crypt levels four
  skills at once, is paid in full.

Nobody is disconnected and nothing is ever taken away. Rist used to refuse the connection, and
a levelling mod deciding who may play means a bug in an XP system locks people out of a server.
Refusing at the door moved to [Dyrr](../dyrr), where it is the whole job and is done
openly.

### Cloning vanilla UI, and what it does not hand over

Both the bar and the compendium tab are clones of things the game already draws, which is what
makes them match. What a clone does **not** bring is its own lifecycle: it has never run
`Awake`, `OnEnable` or `LateUpdate`, because the donors sit inactive.

`GuiBar` puts something essential in each of those, so the bar failed three times over - the
tint reached nothing, the width was discarded and re-derived from the donor's, and setting a
value stored a number without drawing it. All three are ways of not reaching one line. The
fill is written directly now, and the trailing bar lagged by hand.

The tab had the same shape of problem read from the other end: its gold is not in any sprite
or any `Image.color` but in the Button's `normalColor`, applied to whichever graphic is its
`targetGraphic`. Building a layer alongside it could never match; swapping the sprite on the
graphic already being tinted inherits size, shadow, gold, hover and press at once.

The rule both arrive at: **clone for geometry and sprites, drive the state yourself.**

### Core is optional

Rist installs and runs on its own. Core is a **soft** dependency; installing Rist no longer
installs Core with it, and `manifest.json` no longer lists it.

Solo, nothing is given up, including the extra inventory rows, which now have a Rist-owned
implementation used only when Core is absent. On a server, three things are lost, and the
README section "Core is optional, and here is exactly what that costs" sets them out: the
`cards.txt` hash check, the host-authoritative curve, and the arbitration of
`Inventory.m_height` between mods. Rist logs a warning at startup naming all three.

The row fallback deliberately copies Core's `Player.Load` widening rather than simplifying it.
That is not a nicety: without it every item in a granted row is destroyed by loading the game,
silently, because `AddItem` drops any saved position outside the current grid and the rows are
not applied until after the load. It is patched in only when Core is absent, so the two owners
can never both write the field or both widen the grid.

Mechanically, as elsewhere in the suite: every `Ezomic.Core` call now sits in its own
`[MethodImpl(MethodImplOptions.NoInlining)]` method behind a `Chainloader.PluginInfos` check,
because the JIT resolves a method's assemblies when it first compiles that method, and an inline
call would drag Core in before the check could prevent it. Verified by decompiling the built
DLL: `Suite.*` and `InventoryRows.*` appear only inside those isolated methods.

### A character arrives worth what its skills are worth

`CreditExistingSkills`, on by default, pays a joining character for the skills it already
holds: turn up carrying skills worth twenty levels and you arrive at twenty levels with the
picks to spend. XP is the skill level reached, so a skill at N has already produced N(N+1)/2;
summing that over every skill is exactly what the character would hold had all of it happened
here, which also makes it safe to re-run on every login and impossible to double-pay.

Off, only XP gained on this server counts and every character starts at level 0. Config syncs
from the host, so a server picks one rule for everyone on it.

There is no untrusted state. A character above the baseline was briefly marked untrusted and
paid nothing "until they line up again", which it could never do, because the withholding returned
before the baseline was updated, and the login check only adopted a new baseline when it had
found nothing wrong, so the baseline froze while the player's real skills climbed away from it.

Keeping a well-travelled character out of a world altogether is a door policy, and the door is
Dyrr's.

### Known limits

- **Never played at the shipping curve.** The pick path was exercised against a deliberately
  cheapened one so it could be reached without grinding. `LevelBaseXp` and `LevelExponent` are
  back at `60` and `1.5`, and how often a stone lights up at those numbers is still a guess.
- **Runestone balance is untested by definition.** With free choice the strongest runestone is
  always available, and nothing has been played hard enough to find out which one that is.
- **Multiplayer has never had a second player.** The version gate, config sync and catalogue
  hash all work host-side and against a dev server, but no remote client has connected.
