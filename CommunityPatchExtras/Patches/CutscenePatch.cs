using BepInEx.Configuration;
using HarmonyLib;

namespace CommunityPatchExtras.Patches {
    // A switch for every cutscene Valheim plays at you unasked. There are four, they arrived in
    // different updates, they have four different triggers and four different mechanisms, and the
    // game exposes a setting for none of them. Every entry here defaults to vanilla - on, playing
    // - so this section changes nothing until somebody turns something off.
    //
    // WHAT IS COVERED, in the order a playthrough meets them:
    //
    //  1. THE STARTUP CINEMATIC - the pre-rendered video that covers the first walk to the main
    //     menu after launching the game. FejdStartup.Start kicks off PlayIntroCinematic
    //     (FejdStartup.cs:467), which gates on three things (FejdStartup.cs:477): the process has
    //     not run a Game yet (Game.m_hasStartedOnce), a CinematicsManager exists, and that
    //     manager's m_introOnStartup is set. That last one is a plain serialized bool Irongate put
    //     there as exactly this switch and then never wired to a setting - so this patch writes it
    //     rather than reimplementing anything. Cleared, vanilla's own else branch runs: the main
    //     menu is enabled, the IsStartedPlaying wait falls straight through, and the menu's FadeIn
    //     trigger fires as usual. Nothing else in the game reads the field.
    //
    //     Note what this is NOT: blocking CinematicsManager.Play instead would also work, but
    //     vanilla treats a refused Play as a failure and logs "Failed to play intro cinematic" as
    //     an error (FejdStartup.cs:486). Flipping the flag is the path the code already has for
    //     "there is no intro here", so nothing is left looking broken in the log.
    //
    //  2. THE NEW CHARACTER INTRO - the arrival sequence the first time a character spawns in any
    //     world: the raven's greeting, or on a new world the same video again if the scene enabled
    //     m_introOnNewWorld, followed by the valkyrie carrying you in and dropping you at the
    //     spawn stones. Game.Start queues it (Game.cs:300) whenever this is not a dedicated server
    //     and the profile has never spawned, and Game.Update (Game.cs:650) waits for world
    //     generation to be within the intro's own length of finishing before starting it.
    //
    //     Clearing m_queuedIntro in a Start postfix is the whole suppression, and it is the
    //     smallest possible cut: Update's very first check bails on it, so m_inIntro never becomes
    //     true, so UpdateRespawn's SpawnPlayer(point, firstSpawn && m_inIntro) passes false
    //     (Game.cs:738) and Player.OnSpawned never calls SetIntro or SpawnValkyrie. That leaves
    //     the player on vanilla's ordinary spawn path, which is not a degraded one - it is the
    //     branch that already runs for every spawn after the first, and it carries the two side
    //     effects the valkyrie would otherwise have delivered at the end of its flight
    //     (IncrementPlayerStat(PlayerSpawn) and SetMultiplayerUsageStart, Game.cs:493). Every
    //     other first-spawn consequence is keyed off m_playerProfile.m_firstSpawn or Game's own
    //     m_firstSpawn rather than off the intro, so the home point, the "player arrived" shout
    //     and the join code popup all still happen.
    //
    //     There is then nothing left to skip, so the pause menu's Skip button correctly hides
    //     itself (Menu.cs:250 reads InIntro), and Player.InIntro stays false for the camera,
    //     music, chat and movement checks that branch on it.
    //
    //  3. DREAM CINEMATICS - the videos that play the next time you sleep after killing a boss, or
    //     after offering a trophy at a boss stone. Both triggers call SetDreamCinematic
    //     (Character.cs:2918 on death, RuneStone.cs:83 at the stone), which is a routed RPC sent
    //     to EVERY peer, so the dream is queued for the whole server by one player's kill. It is
    //     then cashed in locally: SleepText.ShowDreamText calls CinematicsManager.OnSleep four
    //     seconds into the sleep fade, which plays the queued video and clears the queue.
    //
    //     Prefixing OnSleep is therefore both the right choke point and the only local one -
    //     suppressing the RPC end instead would either stop the dream for everyone else (the
    //     sender is the killer's machine) or leave the queue permanently armed. The prefix mirrors
    //     vanilla's own two conditions exactly, including only consuming the queue when the player
    //     really is still asleep, and then clears it: a dream Valheim considers spent should not
    //     ambush the player three nights later because a setting was flipped back on. With no
    //     video playing, SleepText's own IsPlaying check falls through to the ordinary random
    //     dream text, which is the vanilla no-cinematic path rather than a blank screen.
    //
    //  4. THE ENDING - interacting with "THE END" plays the outro video, then rolls the credits,
    //     then logs you out (EndCredits.cs:70 and the m_queueCredits/m_queueLogout walk in its
    //     Update). Prefixing StartEndCredits and setting the queue straight to the logout leg
    //     hands the sequence back to vanilla's own Update at its last step - the credits panel is
    //     never shown, so its scroll (which is gated on a video actually playing, EndCredits.cs:34)
    //     never has to be faked, and the logout keeps vanilla's own multiplayer guard: a host with
    //     peers still connected stays in the world exactly as it does today.
    //
    // WHAT IS DELIBERATELY NOT COVERED: the cinematics the player asks for by name. The main
    // menu's Cinematics gallery (FejdStartup.OnCinematicsPlay), its Credits button
    // (FejdStartup.OnCreditsEnd) and the `cinematic` / `cinematicsleep` console commands all reach
    // the same CinematicsManager.Play, which is exactly why this patch touches call sites rather
    // than that funnel - Settings.Credits alone cannot tell the ending's credits from the menu
    // button's, and a filter there would break a button whose entire purpose is to play the thing.
    //
    // NOTHING IS LOST BY SKIPPING, which is the fact that makes these toggles cheap. A video's
    // place in the main menu gallery is derived from player stats, world keys and player keys
    // (FejdStartup.cs:1966 onward), never from having watched it - so the boss kill unlocks the
    // dream whether or not the dream played, and every skipped cutscene remains sitting in the
    // Cinematics menu to be watched on purpose later.
    //
    // THE ONE REAL KNOCK-ON is on the character intro, and only on a brand new world: that intro
    // doubles as cover for world generation. While it is on screen ZoneSystem.Update hands
    // location generation a per-frame time budget so the intro stays smooth; with nothing on
    // screen to protect, it takes 0.1 s per frame instead (ZoneSystem.cs:1149). Skipping does not
    // skip that work, it moves it to after you have landed - so the first stretch on a fresh world
    // generates at roughly ten frames a second while you stand in it. The patch does not introduce
    // this: it is exactly what vanilla's own Skip button already does, and the trade is stated in
    // the setting's description rather than papered over here, because "get me into the world now"
    // is a legitimate thing to want and holding the frame rate hostage to it would not be.
    //
    // WHY ALL FOUR ARE CLIENT CONFIG. None of these is a world rule. The startup video never sees
    // a ZNet at all, and the other three change no ZDO, no spawn point, no global key and no
    // profile state - they are cutscenes played to one player at one machine, and the server
    // cannot tell whether they ran. The dream is the interesting case and it still holds: the
    // queueing RPC goes out to everyone regardless, and each machine independently decides whether
    // to cash it in, so one player skipping does not take the dream away from anyone else. A
    // dedicated server plays none of them - it never queues a character intro, never sleeps and
    // never has a local player - so these are inert there rather than needing guards.
    [HarmonyPatch]
    internal static class CutscenePatch {
        internal static ConfigEntry<bool> PlayStartupCinematic;
        internal static ConfigEntry<bool> PlayCharacterIntro;
        internal static ConfigEntry<bool> PlayDreamCinematics;
        internal static ConfigEntry<bool> PlayEndingCinematic;

