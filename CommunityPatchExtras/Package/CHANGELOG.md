# Changelog

## 0.1.0

First release: the Extras companion to the Valheim Community Patch — the home for opt-in
tunables that deliberately go beyond the Community Patch's no-gameplay-changes rule. Hard
dependency on the Community Patch; all settings admin-only and server-synced.

- **Zone Load Radius** (default 2 = exactly vanilla) — zone rings of full objects loaded
  around each player. Each +1 hands the Community Patch's throttled spawn stream one more
  64 m of approach runway into built-up areas, at roughly double the steady-state loaded
  objects and a wider active-simulation ring per step.
- **Distant Zone Load Radius** (default 4, vanilla 2) — further rings of lightweight
  distant-flagged props, raised by default for better long-range silhouettes.
- Both values are applied only where they are authoritative: on the server, or on a client
  after the server's config sync arrives. A client on a server without this mod stays at
  vanilla — including immunity to the vanilla quirk where a client-side radius raise can
  stall object spawning outright.
