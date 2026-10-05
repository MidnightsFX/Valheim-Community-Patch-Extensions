using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;
using CommunityPatchExtras.Common;

namespace CommunityPatchExtras.Patches {
    // Lowers the waves over shallow water near shore, in both the physics and the rendered sea, so a
    // boat still sits on the waves the player sees. Open ocean is untouched. A deliberate gameplay
    // change, which is why it lives here and not in the Community Patch.
    //
    // WHERE WAVE HEIGHT COMES FROM. Each water tile is a WaterVolume, one per zone. Its Start
    // (WaterVolume.cs:83) runs DetectWaterDepth and then SetupMaterial, and nothing runs either again.
    //  - DetectWaterDepth (WaterVolume.cs:99) reads the tile's four corner ocean depths from its
    //    Heightmap and stores Clamp01(depth / 10 m) in m_normalizedDepth (0 NW, 1 NE, 2 SE, 3 SW).
    //    When all four are equal it also sets m_oneDepth, a shortcut Depth uses instead of blending.
    //    Tiles without a heightmap (caves, hot tubs) use m_forceDepth instead.
    //  - SetupMaterial (WaterVolume.cs:149) copies m_normalizedDepth into the water material's _depth
    //    array. The shader's wave height is that array blended across the tile, times the wind.
    //  - Physics reads the same array: Depth (WaterVolume.cs:231) blends it (or returns m_oneDepth),
    //    and CalcWave (WaterVolume.cs:197) scales the wave by Lerp(0, wind, depth), the same formula
    //    as the shader. Everything that floats or swims goes through GetWaterSurface, which calls
    //    both: Floating, the five hull samples Ship uses for buoyancy, tilt and water-impact damage,
    //    the swim level of every Character, and the Leviathan. Fish call Depth and CalcWave directly.
    //
    // WHY REMAP THE DEPTH, NOT THE WAVE. A postfix on DetectWaterDepth rewrites m_normalizedDepth
    // and m_oneDepth once per tile, before SetupMaterial copies them, so the physics, the fish and
    // the rendered waves all read the same remapped values and cannot drift apart. Scaling
    // GetWaterSurface or CalcWave instead would change the physics alone and leave the drawn waves
    // taller than the ones boats ride, which is the very mismatch the Community Patch 0.32.4 fixed
    // by no longer writing _depth itself.
    //
    // THE CURVE. Each corner d becomes d * Lerp(s, 1, d), where s is Shore Wave Height:
    //  - f(1) = 1, so water 10 m deep or more is vanilla, and f(0) = 0, so dry land stays still.
    //  - It is applied to each shared corner value, so two neighbouring tiles still agree along
    //    their common edge and no seam appears.
    //  - It only ever lowers a value and keeps the order of depths, so a deeper corner still has the
    //    taller waves.
    // At 0.6: 1 m deep is 36% lower, 2.5 m 30%, 5 m 20%, 7.5 m 10%, 10 m and beyond unchanged.
    //
    // SIDE EFFECT ON THE LOOK. The water shader also uses _depth for the shallow-to-deep color blend,
    // wind foam and ripple strength, so lowering it makes shore water a little sandier and calmer
    // looking. The Community Patch's Water Color Shore Tint already pulls the shallow color most of
    // the way to the deep one, which hides most of the color shift.
    //
    // LIVE CHANGES. ApplyToLoaded re-runs DetectWaterDepth on every loaded tile, which re-reads the
    // heightmap and so passes back through the postfix at the new value, then writes the result to
    // the material directly. Not SetupMaterial, which other mods postfix (the Community Patch's
    // WaterColorSeamPatch for one) and which also sets the global wind flag. m_oneDepth is reset
    // first because vanilla only ever sets it, never clears it.
    //
    // SERVER-SYNCED, and held at vanilla on a server that does not run this mod, the same way as
    // UpgradableWisplightPatch. Every machine has to agree: whichever machine owns a ship or a
    // floating item simulates it, a dedicated server included, and everyone else draws the waves it
    // sits on. A client therefore stays at vanilla until Jotunn confirms the server's config for
    // this session, then re-applies to the tiles it already has. The main menu's sea is always vanilla.
    [HarmonyPatch(typeof(WaterVolume))]
    internal static class ShoreWaveHeightPatch {
        internal static ConfigEntry<float> ShoreWaveHeight;