        // The CinematicsManager flags as the scene shipped them, so turning a toggle back on
        // restores what was there instead of assuming Irongate's field initializers. Captured once
        // per process rather than per Awake, for the same reason GrassDistancePatch captures its
        // prefab LODs once: we write to these, and a later capture would record our own value as
        // the vanilla one.
        private static bool _shippedOnStartup;
        private static bool _shippedOnNewWorld;
        private static bool _shippedCaptured;

        internal static void BindConfig() {
            PlayStartupCinematic = ValConfig.BindClientConfig(
                "Cutscenes",
                "Play Startup Cinematic",
                true,
                "Whether the opening cinematic plays on the way to the main menu when you launch " +
                "the game. Vanilla plays it once per launch and never again in that session; " +
                "turning this off goes straight to the menu instead. Still watchable any time " +
                "from the main menu's Cinematics list. Per-machine: this is a presentation " +
                "setting and is not synced from the server.");

            PlayCharacterIntro = ValConfig.BindClientConfig(
                "Cutscenes",
                "Play New Character Intro",
                true,
                "Whether the arrival sequence plays the first time a character spawns in a world " +
                "- the raven's greeting and the valkyrie flight that drops you at the spawn " +
                "stones. Turning this off puts you on the ground immediately, which is the same " +
                "thing vanilla's own Skip button in the pause menu does. On a BRAND NEW world the " +
                "intro also covers world generation, which runs at a much heavier per-frame " +
                "budget once nothing is on screen to protect - so skipping it trades the flight " +
                "for a stuttery first minute or two while the world finishes generating around " +
                "you. Per-machine: not synced from the server, and inert on a dedicated server, " +
                "which never plays one.");

            PlayDreamCinematics = ValConfig.BindClientConfig(
                "Cutscenes",
                "Play Dream Cinematics",
                true,
                "Whether the dream videos play - the ones queued by killing a boss or by offering " +
                "a trophy at a boss stone, which then run the next time you sleep. Turning this " +
                "off gives you the ordinary random dream text instead, exactly as a night with no " +
                "cinematic queued does. Skipping one costs you nothing: the main menu's " +
                "Cinematics list unlocks these from the boss kill itself, not from having watched " +
                "the dream. Per-machine: the queue is still sent to every player, and each " +
                "machine decides for itself whether to play it, so this never skips anyone " +
                "else's dream.");

            PlayEndingCinematic = ValConfig.BindClientConfig(
                "Cutscenes",
                "Play Ending Cinematic",
                true,
                "Whether interacting with \"THE END\" plays the outro video and rolls the " +
                "credits. Turning this off goes straight to the log out that ends that sequence " +
                "anyway - a host with other players still connected stays in the world, just as " +
                "in vanilla. Both the outro and the credits remain in the main menu's Cinematics " +
                "list. Per-machine: this only affects what your own client plays.");
        }

