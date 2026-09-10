using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using CommunityPatchExtras.Common;

namespace CommunityPatchExtras.Patches {
    // Exposes how far from the player Valheim grows grass - ClutterSystem.m_distance (ClutterSystem.cs:99,
    // shipped 40 m), the radius of the square-spiral of 8 m clutter patches rebuilt around the camera every
    // frame. Vanilla offers no setting for it: the graphics menu's Vegetation slider only scales how MUCH
    // clutter goes in a patch (Off/Low/Med/High divide m_amount), never how far out patches are placed, so
    // grass has always stopped dead at 40 m while the terrain under it runs to the horizon.
    //
    // m_distance is read in four places, and writing the one field drives all of them:
    //   IsHeightmapReady     ClutterSystem.cs:213 - Heightmap.HaveQueuedRebuild within m_distance
    //   GeneratePatches      ClutterSystem.cs:234 - ring count, CeilToInt((m_distance - 4) / 8)
    //   GeneratePatch        ClutterSystem.cs:252 - per-patch cull, DistanceXZ > m_distance
    //   GenerateVegPatch     ClutterSystem.cs:465 - clamps each patch's instanced fade distance
    //
    // THE CEILING, which is what makes "up to the simulated zones" the right framing rather than an
    // arbitrary metre cap. Clutter is placed by raycasting the terrain collider and sampling the biome, and
    // the biome sampler short-circuits: Heightmap.FindBiomeClutter (Heightmap.cs:1322) returns Biome.None
    // the moment ZoneSystem.IsZoneLoaded (ZoneSystem.cs:1280) says no zone is loaded at that point.
    // GetPatchBiomes tests all four corners of a patch and GenerateVegPatch bails on None, so a patch
    // outside the loaded zones is not thin grass - it is no grass, retried every single frame. How far the
    // zones reach is now vanilla's own Simulation Distance setting: NearSimulationDistance rings of 64 m
    // zones around the player's own zone, square at the classic levels and circular at the rest. The radius
    // guaranteed in EVERY direction regardless of where in its zone the player stands is near * m_zoneSize
    // for a square, one ring less for a circle because the diagonal corners fall outside it - so 128 m at
    // vanilla's default level 2, and 320 m at its maximum of 6. ResolveDistance clamps to that live, which
    // is both a correctness fix and a performance one - inside the ceiling essentially no patch ever fails,
    // so the retry-every-frame path stays cold. Raising Simulation Distance therefore raises the grass
    // ceiling too, and a server capping it lower (see ZoneLoadRadiusPatch) lowers it back; the clamp is
    // re-evaluated every frame because it is two multiplies (see UpdateGrassPrefix).
    //
    // Two things have to come with the distance or the setting does not deliver what it promises:
    //
    //  1. FADE DISTANCE. Instanced clutter - which is most grass - fades out over
    //     InstanceRenderer.m_lodMinDistance..m_lodMaxDistance (5 m and 20 m on the shipped prefabs,
    //     InstanceRenderer.cs:19-21), and vanilla's line 465 only ever clamps a clone's max DOWN to
    //     m_distance - 4. Raising m_distance alone would therefore generate patches out to 256 m that
    //     still faded to nothing at 20 m: all of the cost, none of the view. So the clutter prefabs' own
    //     two fade distances are scaled by the same factor as the distance, which vanilla's own clamp then
    //     caps at the generated radius for free. Scaling both keeps the near-field density profile the
    //     game shipped instead of stretching it thin. The prefabs are shared assets that we write to, so
    //     the shipped values are captured exactly once per process and restored when the setting goes back
    //     to vanilla.
    //
    //  2. FILL RATE. GeneratePatch builds at most one new patch per frame (the ref bool generated gate at
    //     ClutterSystem.cs:262) - at 40 m that fills ~79 patches in ~1.3 s, but the count grows with the
    //     square of the radius: ~800 at 128 m, ~3200 at 256 m, which is 13 s and 54 s of visibly growing
    //     grass. Worse, the gate is skipped entirely when rebuildAll is set, and ClearAll sets it on every
    //     world load and every Vegetation quality change: vanilla would then build all ~3200 patches in a
    //     single frame, each raycasting up to m_amount times per matching clutter type. That is a
    //     multi-second freeze. So while the distance is raised, rebuildAll is forced off - which costs
    //     nothing, because with a budget the normal path fills just as fast without the stall - and the
    //     one-per-frame gate becomes a configurable Grass Patches Per Frame budget.
    //
    // Why this is client config, and the only setting in this mod that is: ClutterSystem is pure local
    // rendering. Nothing here is a ZDO, nothing is sent, nothing is negotiated, and a dedicated server's
    // ClutterSystem never draws a frame. The right value is a function of the machine's GPU and RAM, so
    // holding a player to the admin's choice would be wrong in the way that syncing a resolution would be.
    // The one part that IS server authority - how many zones are simulated - arrives through vanilla's own
    // Simulation Distance handshake, bounded by the server cap in ZoneLoadRadiusPatch, and this reads the
    // result as a ceiling rather than duplicating it.
    //
    // Knock-ons, documented rather than patched:
    //  - MEMORY IS THE REAL LIMIT, not frame time. Every instanced clutter type in every patch gets its
    //    own InstanceRenderer, and each one eagerly allocates a fixed Matrix4x4[1024] = 64 KB
    //    (InstanceRenderer.cs:31) whether it holds 1000 instances or 3. With a handful of clutter types
    //    matching a typical Meadows patch that is a few hundred KB per patch, so the ~25 MB vanilla spends
    //    at 40 m becomes a few hundred MB at 128 m and can approach a gigabyte at 256 m. This scales with
    //    the square of the distance and does not care how strong the GPU is, which is why the default is
    //    exactly vanilla and why the description says to move it in small steps.
    //  - IsHeightmapReady (line 213) now waits on every heightmap within the larger radius, so a terrain
    //    edit anywhere in it pauses ALL grass generation for the frame or two that heightmap takes to
    //    rebuild. Left alone deliberately: it is vanilla's own logic and it is right - clutter raycast
    //    onto a heightmap that is about to be regenerated is work thrown away.
    //  - Terrain edits repaint over a few frames instead of instantly, because ResetGrass marks patches
    //    and relies on the same rebuildAll that is forced off. A hoe touches a handful of patches and the
    //    budget is per frame, so this is imperceptible at any budget above 1.
    //  - Untouched outside a world. ResolveDistance returns the shipped value whenever ZoneSystem.instance
    //    is null, which leaves the main menu's background ClutterSystem exactly as vanilla drew it.
    [HarmonyPatch(typeof(ClutterSystem))]
    internal static class GrassDistancePatch {
        internal static ConfigEntry<float> Distance;
        internal static ConfigEntry<int> PatchesPerFrame;

