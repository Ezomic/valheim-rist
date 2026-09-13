# Rist design notes

Why it works the way it does, and how it is built. None of this is needed to play; for that
see the [README](README.md).

## It used to deal three at random

The first version dealt three runestones per level, seeded so that quitting on a bad offer and
coming back re-offered the same three, since otherwise the pick is theatre, since anyone can reroll
until they get what they wanted.

Choosing freely deletes that whole problem rather than defending against it. There is no roll
to reroll, no offer to store in the ledger or on the wire, and no second window with its own
visibility rules. What it costs is the tension of a hand you are dealt: a free choice means the
strongest runestone is always available, so balance now has to live in the runestones themselves and in
`MaxRank` rather than in whether one happened to come up.

The panel earns its keep here. Choosing between the runestones is only a real choice if each
one says what it is worth now **and** what the pick would make it. The tile says the first and
the column beside the field says the second, for whichever stone the cursor is on.

### Why ættir

The field was one even grid a set number of columns across, on a board 1120px wide whatever the
screen. Every runestone added height, and at twenty-one it ran off the bottom of a 2560x1009
window with eight more designed. Three ways out were drawn and compared: the same field with its
column count worked out from the screen, a quieter field of stones and names only, and the stones
grouped by theme. The grouping won.

It solves the height problem sideways. An ætt is a tower two wide and up to four tall, towers stand side
by side, and a new theme is a new tower rather than a new row - a short, wide window has width to
spare and no height. It also makes a stone findable by what it is for, which a field in catalogue
order never was. Eight is the cap, the way the futhark is cut into ættir of eight.

What it costs is written into the catalogue: every runestone has to belong to one ætt, a ninth in
one ætt becomes a second tower of the same name, and a theme with two stones in it still costs a
whole tower of width. The headings live in `cards.txt`, which is part of what Core's gate compares,
so regrouping is a paired client and server change.

Nothing about size is fixed any more. The panel walks a ladder - full tiles first, towers
standing and then each ætt lying four wide in bands, at 100px stones and then 78; then tiles
without their value line, standing and then lying, at 100, 78 and 64 - and draws the first that
fits. A scroll view was considered for the smallest screens and left out: the panel
has not scrolled since the stone field replaced the tile board, and on the screens that matter
every stone is visible at rest. If nothing fits, the smallest layout draws anyway and the log says
so.

## Why rists, and why capacity is the bottom of one

This started as an inventory problem. Late-biome kit eats the grid - armour, a weapon, a bow,
ammo, five tools, a torch, food, mead - and there is very little left for what you went out to
fetch.

The cheap fix is to add rows. `Inventory.m_height` is one int and `InventoryGrid` rebuilds
itself when it changes, so more space is a twenty-line mod. That is also exactly the answer
[Hoard](../hoard) argues against: the mid game is a logistics problem, and the tedium and the
difficulty are the same mechanic seen from two sides.

So capacity is earned, and it is a **capstone** rather than a rist of its own. It was a rist
once, called Deep pack, and it was the strongest thing in the catalogue - taken first every
time, which is not a choice. A row now sits at the bottom of Ox-backed and of Sure hand: the
reward for taking a hauling rist the whole way.

## What a level is made of

XP is skill level-ups and nothing else, which keeps the mod out of the business of maintaining
a table of what activities count. But "a skill level-up" is not one thing, and treating it as
one is what the first year of this mod got wrong.

XP is weighted by the **level reached**. Vanilla's skill cost curve is `pow(level+1, 1.5)`, so
early levels are nearly free; a flat rate per level-up would rain runestones in the first hours
and dry up exactly when the deep ranks start to matter, and it would make grinding a fresh
cheap skill from zero the fastest way to farm picks.

That much was right and is unchanged. What it missed is that the two are not independent. The
live server's ledger is what said so - a character sitting at Rist level 12 held 1346 XP:

| Skill | Level | XP | Share |
| --- | --- | --- | --- |
| WoodCutting | 36 | 666 | 49% |
| Run | 19 | 190 | 14% |
| Spears | 19 | 190 | 14% |
| Crafting | 14 | 105 | 8% |
| Jump | 11 | 66 | 5% |
| Axes | 11 | 66 | 5% |
| Clubs | 8 | 36 | 3% |
| the rest | ≤5 | 27 | 2% |

