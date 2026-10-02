using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using CommunityPatchExtras.Common;

namespace CommunityPatchExtras.Patches {
    // Lets you tell every tamed creature to follow or to stay, not just the handful Irongate marked
    // for it. Boars are the ones people actually notice: a tamed wolf heels, a tamed lox heels, a
    // tamed boar is furniture - it wanders its pen forever, and the only way to move one is to build
    // a corridor and herd it. Nothing about a boar makes it incapable; the whole difference is one
    // serialized bool.
    //
    // THE FLAG IS THE ENTIRE MECHANISM. Tameable.m_commandable (Tameable.cs:28) is read in exactly
    // one place in the whole game - Tameable.Interact (Tameable.cs:203), where a tamed creature's
    // Use either toggles follow/stay (flag set) or prints the petting message (flag clear).
    // Everything behind it is already generic and already on the boar: Command -> the "Command" RPC
    // -> RPC_Command, which flips MonsterAI's follow target, writes the owner's name to the ZDO's
    // "follow" key, and hands "stay" to BaseAI's patrol point. None of that is species-specific and
    // none of it needed adding, so this patch writes the one bool and lets vanilla do the rest.
    //
    // WHY Awake, AND ONLY Awake. "Set it when the creature is tamed" is the obvious guess and would
    // be strictly worse. m_commandable is a plain MonoBehaviour field on the prefab, not a ZDO value
    // - it is deserialized fresh from the prefab every time the object is instantiated, and it is
    // never saved. So there is no such thing as an already-tamed boar that "does not have the flag":
    // the flag survives a zone unload for nobody, vanilla included. One postfix on Tameable.Awake
    // therefore covers every case at once - a boar tamed a year ago on an existing save, a boar
    // tamed a second ago, a piglet that just grew up into a brand new adult object, and a boar that
    // is still wild. A tame-time hook would cover strictly fewer of those and would need this pass
    // anyway.
    //
    // Nothing is needed to keep wild creatures out of it either. Interact tests IsTamed() before it
    // ever looks at m_commandable, so a flagged wild boar is just a wild boar.
    //
    // WHAT IT COSTS, both inherent to vanilla's own toggle rather than added here:
    //  - Use on a newly commandable creature commands it instead of petting it. The "$hud_tamelove"
    //    message and the TamedPetting player stat give way to TamedCommand, which is precisely the
    //    trade a wolf owner already makes. Nothing reads either stat by name - they are display-only
    //    counters - so no unlock or achievement moves.
    //  - A creature follows by PLAYER NAME, not by id: Tameable saves GetPlayerName() to the ZDO and
    //    re-acquires the target by matching that string (Tameable.cs:503). Two players sharing a
    //    name on one server share each other's followers. That is vanilla's quirk on vanilla's own
    //    commandable creatures; widening the set widens its reach.
    //
    // RELEASING ON THE WAY BACK DOWN. Turning the setting off cannot just stop setting the flag. A
    // creature left mid-follow would follow forever: UpdateSavedFollowTarget re-issues the saved
    // command out of the ZDO every Update (Tameable.cs:495), and Interact would no longer offer any
    // way to cancel it. So a creature this feature no longer covers has its saved follow target
    // cleared - see Release, which also explains why the "stay" patrol point is deliberately left
    // alone. That runs over the loaded creatures the moment the setting changes, and at every Awake
    // afterwards, which is what reaches the ones that were nowhere near a player at the time.
    // Summoned minions are the exception: following is how vanilla ties them to their summoner.
    //
    // SUMMONED MINIONS are tames too, so this makes them commandable as well. Telling one to stay
    // clears the very key every summon limit reads; SummonLeashPatch keeps a parked summon counted
    // and leashed to its summoner.
    //
    // SERVER-SYNCED, unlike the grass and cutscene settings. This one changes what the world does:
    // the command writes the ZDO keys "follow", "patrol" and "patrolPoint", which the server owns,
    // persists and streams to everyone. "Can boars be ordered around here" is an admin's answer.
    //
    // Safe one-sided either way, which is the bar every patch here has to clear. The "Command" RPC
    // is registered by vanilla's own Tameable.Awake on every machine, mod or no mod, so a modded
    // client commanding a boar whose ZDO a vanilla client owns works untouched. And a client without
    // the mod on a server with it simply never raises the flag locally, so it pets its boars while
    // its neighbour commands them - a difference in what one player's Use key does, not a desync.
    [HarmonyPatch]
    internal static class CommandableTamesPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Exceptions;