        // ClutterSystem.m_distance as the scene shipped it. Re-read at every Awake rather than hardcoded:
        // it is scene data on a per-scene instance, so the menu background's is not necessarily the
        // world's, and an Irongate change to the shipped 40 degrades gracefully.
        private static float _vanillaDistance;
        private static bool _vanillaCaptured;

        // The clutter prefabs' shipped fade distances. Captured exactly once per process, unlike the
        // distance above: prefabs are shared assets that survive scene loads, and we write to them, so
        // re-capturing at a second Awake would record our own scaled values as the vanilla ones.
        private static readonly List<ClutterLod> Lods = new List<ClutterLod>();
        private static bool _lodsCaptured;
        private static bool _lodsScaled;

        // New patches still allowed this frame beyond vanilla's first. Reset per GeneratePatches pass.
        private static int _budget;

        internal static void BindConfig() {
            Distance = ValConfig.BindClientConfig(
                "Grass",
                "Grass Distance",
                40f,
                "How far from you, in metres, grass and other ground clutter is grown. 40 is exactly " +
                "vanilla. Raising it is capped at whatever the loaded zones can actually cover - your " +
                "Simulation Distance setting, so 128 m at its default of 2 - because clutter cannot be " +
                "placed on terrain that is not loaded. Cost grows with the SQUARE of this number, and it " +
                "is memory rather than framerate that bites first - each step up is roughly double the " +
                "grass patches of the last, so raise it a little at a time and watch RAM. Lower it below " +
                "40 to buy back performance on a weak machine. Per-machine: this is a rendering setting " +
                "and is not synced from the server.",
                false,
                16f,
                256f);

            PatchesPerFrame = ValConfig.BindClientConfig(
                "Grass",
                "Grass Patches Per Frame",
                8,
                "How many clutter patches may be built per frame while Grass Distance is raised above " +
                "vanilla. Vanilla builds 1, which is fine over its 40 m but would leave a raised radius " +
                "visibly growing in for tens of seconds. Higher fills faster after a world load or a " +
                "graphics change at the cost of a heavier frame while it does. Ignored at or below the " +
                "vanilla distance.",
                true,
                1,
                64);

            Distance.SettingChanged += (sender, args) =>
                ConfigChangeDebouncer.Schedule(typeof(GrassDistancePatch), Apply);
        }