Half the character came from felling trees and under a quarter of it from being in a fight.
The level-reached weighting had compounded the wrong way: the safest repeatable activity in
the game is also the one that climbs highest, so it was collecting the most level-ups **and**
the most XP per level-up. Chopping is not nothing, but it should not be the majority of who a
character is.

### Why the curve could not fix it

`LevelBaseXp` and `LevelExponent` are the obvious reach and they cannot reach this. Both scale
the whole curve, so the *ratio* survives them untouched - a character made of trees stays a
character made of trees, just a slower one - and every hour of the late game pays for a
problem that lives in the first. That is not a prediction. The pair was `60` and `1.5`, it
asked for sixteen skill level-ups per runestone by level 20, the reward simply stopped
arriving, and it had to be walked back to `40` and `1.4`.

So the price goes on the income rather than the curve. `SkillWeights` is a multiplier per
skill, defaulting to full price for a skill raised by something that can kill you, half for
situational, a quarter for repetition that cannot hurt you. The same character re-prices to
level 6, 53% of it earned fighting, WoodCutting down to 30% - still the largest single line,
because level 36 genuinely is a lot of work, but no longer the story.

### Re-pricing, and why it is a stamp rather than a switch

Weights only affect new XP, so changing them would leave everyone already playing holding a
level bought at the old prices. `WeightGeneration` recomputes: bump it to any different text
and each character is re-priced once, on its next login.

The recompute is **exact** rather than an estimate, and that falls out of the design rather
than being arranged. XP is granted per level-up weighted by the level reached and by the
skill, so a skill at N has produced precisely `weight × N(N+1)/2` - and the server already
keeps the highest level it has watched every skill reach, for anti-forgery reasons that have
nothing to do with this. Summing over that snapshot reproduces the ledger to the XP. It is
literally the same function that credits a joining character, shared deliberately: if
crediting priced a character higher than re-pricing does, the next login would silently undo
every re-price and the mod would look like it forgot.

It reads that server-side snapshot rather than the character's current skills for two reasons.
The snapshot cannot be talked down by a client reporting itself lower, and it is monotonic, so
a death penalty landing between the old pricing and the new one does not read as levels that
were never earned.

A stamp rather than a switch because this is the **only** operation allowed to move XP
downward. The routine per-login credit is one-way on purpose - it must never take levels away,
or a skill lost to a death penalty would cost a runestone - so re-pricing cannot be folded
into it, and needs a record of having run rather than re-flooring a character every login.
That record is a field on the ledger line, which is what took the ledger to `v4`.

**Runestones already taken are kept.** `Owed` already floors at zero, so a character re-priced
from 12 to 6 holds all twelve and earns no new pick until it passes twelve again. Handing them
back instead would turn a re-pricing into a free rebuild of the whole character, which is a
different and much larger thing to do to someone.

## The bar

The experience bar is a **clone of one of the game's own upright bars**, the eitr bar, with
the stamina bar behind it as a fallback donor.

The first version drew two flat rectangles in IMGUI. It sat in the right place and still read
as a mod, because a vanilla bar is not a rectangle: it carries a frame sprite, a bevelled
inner track, softened ends, and a second fill behind the first that lags a change. None of
that survives being approximated with a 1×1 texture. Cloning the real thing inherits all of
it at once, the same argument as borrowing a material rather than authoring one.

`GuiBar` only ever resizes its fill on `RectTransform.Axis.Horizontal`, so an upright vanilla
bar is a **rotated horizontal one**. That is why the bar is cloned rather than built: the
geometry cannot be reproduced without it.

What the clone gives for free:

- the frame, track and fill sprites, at whatever HUD scale is set
- the fast/slow pair, so a level-up **drains** rather than snapping to empty
- the fade the bar already uses to show and hide itself
- the flash it already has, fired every few seconds while a runestone is waiting
- a TextMeshPro number in the game's own font, reused for the level

Two things are read off components rather than off child names, because names are scene data
and would be a guess: the trailing bar is the one with `m_smoothDrain` set, and the animator
is driven only through parameters it is confirmed to have.

Position is **screen pixels**, not the donor's `anchoredPosition`, because `Hud.UpdateEitr` rewrites
that every frame `(0,130)`, or `(0,285)` with the build HUD up, so it is never a stable thing
to copy. Being pinned in screen space means the bar also has to make vanilla's own
build-panel dodge by hand, which is what `BarBuildRaise` is.

