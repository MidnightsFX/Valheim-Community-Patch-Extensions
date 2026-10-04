# Community Patch Extras

Opt-in world and performance tunables built on top of the
[Valheim Community Patch](https://github.com/MidnightsFX/Valheim-Community-Patch). Where the
Community Patch promises *no gameplay changes*, this companion is where the knobs live that
deliberately step past that line — each one off (or at a stated default) until an admin turns it.

**Requires the Valheim Community Patch** (hard dependency): the features here are designed
against its fixes and will not load without it. All settings are admin-only and server-synced:
the server's values win for everyone, and a client whose server does not run this mod is held at
vanilla behaviour automatically.

## Features

### Zone Loading

Valheim now ships its own **Simulation Distance** setting (graphics menu, levels 0–6) with a
proper client/server handshake: each player picks their own value and the server caps it. These
two settings fill the gaps that setting leaves rather than competing with it — the near ring is
the player's choice, as vanilla intends.

- **Server Simulation Distance Cap** *(default 2 = vanilla)* — the highest Simulation Distance
  this server will grant a client, in 64 m zone rings. Vanilla caps every client at whatever the
  *server* is set to, which on a dedicated server is 2 unless it was launched with
  `-simulationdistance` — so without this, a player who picks 5 in the menu silently gets 2.
  Raising it also gives the Community Patch's throttled spawn stream ("Spawn Burst Divisor") more
  approach runway into heavily built-up areas, so pop-in finishes before you arrive. Costs are
  real and land on the client that opts in: loaded objects scale as roughly (2n+1)², and
  creatures/spawners actively simulate in a wider ring — which is why the default changes nothing.
  This only ever *raises* the ceiling; a client asking for less still gets less. A server launched
  with `-simulationdistance` uses that as its starting point, and this cap and the distant radius
  below still apply on top of it from the moment the server starts.
- **Distant Zone Load Radius** *(default 4, vanilla 2)* — how many further rings of
  distant-flagged objects (lightweight landmark props) load beyond the simulated ring. Vanilla
  ships 2 at *every* Simulation Distance and offers no way to change it. Cheap per zone, so the
  default is raised for better long-range silhouettes.

Safety: vanilla's own handshake arbitrates the near ring, and the server is authoritative for
it. The distant ring it cannot arbitrate — Valheim only compares two simulation distances whose
far rings already match — so that value is held at vanilla until the server's config sync
confirms it is running this mod too. A client on a server without this mod stays at vanilla.

### Grass

- **Grass Distance** *(default 40 m = vanilla)* — how far from you grass and other ground clutter
  is grown. Valheim has never offered this: the graphics menu's Vegetation slider only changes how
  *much* clutter goes into a patch, never how far out patches are placed, so grass has always
  stopped dead at 40 m while the terrain under it ran to the horizon.

  The ceiling is the loaded world, not an arbitrary number. Clutter can only be placed where a
  zone is actually loaded, so this is clamped live to what your **Simulation Distance** covers —
  128 m at its default of 2, rising to 320 m at its maximum of 6. Raise Simulation Distance and
  the grass ceiling rises with it; a server capping it lower lowers it back, with no restart
  needed either way. Set a value above the ceiling and you simply get the ceiling.

  Cost grows with the **square** of the distance, and it is memory that bites first, not framerate:
  every instanced clutter type in every patch allocates a fixed 64 KB buffer regardless of how much
  grass it holds, so the ~25 MB vanilla spends at 40 m becomes a few hundred MB at 128 m and can
  approach a gigabyte at 256 m. That is why the default is exactly vanilla — raise it a little at a
  time and watch RAM, not FPS. Lowering it below 40 is a legitimate way to buy performance back on
  a weak machine.

  Two things come along for the ride so the setting actually delivers. Grass fade distances are
  scaled by the same factor, because vanilla only ever clamps them *down* and raised patches would
  otherwise still dissolve at 20 m — all of the cost, none of the view. And the fill rate is
  budgeted (below), because vanilla builds one patch per frame and rebuilds *everything* in a single
  frame after a world load or a graphics change; at a raised radius that is a multi-second freeze.
- **Grass Patches Per Frame** *(default 8, advanced)* — how fast a raised radius fills in. Vanilla's
  rate is 1, which is fine over 40 m but would leave 256 m visibly growing in for the better part of
  a minute. Higher fills faster at the cost of a heavier frame while it does. Ignored at or below
  the vanilla distance.

Unlike everything else here, these two are **per-machine and not server-synced**: clutter is pure
local rendering that Valheim never sends, validates or negotiates, so the right value is a property
of your GPU and RAM rather than of the world. The one part that *is* server authority — how many
zones are simulated — arrives through vanilla's own Simulation Distance handshake, bounded by the
server cap above, and is read as the ceiling.

### Cutscenes

Valheim plays four cutscenes at you and offers a setting for none of them. All four toggles below
default to **on**, i.e. exactly vanilla, and are per-machine rather than server-synced — a cutscene
shown to one player at one machine is not a world rule.

Skipping costs you nothing permanent: the main menu's **Cinematics** list unlocks each video from
the boss kill, world key or player stat behind it, never from having watched it, so anything you
skip is still there to watch on purpose later. The cinematics the player asks for by name — that
gallery, the menu's Credits button, and the `cinematic` console command — are untouched.

- **Play Startup Cinematic** — the pre-rendered video that covers the walk to the main menu when
  you launch the game. Off goes straight to the menu. Vanilla already plays it only once per
  launch; this makes that skip permanent.
- **Play New Character Intro** — the arrival sequence the first time a character spawns in a world:
  the raven's greeting and the valkyrie flight that drops you at the spawn stones. Off puts you on
  the ground immediately — the same thing vanilla's own Skip button in the pause menu does, just
  without having to press it every time you roll a character.

  One trade worth knowing about, on a **brand new world only**: this intro doubles as cover for
  world generation. While it is on screen the game throttles location generation to keep the intro
  smooth, and with nothing on screen to protect it stops throttling — so skipping does not skip
  that work, it moves it to after you have landed, and the first minute or two on a fresh world
  will stutter while the world finishes generating around you. Joining an already-generated world
  with a new character has no such cost. This is vanilla's behaviour behind its own Skip button,
  not something the mod adds.
- **Play Dream Cinematics** — the dream videos queued by killing a boss or offering a trophy at a
  boss stone, which play the next time you sleep. Off gives you the ordinary random dream text
  instead, exactly as a night with nothing queued does. The queue is still broadcast to every
  player and each client decides for itself, so turning this off never skips anyone else's dream.
- **Play Ending Cinematic** — the outro video and rolling credits you get from the end.

### Tamed Creatures

- **Commandable Tames** *(default on, vanilla off for most creatures)* — lets you tell **any** tamed
  creature to follow or to stay, not just the few Valheim marked for it. Vanilla allows it for
  wolves, lox and asksvins and for nothing else. Summoned minions can be told to stay too, but a
  waiting minion still counts toward its summoner's summon limit and is dismissed under the same
  rules as one following them, such as when its summoner logs out. One you walk away from simply
  waits where you left it, and counts again as soon as you are back. Server-synced.

- **Commandable Tames Exceptions** *(default empty, advanced)* — prefab names to leave exactly as
  vanilla ships them, comma separated, for when you want commandable boars but not commandable hens
  (`Hen,Chicken`). Matched ignoring case; listed creatures keep petting, and any already following
  someone is released, except summoned minions, which keep following their summoner. Server-synced.

- **Shared Summon Limit** *(default on, vanilla off)* — a staff that summons more than one kind of
  creature counts them all toward its one summon limit. Vanilla counts each kind separately, so the
  Spirit Caller, which calls a random wolf, boar, moose or bear, lets you keep a full limit of
  *each* — four times what the staff allows — while the Dead Raiser, which only raises skeletons,
  stops where it should. On, the oldest summon is dismissed first, exactly as vanilla does for
  skeletons. Modded staffs that summon several creatures are covered too. Server-synced; held at
  vanilla on a server without this mod.

### Building

- **Deconstruct Boats And Carts** *(default on, vanilla off)* — the hammer's remove click works on
  boats and carts. Vanilla refuses it, so the only way to get rid of one is to smash it. That
  already returns the full build cost, so this changes how you take a vehicle apart, not what you
  get back. Covers every boat, the cart, and the battering ram, catapult and sled, which are built
  on the same cart class.

  Refused (with vanilla's "can't remove this now" message) while anyone is pulling or riding the
  cart, or standing anywhere on the boat, not just at the helm. Anything in the vehicle's hold drops
  in a floating crate, as when it breaks. Otherwise the usual removal rules apply: wards, no-build
  zones, and a **workbench in range**, which every vehicle lists as its crafting station, the Raft
  included. So a boat moored away from base needs a workbench nearby. Server-synced; a client on a
  server without this mod keeps vanilla behaviour.

### Equipment

- **Upgradable Wisplight** *(default off = vanilla)* — the Wisplight can be upgraded at the
  workbench, up to level 4, and each level pushes the mist back further. Each upgrade costs three
  times the Wisplight's crafting materials per level, scaled by level the way every upgrade in the
  game is: 3, then 6, then 12 each of Silver and Wisps, at workbench levels 2, 3 and 4. Upgrading
  unequips it, as it does any item, so put it back on to get the new reach.

  Stacks with anything else that widens the Wisplight's reach, such as Epic Loot's Demisting
  enchantment: the two multiply. Other players see the mist cleared as far as you do, and, as with
  any Wisplight, creatures can see you through the cleared mist too. Turning this off leaves
  upgraded Wisplights at their level but back at vanilla reach. Server-synced; a client on a server
  without this mod keeps vanilla behaviour.
- **Wisplight Range Per Level** *(default 30%)* — how much further each upgrade pushes the mist
  back, as a share of the Wisplight's reach before upgrading: at 30%, levels 2, 3 and 4 clear 30%,
  60% and 90% further. Takes effect the next time the Wisplight is equipped. Server-synced.

### Traders

- **Hildir Buys Spare Chests** *(default on, vanilla off)* — Hildir will buy extra copies of her
  three quest chests. In vanilla a second chest is dead weight: she only tells you she already has
  it, no trader will buy it, and it cannot go through a portal. Each of her dungeons is placed up to
  three times per world, and every one drops its chest, so spares turn up, especially on a server.

  The first chest of each kind still has to be **given** to her. A chest only becomes sellable once
  that same chest has been handed in, so selling can never take the place of the quest or cost you
  her unlocks. Each chest counts separately: handing in the Brass Chest makes spare Brass Chests
  sellable, and the other two stay quest items until they are handed in too. Sales go through the
  ordinary sell button, after any gems or other valuables you are carrying. Server-synced; a client
  on a server without this mod keeps vanilla behaviour.
- **Brass Chest Price** *(default 500)* — coins paid per spare Hildir's Brass Chest, from the
  Smouldering Tomb (Black Forest). Server-synced.
- **Silver Chest Price** *(default 700)* — coins paid per spare Hildir's Silver Chest, from the
  Howling Cavern (Mountains). Server-synced.
- **Bronze Chest Price** *(default 900)* — coins paid per spare Hildir's Bronze Chest, from the
  Sealed Tower (Plains). Server-synced.

  For scale, the most a trader pays for any vanilla valuable is 175.

### Bosses

- **Limit Boss Announcements** *(default on, vanilla off)* — a boss's centre-screen announcements,
  when it is summoned, when it wakes and when it is defeated, are shown only to players near it.
  Vanilla shows them to every player on the server, so anyone building at home gets a banner for
  every boss fight anywhere in the world. Covers every vanilla boss, from Eikthyr's summoning to the
  Queen, Fader and the Frozen King waking in their rooms, and any modded creature that announces
  itself the same way.

  The server decides who is near, from where each player last reported being, and delivers the
  message through vanilla's own channel, so a player whose client lacks this mod still sees it when
  they are in range. Distance is measured across the map and ignores height: dungeon interiors sit
  far above their entrance, and a party waiting at the door should still see what happens inside.
  Server-synced; a client on a server without this mod keeps vanilla behaviour.
- **Boss Announcement Range** *(default 300 m)* — how close to the boss a player must be to see its
  announcements. The default matches the Community Patch's **Boss Defeat Key Range**, so everyone
  credited with a kill also sees it announced. 0 shows them to nobody. Server-synced.

### HUD & Notifications

In native Valheim, Game.UpdateSaving broadcasts a 30-second autosave countdown warning ($msg_worldsavewarning 30s) with MessageHud.MessageType.Center. This displays a giant, screen-filling yellow banner directly across the player's crosshair and combat view. Many dedicated servers and administrative tools broadcast similar countdowns and save confirmations to the center of the screen as well.

During combat, boss encounters, sailing, or precise building, having the center of the screen obscured by save alerts is disruptive and hazardous.

- **World Save Notice Mode** *(default Native)* - controls how world save notifications are displayed on the HUD:
  - Native *(default)*: Preserves the game's default behavior (large center-screen toast).
  - RelocateToTopLeft: Demotes the center-screen save warnings and completion notices to the subtle top-left corner notification feed, keeping you informed without blinding your crosshair.
  - Mute: Completely silences the center save notifications.

Per-machine presentation setting: not synced from the server, and inert on dedicated servers where HUD messages are not rendered.

### Accessibility

- **Disable Heat Distortion** *(default off = vanilla)* — turns off the wavy full-screen heat effect
  the Ashlands puts over your view when you stand near lava, wade in its sea, or walk under its
  daytime sun. The moving image makes some players motion sick, and Valheim's own Accessibility
  settings have no option for it. Only the screen effect goes: heat still builds up and burns you
  exactly as in vanilla, and the heat particles and sound around your character still play, so you
  can still tell when you are overheating.

Per-machine: this only affects your own screen and is not synced from the server.

## Installation (manual)

Drop `CommunityPatchExtras.dll` into `BepInEx/plugins`, alongside the Valheim Community Patch
and Jotunn. Install on the server and all clients; versions are checked on connect.

## Known issues

None yet. Report at https://github.com/MidnightsFX/Valheim-Community-Patch-Extensions