        // Unity runs every Awake in a scene load before any Start in it, and FejdStartup reaches
        // the cinematic from its Start - so the flag is always written before the code that reads
        // it, whether the CinematicsManager loads with the menu or survived from an earlier scene
        // (in which case its Awake, and this, ran earlier still). Re-applied at every Awake rather
        // than once, so a toggle flipped mid-session is in force the next time that scene loads
        // without any SettingChanged plumbing - the flags are read exactly twice in the whole
        // game, both times moments after an Awake.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CinematicsManager), "Awake")]
        private static void CinematicsManagerAwakePostfix(CinematicsManager __instance) {
            if (!_shippedCaptured) {
                _shippedCaptured = true;
                _shippedOnStartup = __instance.m_introOnStartup;
                _shippedOnNewWorld = __instance.m_introOnNewWorld;
            }

            bool startup = PlayStartupCinematic == null || PlayStartupCinematic.Value;
            bool character = PlayCharacterIntro == null || PlayCharacterIntro.Value;

            __instance.m_introOnStartup = startup && _shippedOnStartup;

            // Belt and braces on a flag the Start postfix below already makes unreachable:
            // Game.Update only consults m_introOnNewWorld after m_queuedIntro has let it past
            // (Game.cs:650). Cleared here anyway so both halves of "no character intro" are stated
            // in one place, and so the video stays off if a future version of the game finds
            // another route to it.
            __instance.m_introOnNewWorld = character && _shippedOnNewWorld;
        }

        // m_queuedIntro is set at the end of Game.Start and read nowhere until the next Update, so
        // a postfix here is the whole intervention. Nothing else writes it except vanilla's own
        // SkipIntro (Game.cs:781), which is this same thing by hand.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), "Start")]
        private static void GameStartPostfix(Game __instance) {
            if (PlayCharacterIntro == null || PlayCharacterIntro.Value) { return; }

            // False already on a dedicated server, and on every spawn after a character's first.
            if (!__instance.m_queuedIntro) { return; }

            __instance.m_queuedIntro = false;
            Logger.LogInfo("New character intro suppressed; spawning directly at the spawn point.");
        }

        // Vanilla's OnSleep is four lines: play the queued dream if there is one and the player is
        // actually asleep, then clear it. Both conditions are repeated here rather than assumed,
        // because SleepText fires this on a four second Invoke and the player can be out of bed by
        // then - vanilla holds the dream over to the next night in that case, and so does this.
        // The null guard is ours: vanilla dereferences m_localPlayer unchecked.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CinematicsManager), nameof(CinematicsManager.OnSleep))]
        private static bool OnSleepPrefix() {
            if (PlayDreamCinematics == null || PlayDreamCinematics.Value) { return true; }

            if (string.IsNullOrEmpty(CinematicsManager.m_dreamCinematic)) { return false; }
            if (Player.m_localPlayer == null || !Player.m_localPlayer.IsSleeping()) { return false; }

            Logger.LogInfo($"Dream cinematic '{CinematicsManager.m_dreamCinematic}' skipped; " +
                "it stays unlocked in the main menu's Cinematics list.");

            // Consumed, not deferred: Valheim treats a dream as spent the night it comes due, and
            // holding it would fire it on some unrelated night if this setting were turned back on.
            CinematicsManager.m_dreamCinematic = "";
            return false;
        }

        // Vanilla's StartEndCredits plays the outro and sets m_queueCredits; its Update then walks
        // credits -> logout. Setting the queue to the logout leg directly is that same walk minus
        // the two video steps, and leaves the logout itself - including its "not while other
        // players are connected" guard - entirely to vanilla's Update.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EndCredits), nameof(EndCredits.StartEndCredits))]
        private static bool StartEndCreditsPrefix(EndCredits __instance) {
            if (PlayEndingCinematic == null || PlayEndingCinematic.Value) { return true; }

            __instance.m_queueCredits = false;
            __instance.m_queueLogout = true;
            Logger.LogInfo("Ending cinematic skipped; logging out as the credits would have.");
            return false;
        }
    }
}