The old IMGUI bar is kept as the **automatic fallback**. A HUD hierarchy is scene data rather
than API, so this can break under a game update in a way ilspy cannot warn about, and falling
back to a bar that certainly draws beats falling back to an empty corner.

### What the clone does not give: its own lifecycle

The bar took four rounds to get right, and every fault had one cause. A cloned component has
**never run its own `Awake`, `OnEnable` or `LateUpdate`** - the eitr donor sits inactive for a
character with no eitr - and `GuiBar` puts something essential in each of them:

| Where | What it does | What went wrong |
| --- | --- | --- |
| `Awake` | caches `m_barImage` | `SetColor` returns silently, so the bar wore the donor's own colour |
| first `SetValue` | re-reads `m_width` off the fill's current size | `SetWidth(240)` was discarded, so 43% drew as 43% of the donor's 64 |
| `LateUpdate` | the only caller of `SetBar` | later `SetValue`s stored a number and drew nothing |

Each is a different way of not reaching `SetBar`, which is one line:

```csharp
m_bar.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, m_width * i);
```

So the fill is written directly and the trailing bar lagged by hand. Nothing is cached and
nothing waits on a callback that is not coming.

The lesson generalises past this bar, and it is not "do not clone": **clone a HUD object for
its geometry and its sprites, then drive its state yourself.** The frame, the bevelled track,
the softened ends and the rotation are all worth inheriting and none are worth rebuilding. It
is only the value machinery that assumes it is running on a live scene object.

The middle fault is also a small lesson in reading a symptom. The fill was authored **magenta**
(`1, 0.294, 0.939`) under a greyscale sprite, so a tint that reached nothing did not look
untinted - it looked deliberately pink, which reads as a shader or colour-space fault and sends
you looking in entirely the wrong place.

## Where progress lives, and why it is not on your character

**On the server, keyed by the platform identity of the connection and the character being played.**

This is the load-bearing decision. Valheim keeps characters entirely client-side. Even
`ZNet.SaveOtherPlayerProfiles` only sends each client an RPC telling it to save its own
profile locally, so the server never holds a character file. Anything stored on the character
is therefore editable by its owner, which rules it out for anything that decides rewards.

Half the key is `ISocket.GetHostName()`, read server-side from the peer's own socket. That
identity is established by the platform before any of this code runs, so a client cannot claim
someone else's record. `Player.GetPlayerID()` was the obvious alternative and is wrong on its
own: it arrives from the client and could name anyone.

It takes both halves, and each one alone was tried. The platform identity is per *account*, so
every character on one machine shared a record and a remade character inherited the last one's
level and runestones - which is exactly what happened the first time a character was remade.
The character id alone is client-supplied. Together, the platform half fences a player into
their own account's records and the character half separates their characters within it.

The client's half of the protocol is deliberately thin. It reports **that a skill went up**
and **which runestone it wants**. It never sends a level, an XP total or a rank, because the server
does not store anything the client says, only re-derives from the events it claims.

### Singleplayer

Works, with no special casing. Valheim runs a local server in singleplayer, and `ZRoutedRpc`
handles a message addressed to yourself locally rather than putting it on a socket:

```csharp
if (targetPeerID == m_id || targetPeerID == 0L) HandleRoutedRPC(data);
```

Since `GetServerPeerID()` returns your own id when you are the server, skill reports, picks and
state pushes all loop straight back and resolve against the local ledger. Progress is stored
under the owner `localhost@<character id>`.

The only gap is that the host has no peer entry for itself, so nothing greets it on spawn,
hence a one-shot seed of the opening state. Everything after arrives by the same path a
client's would.

The gate is pointless here and effectively inert: on your own machine you are the one it would
be protecting you from.

## The gate, and how small a claim it makes

Rist used to refuse the connection. A character that had ever spawned in another world was
sent `ConnectionStatus.ErrorKicked` and dropped, on the reasoning that a character which has
only been here cannot have been levelled elsewhere.

That was the wrong power for this mod to hold. A levelling mod deciding who may play means a
bug in an XP system locks people out of the server, and it did: the player got Valheim's
generic kick screen with the reason only in the server's log, so a refusal and a crash looked
identical from their side. The check itself was also stricter than it sounded: "has this
character been anywhere else" refuses a character on every world but its first, permanently,
because `m_worldData` entries are never removed. Visiting a friend's world once cost you this
server for good.

