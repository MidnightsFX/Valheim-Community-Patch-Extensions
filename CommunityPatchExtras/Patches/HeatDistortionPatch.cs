using BepInEx.Configuration;
using HarmonyLib;

namespace CommunityPatchExtras.Patches {
    // A switch for the Ashlands' full-screen heat ripple, which makes some players motion sick.
    // Vanilla's own Accessibility tab covers camera shake, ship camera tilt and flashing lights
    // (GameCamera.ApplySettings, Settings.ReduceFlashingLights) and has nothing for this one.
    //
    // WHAT THE EFFECT IS. HeatDistortImageEffect is a component on the game camera
    // (GameCamera.cs:144) whose OnRenderImage blits the whole frame through the
    // Hidden/CameraHeatDistort shader, which warps the image with a scrolling noise texture.
    // Exactly one thing ever drives it: Character.UpdateHeatEffects
    // (Character.cs:1030), which for the local player only, every fixed tick, enables it whenever
    // the player's heat is above zero and sets its intensity to that heat. The heat itself comes
    // from three places, all in the Ashlands: standing near or over lava (UpdateLava), being out
    // in the daytime sun without enough heat resistance (the same method, line 993), and wading or
    // swimming in the Ashlands sea (UpdateAshlandsWater).
    //
    // WHY A POSTFIX THAT SWITCHES THE COMPONENT OFF. Vanilla rewrites `enabled` on every call, so a
    // postfix on the same method gets the last word every tick, and the camera does not render in
    // between. Switching the component off is also the one cut that is certain to remove all of
    // the motion: Unity skips OnRenderImage on a disabled component, so the shader never runs. The
    // gentler alternative, zeroing the shader's _DistortionStrength and leaving its edge tint as a
    // warning, depends on what that compiled shader does with its other inputs, and a setting that
    // exists for nausea should not leave anything moving on a guess.
    //
    // Nothing else about heat changes. The heat level, the damage it does, the Burning status, and
    // the heat particles and sound that UpdateHeatEffects plays around the character after this
    // (Character.cs:1032 onward) are all untouched, so the player still has a cue that they are
    // overheating - just not a screen that moves.
    //
    // No SettingChanged plumbing: turning the setting on takes effect on the next fixed tick, and
    // turning it off hands `enabled` straight back to vanilla's own write on that same tick.
    //
    // Client config, because this is a single player's own screen. No ZDO, RPC or world state is
    // involved, and a dedicated server has no local player and no camera to draw.
    [HarmonyPatch(typeof(Character))]
    internal static class HeatDistortionPatch {
        internal static ConfigEntry<bool> DisableHeatDistortion;

        internal static void BindConfig() {
            DisableHeatDistortion = ValConfig.BindClientConfig(
                "Accessibility",
                "Disable Heat Distortion",
                false,
                "Turns off the wavy full-screen heat effect you get in the Ashlands when you stand " +
                "near lava, wade in the Ashlands sea, or walk under its daytime sun. Some players " +
                "find the moving image nauseating. Only the screen effect is removed: heat still " +
                "builds up and hurts exactly as in vanilla, and the heat particles and sound around " +
                "your character still play, so you can still tell when you are overheating. " +
                "Per-machine: this only affects your own screen and is not synced from the server.");
        }

        // Runs for every Character every fixed tick, so the config is read before anything else:
        // with the setting off, as it is by default, this costs a single bool check.
        [HarmonyPostfix]
        [HarmonyPatch("UpdateHeatEffects")]
        private static void UpdateHeatEffectsPostfix(Character __instance) {
            if (DisableHeatDistortion == null || !DisableHeatDistortion.Value) { return; }

            // The same filter vanilla applies before it touches the camera.
            if (Player.m_localPlayer != __instance) { return; }

            GameCamera camera = GameCamera.instance;
            if (camera == null || camera.m_heatDistortImageEffect == null) { return; }

            camera.m_heatDistortImageEffect.enabled = false;
        }
    }
}