        // The ZNet instance whose session Jotunn confirmed this mod's config for. Identity-compared so
        // a new session invalidates it.
        private static ZNet _confirmedFor;

        internal static void BindConfig() {
            ShoreWaveHeight = ValConfig.BindServerConfig(
                "Water",
                "Shore Wave Height",
                0.6f,
                "How tall waves are over shallow water near shore, compared with vanilla. 1 is " +
                "vanilla. Lower values calm the waves the closer the water is to the beach, while " +
                "water about 10 m deep or more keeps its full waves, so the open sea is unchanged. " +
                "The default of 0.6 is a slight reduction: about a third lower in 1 m of water and a " +
                "fifth lower in 5 m. Boats, swimmers, floating items and fish ride exactly the waves that are drawn. " +
                "Shallow water also looks a little sandier and calmer at lower values, because the " +
                "game uses the same depth for its color and foam. Server-synced; a client on a " +
                "server without this mod keeps vanilla waves.",
                false,
                0f,
                1f);

            ShoreWaveHeight.SettingChanged += (sender, args) => ScheduleApply();
            SynchronizationManager.OnConfigurationSynchronized += OnConfigurationSynchronized;
        }

        private static void ScheduleApply() =>
            ConfigChangeDebouncer.Schedule(typeof(ShoreWaveHeightPatch), ApplyToLoaded);

        // Also re-applies: with Config Apply Delay at 0, the sync's own SettingChanged runs before
        // this event and so applies while the client is still unconfirmed, at vanilla.
        private static void OnConfigurationSynchronized(object sender, ConfigurationSynchronizationEventArgs args) {
            if (args.UpdatedPluginGUIDs == null || !args.UpdatedPluginGUIDs.Contains(CommunityPatchExtras.PluginGUID)) { return; }

            if (ZNet.instance != null && !ZNet.instance.IsServer()) { _confirmedFor = ZNet.instance; }
            ScheduleApply();
        }

        // The s in d * Lerp(s, 1, d), or 1 wherever this process must stay at vanilla.
        private static float ActiveScale() {
            if (ShoreWaveHeight == null || ShoreWaveHeight.Value >= 1f) { return 1f; }

            ZNet znet = ZNet.instance;
            if (znet == null) { return 1f; }
            if (!znet.IsServer() && !ReferenceEquals(_confirmedFor, znet)) { return 1f; }

            return ShoreWaveHeight.Value;
        }

        [HarmonyPostfix]
        [HarmonyPatch("DetectWaterDepth")]
        private static void DetectWaterDepthPostfix(WaterVolume __instance) {
            // The same split SetupMaterial makes: a forced depth is what the material gets, so the
            // heightmap values must stay as vanilla left them or physics would part from the render.
            if (__instance.m_heightmap == null || __instance.m_forceDepth >= 0f) { return; }

            float scale = ActiveScale();
            if (scale >= 1f) { return; }

            float[] depth = __instance.m_normalizedDepth;
            for (int i = 0; i < depth.Length; i++) {
                depth[i] *= Mathf.Lerp(scale, 1f, depth[i]);
            }

            // Vanilla's own rule, applied after the remap. Equal corners stay equal, so this sets the
            // shortcut exactly when vanilla just did, and leaves it alone otherwise.
            if (depth[0] == depth[1] && depth[0] == depth[2] && depth[0] == depth[3]) {
                __instance.m_oneDepth = depth[0];
            }
        }

        private static void ApplyToLoaded() {
            int count = 0;
            foreach (WaterVolume volume in WaterVolume.Instances) {
                if (volume == null || volume.m_heightmap == null || volume.m_forceDepth >= 0f) { continue; }

                volume.m_oneDepth = -1f;
                volume.DetectWaterDepth();
                if (volume.m_waterSurface != null) {
                    volume.m_waterSurface.material.SetFloatArray(WaterVolume.s_shaderDepth, volume.m_normalizedDepth);
                }
                count++;
            }

            Logger.LogDebug($"Shore wave height {ActiveScale():0.##} applied to {count} loaded water tile(s).");
        }
    }
}