That whole judgement now lives in [Dyrr](../dyrr), where turning people away is the
entire job and is done openly with its own message. What is left here is strictly about
payment, and it is a much smaller claim: **this server decides what it is willing to pay for,
not who may play.**

### What a character arrives worth

The level is meant to sit **beside** the skills, so a character that turns up carrying skills
worth twenty levels arrives at twenty levels, with the picks to spend. `CreditExistingSkills`
is on by default and does exactly that.

The arithmetic is not an estimate. XP is granted per skill level-up weighted by the level
reached and by the skill, so a skill sitting at N has already produced
`weight × (1 + 2 + … + N)`, which is `weight × N(N+1)/2`. Summing that over every skill gives
precisely the XP the character would hold if every one of those level-ups had been watched
from here, at the weights standing now.

That exactness is what makes it safe to run on **every** login rather than once: a character
that earned everything here computes the total it already has, so the credit is zero and
nothing is paid twice. It only ever raises, so a skill lost to a death penalty cannot take
levels away.

Turn it **off** and the opposite rule applies: only XP gained on this server counts, and every
character starts at Rist level 0 no matter what it is carrying. That is the stricter setting
and it is a per-server one: config syncs from the host, so whichever a server picks applies to
everyone on it.

| `CreditExistingSkills` | what a joining character gets |
| --- | --- |
| `true` (default) | the levels its skills are already worth |
| `false` | nothing until it levels a skill here |

What crediting costs is worth stating plainly: a maxed vanilla character walks in at Rist level
219 and takes the whole catalogue. If that is not wanted, the answer is not to withhold XP from
it, it is to not let it in. That is a **door policy**, and the door is
[Dyrr](../dyrr)'s: it reads `m_worldData` and `m_usedCheats` off the profile and
turns the connection away openly, which is the whole of its job.

**There is no untrusted state**, and the removal is worth recording. A character above the
baseline used to be marked untrusted and paid nothing "until they line up again". It never
could: the withholding returned before the baseline was updated, and the login comparison only
adopted a new baseline when it had found nothing wrong, so the baseline froze at the moment of
judgement while the player's real skills climbed away from it. A penalty with no exit is worse
than no penalty, and it was aimed at something a door handles better.

The baseline itself is still kept, for a reason unrelated to trust: `Throttle.Step` needs it to
tell a plausible next level from a forged one.

### Three ceilings, because a report cannot be verified

Skills live on the client. `Player.OnSkillLevelup` fires there, so a skill-up report is a claim
about something the server never saw, and no amount of checking makes it otherwise. What is
possible is bounding what a claim can be worth, and there are exactly three levers:

| Ceiling | Bounds |
| --- | --- |
| `MaxSkillUpsPerMinute` | how many claims are accepted |
| `MaxSkillLevelJump` | how much one claim may say |
| `MaxXpPerMinute` + `XpBurst` | how much anything can be worth over time |

The first alone was not enough, and the gap is arithmetic: XP is the skill level reached, so
thirty reports a minute each naming a level-100 skill was worth 3,000 XP, a character level
every ten seconds, and each of those forged levels was adopted into the baseline, giving the
next claim an alibi.

`MaxSkillLevelJump` closes that by refusing anything more than one level above the baseline,
which is what the game itself does: `Skills.Skill.Raise` levels one at a time and fires one
callback each. It needs a complete login baseline to mean anything, so it is enforced only for
characters this world has fully seen, since an absent baseline entry is otherwise indistinguishable
from a skill that has genuinely never been used.

`MaxXpPerMinute` is the only cap that does not depend on believing the client at all. However
convincing a report is, time connected is a quantity the server measures for itself. It is a
token bucket rather than a tripwire, banking three minutes by default, so an honest burst,
clearing a crypt levels four skills at once, is still paid in full.

### What this does not do

None of it detects a cheat; it bounds one. A purpose-built client can still claim a plausible
level-up it did not earn, at the honest rate, forever. What the ceilings guarantee is that
doing so is no faster than playing, which is the only property worth having here, and it is
the reason the whole thing withholds rather than punishes. A false positive costs a player
some XP and says so on screen, not their seat on the server.