        // The distance to actually run at: the configured value, held to the radius the loaded zones can
        // support, and pinned to vanilla entirely when there is no world (the main menu background).
        private static float ResolveDistance() {
            ZoneSystem zones = ZoneSystem.instance;
            if (zones == null || Distance == null) { return _vanillaDistance; }

            float configured = Distance.Value;

            // Lowering needs no ceiling - less than vanilla is always coverable.
            if (configured <= _vanillaDistance) { return configured; }

            // The radius loaded in every direction no matter where in its own zone the player stands.
            // ZoneSystem's own copy of the negotiated distance rather than ZNet's: this is the exact
            // value CreateLocalZones walks and IsZoneLoaded answers from, so it is what clutter
            // placement actually succeeds against. A classic (square) area guarantees the full
            // near * zoneSize; the circular shape vanilla uses at every other level clips the
            // diagonals - the worst-case corner loses most of a zone - so a ring comes off there.
            // A default-valued struct, before ZNet has applied one, yields 0 or -1 rings and so
            // falls back to vanilla below rather than inventing coverage.
            SimulationDistance simulation = zones.m_simulationDistance;
            int rings = simulation.NearSimulationDistance - (simulation.IsClassic ? 0 : 1);
            float ceiling = rings * zones.m_zoneSize;
            return Mathf.Max(_vanillaDistance, Mathf.Min(configured, ceiling));
        }

        private static void Apply() => Apply("setting changed");

        private static void Apply(string reason) {
            ClutterSystem clutter = ClutterSystem.instance;
            if (clutter == null || !_vanillaCaptured) { return; }

            float target = ResolveDistance();
            if (Mathf.Approximately(clutter.m_distance, target)) { return; }

            clutter.m_distance = target;
            ApplyLodScale(target);

            // Every live patch was built under the old radius: ones now out of range would linger for
            // their two-second timeout, and instanced ones inside it are still holding the fade distance
            // they were clamped to at build time. Dropping the lot heals both, and the budget below is
            // what keeps refilling them from stalling the frame.
            clutter.ClearAll();

            Logger.LogInfo($"Grass distance applied: {target:0.#} m (vanilla {_vanillaDistance:0.#}; {reason}).");
        }

        private static void CaptureLods(ClutterSystem clutter) {
            if (_lodsCaptured) { return; }
            _lodsCaptured = true;

            foreach (ClutterSystem.Clutter entry in clutter.m_clutter) {
                // Only instanced clutter fades: the other kind is instantiated as one real GameObject per
                // blade and left to Unity's own culling, and ClutterSystem.cs:338 is inside the instanced
                // branch, so a renderer on such a prefab would never be fed instances anyway.
                if (entry == null || !entry.m_instanced || entry.m_prefab == null) { continue; }

                // Vanilla reads the component off the root too, unguarded, in that same branch.
                InstanceRenderer renderer = entry.m_prefab.GetComponent<InstanceRenderer>();
                if (renderer == null) { continue; }

                Lods.Add(new ClutterLod {
                    Renderer = renderer,
                    Min = renderer.m_lodMinDistance,
                    Max = renderer.m_lodMaxDistance,
                });
            }
        }

