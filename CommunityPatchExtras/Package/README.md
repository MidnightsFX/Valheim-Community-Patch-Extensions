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

- **Zone Load Radius** *(default 2 = vanilla)* — how many 64 m zone rings of full objects load
  around each player. Each +1 gives the Community Patch's throttled spawn stream ("Spawn Burst
  Divisor") one more zone of approach runway into heavily built-up areas, so pop-in finishes
  before you arrive instead of while you watch. Costs are real: steady-state loaded objects
  roughly double per +1, and creatures/spawners actively simulate in a wider ring — which is why
  the default stays exactly vanilla.
- **Distant Zone Load Radius** *(default 4, vanilla 2)* — how many further rings of
  distant-flagged objects (lightweight landmark props) load beyond the full ring. Cheap per
  zone, so the default is raised for better long-range silhouettes.

Safety: the server's radii are the only truth in Valheim's protocol. This mod only applies
configured values on the server (or a client the server has synced to); an unconfirmed client is
pinned to the scene's own vanilla values, which avoids the vanilla failure mode where a
client-side raise stalls object spawning entirely.

## Installation (manual)

Drop `CommunityPatchExtras.dll` into `BepInEx/plugins`, alongside the Valheim Community Patch
and Jotunn. Install on the server and all clients; versions are checked on connect.

## Known issues

None yet. Report at https://github.com/MidnightsFX/Valheim-Community-Patch-Extensions