        // Tameable.m_commandable exactly as each prefab ships it, keyed by prefab name. Recorded on
        // first sight of a prefab and never re-recorded, so our own writes can never be mistaken for
        // Irongate's value - the same reason GrassDistancePatch captures its LODs once per process.
        // Filled whether or not the feature is on, because Release needs a trustworthy answer to
        // "does this creature ship commandable" even on a server that has had the setting off all
        // along.
        private static readonly Dictionary<string, bool> Shipped = new Dictionary<string, bool>();

        // Parsed form of Exceptions. Rebuilt only when the raw string changes, because this is read
        // once per creature spawn.
        private static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _excludedSource;

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                "Tamed Creatures",
                "Commandable Tames",
                true,
                "Whether every tamed creature can be told to follow and to stay, rather than only " +
                "the ones Valheim marked for it. Vanilla allows it for wolves, lox and asksvins but " +
                "not for boars, hens or anything else - a tamed boar cannot be called, so moving one " +
                "means herding it. On, interacting with any tamed creature toggles follow/stay for " +
                "it exactly as it already does for a wolf; the trade is that those creatures can no " +
                "longer be petted for the affection message, because that is the same key. Taming " +
                "itself is unchanged, and wild creatures are unaffected. Summoned minions can be told " +
                "to stay too, but one waiting still counts toward its summoner's limit and is " +
                "dismissed under the same rules as one following. Turning this back off releases " +
                "anything currently following, except summoned minions, which keep following their " +
                "summoner. Server-synced: this changes saved world state, so the server's value is " +
                "used for everyone.");

            Exceptions = ValConfig.BindServerConfig(
                "Tamed Creatures",
                "Commandable Tames Exceptions",
                "",
                "Prefab names to leave exactly as Valheim ships them, comma separated, for when you " +
                "want commandable boars but not commandable hens - for example \"Hen,Chicken\". " +
                "These are prefab names rather than display names, and are matched ignoring case. " +
                "Creatures listed here keep petting and cannot be commanded, and any of them already " +
                "following someone is released, except summoned minions, which keep following their " +
                "summoner. Ignored entirely when Commandable Tames is off.",
                null,
                true);

            // One debounce key for both entries, so editing the list and the toggle in the same
            // breath - or a server sync delivering both at once - costs a single pass.
            Enabled.SettingChanged += (sender, args) =>
                ConfigChangeDebouncer.Schedule(typeof(CommandableTamesPatch), ApplyToLoaded);
            Exceptions.SettingChanged += (sender, args) =>
                ConfigChangeDebouncer.Schedule(typeof(CommandableTamesPatch), ApplyToLoaded);
        }

        private static bool IsExcluded(string prefab) {
            string raw = Exceptions != null ? Exceptions.Value : "";
            if (!string.Equals(raw, _excludedSource, StringComparison.Ordinal)) {
                _excludedSource = raw;
                Excluded.Clear();
                foreach (string entry in raw.Split(',')) {
                    string trimmed = entry.Trim();
                    if (trimmed.Length > 0) { Excluded.Add(trimmed); }
                }
            }

            return Excluded.Count > 0 && Excluded.Contains(prefab);
        }

        // Whether this feature should be raising the flag on this creature. It never lowers one the
        // prefab ships raised - vanilla's own commandable creatures are not this feature's business.
        private static bool ShouldCommand(Tameable tameable, string prefab) {
            if (Enabled == null || !Enabled.Value) { return false; }

            // RPC_Command returns on its first line without one (Tameable.cs:465), so a commandable
            // Tameable with no MonsterAI would swallow the interaction and do nothing at all -
            // neither commanding nor petting. Rules out anything driven by AnimalAI instead, which
            // has no follow target to set.
            if (tameable.m_monsterAI == null) { return false; }

            return !IsExcluded(prefab);
        }

        // Writes the flag for one instance. Returns whether the value actually moved, which only
        // matters for the log line in ApplyToLoaded.
        private static bool Apply(Tameable tameable) {
            string prefab = Utils.GetPrefabName(tameable.gameObject);

            // First sight of a prefab is always its shipped value: at Awake vanilla has finished and
            // nothing has written the field yet, and ApplyToLoaded only ever sees creatures whose
            // Awake already ran.
            if (!Shipped.TryGetValue(prefab, out bool shipped)) {
                shipped = tameable.m_commandable;
                Shipped[prefab] = shipped;
            }

            bool before = tameable.m_commandable;
            tameable.m_commandable = shipped || ShouldCommand(tameable, prefab);
            return tameable.m_commandable != before;
        }

        // Hands a creature this feature no longer covers back its vanilla behaviour. A creature
        // vanilla never lets you command should not be following anybody - for an ordinary tame,
        // vanilla cannot produce that state, only this feature can - and left in it the animal is a
        // trap: Tameable re-issues the saved command every Update and Interact no longer offers a way
        // to cancel it.
        //
        // Summoned minions are the one non-commandable creature vanilla does make follow: SpawnAbility
        // commands each one to its summoner at spawn, and that saved follow is what the summon limit,
        // the leash and the logout timer all hang off. Clearing it would set the summon loose for
        // good, so a summon is never released - one parked under this feature stays leashed by
        // SummonLeashPatch instead.
        //
        // The patrol point a "stay" leaves behind is deliberately NOT cleared, because that flag is
        // not ours to interpret: CreatureSpawner, SpawnArea, TriggerSpawner and OfferingBowl all set
        // it on creatures they place, so clearing it would unpin guards that have nothing to do with
        // taming. On a released tame it only pins idle wandering to the spot it was told to wait at,
        // which is the spot it is already standing on.
        //
        // Owner-gated because that is who may write the ZDO, and also who runs the re-issuing
        // Update - so the machine that would otherwise keep the command alive is the one that clears
        // it. Returns whether anything was cleared.
        private static bool Release(Tameable tameable) {
            if (SummonLeashPatch.IsSummon(tameable)) { return false; }

            ZNetView nview = tameable.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return false; }

            ZDO zdo = nview.GetZDO();
            if (zdo == null || zdo.GetString(ZDOVars.s_follow).Length == 0) { return false; }

            zdo.Set(ZDOVars.s_follow, "");
            if (tameable.m_monsterAI != null) { tameable.m_monsterAI.SetFollowTarget(null); }
            return true;
        }

        private static void ApplyAndRelease(Tameable tameable, ref int changed, ref int released) {
            if (Apply(tameable)) { changed++; }
            if (!tameable.m_commandable && Release(tameable)) { released++; }
        }

        // The creatures already in the world when a setting changes. Everything spawned after it is
        // handled by the Awake postfix below, so between them no creature is left on a stale value -
        // which matters most on the way down, where a stale value is an animal that cannot be told
        // to stop following.
        //
        // Character.GetAllCharacters is the live list the game already maintains, and every Tameable
        // this feature can touch is on a Character: it needs a MonsterAI, MonsterAI is a BaseAI, and
        // BaseAI drives a Character. The list is empty outside a world, so this is inert in the menu.
        private static void ApplyToLoaded() {
            List<Character> characters = Character.GetAllCharacters();
            int changed = 0;
            int released = 0;

            for (int i = 0; i < characters.Count; i++) {
                Character character = characters[i];
                if (character == null) { continue; }

                Tameable tameable = character.GetComponent<Tameable>();
                if (tameable == null) { continue; }

                ApplyAndRelease(tameable, ref changed, ref released);
            }

            if (changed == 0 && released == 0) { return; }

            string state = (Enabled != null && Enabled.Value) ? "enabled" : "disabled";
            Logger.LogInfo($"Commandable tames {state}: {changed} loaded creature(s) updated, " +
                $"{released} released from following.");
        }

        // Vanilla's Awake has just read the prefab's value into the instance and wired up m_nview,
        // m_monsterAI and the "Command" RPC, and nothing reads m_commandable until a player
        // interacts - so a postfix here is early enough for every instance and late enough to see
        // everything it needs. It runs on a dedicated server too, harmlessly: the server never calls
        // Interact, and it owns only the creatures around its own reference position - each client
        // owns those in its own area - so Release there acts on exactly what that server owns.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Tameable), "Awake")]
        private static void TameableAwakePostfix(Tameable __instance) {
            int changed = 0;
            int released = 0;
            ApplyAndRelease(__instance, ref changed, ref released);
        }
    }
}
