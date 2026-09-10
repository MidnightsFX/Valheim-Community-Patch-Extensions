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
  This only ever *raises* the ceiling; a client asking for less still gets less.
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

## Installation (manual)

Drop `CommunityPatchExtras.dll` into `BepInEx/plugins`, alongside the Valheim Community Patch
and Jotunn. Install on the server and all clients; versions are checked on connect.

## Known issues

None yet. Report at https://github.com/MidnightsFX/Valheim-Community-Patch-Extensions
