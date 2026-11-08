using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace CommunityPatchExtras.Patches {
    // Keeps a boss fight's music playing for the whole fight when the game's Continuous Music
    // setting is off. Vanilla, with that setting off, plays the boss track once and then leaves the
    // rest of the fight silent for about two minutes before it may start again.
    //
    // HOW BOSS MUSIC PLAYS. A boss carries a Character.m_bossEvent naming a RandomEvent
    // (boss_eikthyr, boss_queen, ...). While that boss's health bar is on screen,
    // RandEventSystem.GetForcedEvent picks the event up (re-checked every two seconds), it becomes
    // the active event, and MusicMan.HandleEventMusic asks for its m_forceMusic every frame. Every
    // boss track ships with m_loop and m_resume set (checked against the Deep North rip's
    // _AudioManager), so with Continuous Music on it simply loops until the fight ends.
    //
    // WHAT THE SETTING DOES TO IT. Settings.ContinousMusic is read in exactly two places that
    // matter here, and both are in MusicMan:
    //  - UpdateMusic: once the playing clip is within its fade time of the end, StopMusic(). The
    //    AudioSource would have looped, but it is stopped first.
    //  - StartMusic(NamedMusic): the track that was last started may not start again until
    //    m_resetMusicTimer runs out, which StartMusic sets to m_musicResetNonContinous (120 s) plus
    //    the clip's length. HandleEventMusic keeps asking for the boss track, gets refused, and
    //    still returns true, so nothing else plays either: the fight goes quiet.
    // The other two reads (MusicMan.ApplySettings, MusicLocation.Update) only touch ambient and
    // location music, never an event's track, and are left alone.
    //
    // THE CHANGE. A transpiler swaps each of those two reads for IsContinuous(track), which is the
    // vanilla setting OR'd with "this is the current boss fight's track". The setting itself is
    // never written, so the player's own choice in the Audio settings is untouched and every other
    // kind of music behaves exactly as they set it.
    //
    // WHAT COUNTS AS THE BOSS FIGHT'S TRACK: a boss's health bar is up (RandEventSystem.GetBossEvent),
    // the active event is that boss's own event, and the track in question is that event's forced
    // music. Requiring the event to match keeps a raid that happens to be running near a boss
    // without a music event of its own (the Hive's boss_hive has none) on the player's setting.
    // Ambient music in a boss's arena, such as the Queen's lair's boss_queen_ambience, is the
    // environment's music rather than the fight's, and is not covered. When the boss dies or its
    // bar goes away, the event ends and HandleEventMusic stops the track as in vanilla.
    //
    // The second read also means walking out of range and back in mid-fight restarts the boss music
    // straight away instead of after the two-minute wait.
    //
    // Client config: this only decides what one player hears. MusicMan never runs anything that a
    // server or another player can see.
    [HarmonyPatch]
    internal static class ContinuousBossMusicPatch {
        internal static ConfigEntry<bool> Enabled;

        private static readonly FieldInfo ContinousMusicField =
            AccessTools.Field(typeof(Settings), nameof(Settings.ContinousMusic));
        private static readonly FieldInfo CurrentMusicField =
            AccessTools.Field(typeof(MusicMan), "m_currentMusic");
        private static readonly MethodInfo IsContinuousMethod =
            AccessTools.Method(typeof(ContinuousBossMusicPatch), nameof(IsContinuous));

        internal static void BindConfig() {
            Enabled = ValConfig.BindClientConfig(
                "Music",
                "Continuous Boss Music",
                true,
                "Whether a boss's music keeps playing for the whole fight even when Continuous " +
                "Music is turned off in the game's audio settings. With that setting off, vanilla " +
                "plays the boss track once and then leaves the rest of the fight silent for about " +
                "two minutes. All other music still follows the game's setting. Has no effect when " +
                "Continuous Music is on. Per-machine: this only affects what you hear and is not " +
                "synced from the server.");
        }

        // `!Settings.ContinousMusic && <clip nearly over>` -> `!IsContinuous(this.m_currentMusic) && ...`
        // In this branch nothing is queued or stopping, so m_currentMusic is the track that is playing.
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MusicMan), "UpdateMusic")]
        private static IEnumerable<CodeInstruction> UpdateMusicTranspiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceSettingRead(instructions, "MusicMan.UpdateMusic",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, CurrentMusicField));

        // `music == m_lastStartedMusic && !Settings.ContinousMusic && ...` -> `... && !IsContinuous(music) && ...`
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MusicMan), "StartMusic", new[] { typeof(MusicMan.NamedMusic) })]
        private static IEnumerable<CodeInstruction> StartMusicTranspiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceSettingRead(instructions, "MusicMan.StartMusic",
                new CodeInstruction(OpCodes.Ldarg_1));

        // Swaps the single `ldsfld Settings.ContinousMusic` for `<loadTrack>; call IsContinuous`.
        // The read follows a branch in both methods, so its labels move to the first new instruction.
        private static IEnumerable<CodeInstruction> ReplaceSettingRead(
            IEnumerable<CodeInstruction> instructions, string method, params CodeInstruction[] loadTrack) {
            List<CodeInstruction> codes = new List<CodeInstruction>();
            int reads = 0;
            foreach (CodeInstruction instruction in instructions) {
                CodeInstruction code = new CodeInstruction(instruction);
                if (code.LoadsField(ContinousMusicField)) {
                    codes.Add(new CodeInstruction(loadTrack[0]).MoveLabelsFrom(code).MoveBlocksFrom(code));
                    for (int i = 1; i < loadTrack.Length; i++) {
                        codes.Add(new CodeInstruction(loadTrack[i]));
                    }
                    codes.Add(new CodeInstruction(OpCodes.Call, IsContinuousMethod));
                    reads++;
                } else {
                    codes.Add(code);
                }
            }

            if (reads == 1) { return codes; }

            Logger.LogWarning(
                $"{method}: expected one read of Settings.ContinousMusic, found {reads}, so Continuous " +
                "Boss Music does not apply there. A Valheim update or another mod has most likely " +
                "changed the method.");
            return instructions;
        }

        // Stands in for Settings.ContinousMusic. The player's own setting is checked first, so with
        // Continuous Music on (vanilla's default) this is one static read, as before.
        private static bool IsContinuous(MusicMan.NamedMusic track) {
            if (Settings.ContinousMusic) { return true; }
            if (Enabled == null || !Enabled.Value) { return false; }
            return IsBossFightTrack(track);
        }

        private static bool IsBossFightTrack(MusicMan.NamedMusic track) {
            if (track == null) { return false; }

            RandEventSystem events = RandEventSystem.instance;
            if (events == null) { return false; }

            string bossEvent = events.GetBossEvent();
            if (bossEvent == null) { return false; }

            RandomEvent active = events.GetActiveEvent();
            return active != null && active.m_name == bossEvent && active.m_forceMusic == track.m_name;
        }
    }
}