        // Stretches the shipped fade distances by the same factor as the view distance, so grass reaches
        // the new radius instead of dissolving at the shipped 20 m. Always computed from the captured
        // originals rather than the live values, which keeps repeated applies from compounding.
        private static void ApplyLodScale(float distance) {
            float scale = distance / _vanillaDistance;
            bool stretch = scale > 1f;

            // Nothing to do when we are at or below vanilla and never stretched: vanilla's own clamp at
            // ClutterSystem.cs:338 already caps the clones correctly on the way down.
            if (!stretch && !_lodsScaled) { return; }
            _lodsScaled = stretch;

            foreach (ClutterLod lod in Lods) {
                if (lod.Renderer == null) { continue; }
                lod.Renderer.m_lodMinDistance = stretch ? lod.Min * scale : lod.Min;
                lod.Renderer.m_lodMaxDistance = stretch ? lod.Max * scale : lod.Max;
            }
        }

        // m_distance is freshly scene-deserialized here and nothing has written it yet.
        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(ClutterSystem __instance) {
            // Vanilla's own test for "this process draws nothing", one line above in Awake: a dedicated
            // server builds a ClutterSystem but returns before wiring any of it up, and every path below
            // would be pointless work on prefabs nobody renders.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) { return; }

            _vanillaDistance = __instance.m_distance;
            _vanillaCaptured = true;
            CaptureLods(__instance);
        }

        // Runs once per grass update, which is where the radius is kept honest. Polling rather than
        // chasing events because the ceiling moves for reasons this class does not own - ZoneSystem
        // appearing at world load, an admin editing Zone Load Radius, a server syncing its own - and
        // re-deriving it is two multiplies against a field we then compare. Apply() does nothing at all
        // when the answer has not changed, so the steady-state cost is that comparison.
        [HarmonyPrefix]
        [HarmonyPatch("UpdateGrass")]
        private static void UpdateGrassPrefix(ClutterSystem __instance, ref bool rebuildAll) {
            if (!_vanillaCaptured) { return; }

            if (!Mathf.Approximately(__instance.m_distance, ResolveDistance())) { Apply("zone coverage"); }

            // Vanilla ignores its own one-per-frame gate when this is set and builds every missing patch
            // in one frame - survivable over 40 m, a multi-second freeze over 256 m. The budget below
            // refills just as quickly without the stall, so the flag buys nothing here.
            if (__instance.m_distance > _vanillaDistance) { rebuildAll = false; }
        }

        [HarmonyPrefix]
        [HarmonyPatch("GeneratePatches")]
        private static void GeneratePatchesPrefix(ClutterSystem __instance) {
            // Minus one: vanilla's own gate already allows the first patch of the pass.
            _budget = _vanillaCaptured && PatchesPerFrame != null && __instance.m_distance > _vanillaDistance
                ? PatchesPerFrame.Value - 1
                : 0;
        }

        // Vanilla threads a single ref bool down the whole ring walk and refuses to build once it is set,
        // which is the one-patch-per-frame throttle. Clearing it again, up to the budget, is the entire
        // mechanism - it needs no transpiler and no reimplementation of the spiral, and it spends nothing
        // once the radius is full, because vanilla only ever sets the flag on a patch it actually built.
        // A patch that fails (an unloaded zone, an all-water square) leaves it false and costs no budget.
        //
        // The parameter name is load bearing: Harmony binds ref parameters by name, and getting it wrong
        // is a patch-time throw rather than a silent no-op.
        [HarmonyPrefix]
        [HarmonyPatch("GeneratePatch")]
        private static void GeneratePatchPrefix(ref bool generated) {
            if (!generated || _budget <= 0) { return; }

            _budget--;
            generated = false;
        }

        private struct ClutterLod {
            internal InstanceRenderer Renderer;
            internal float Min;
            internal float Max;
        }
    }
}
