using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace CommunityPatchExtras.Patches {
    // Makes a summoning staff that calls more than one kind of creature count them all toward its one
    // summon limit. In vanilla that is the Spirit Caller, and in vanilla it has no real limit.
    //
    // THE BUG. A summon staff writes its limit (MaxInstances, the staff's quality) onto each summon,
    // and Tameable.UnsummonMaxInstances enforces it when the summon starts following: it counts the
    // loaded summons following the same player and dismisses the oldest past the limit. But it decides
    // which summons count together by Character.m_name - the display name. The Dead Raiser only ever
    // raises Skeleton_Friendly, so one name, one limit, and it works. The Spirit Caller
    // (staff_SpiritCaller_spawn) picks one of four prefabs at random - Wolf_, Boar_, Moose_ and
    // Bjorn_spiritcaller - and each has its own name ($spiritcaller_wolf and so on). A wolf is never
    // counted against a moose, so every species gets the whole limit to itself: four times what the
    // staff's quality allows, and with SpawnAbility's own global check (ten loaded of each prefab) the
    // only thing left above it. The staff's own message - "your Spirit Caller is not strong enough to
    // control more summons" - says the limit is meant to be the staff's, not each species'.
    //
    // THE FIX. A transpiler swaps the two `character.m_name` reads in UnsummonMaxInstances - the
    // summon being enforced and each candidate sibling - for SummonGroup, which maps every creature
    // one staff can summon to that staff. The comparison then asks "summoned by the same kind of
    // staff" instead of "same name", and the rest of the method - the follower check, the
    // oldest-first order, the dismissal and the message - is vanilla's, untouched.
    //
    // WHERE THE GROUPS COME FROM. Read from the game's own data rather than a list of names, so a
    // modded staff that summons several creatures is covered too: every item in ObjectDB, both attacks,
    // every place an attack can carry a SpawnAbility (the projectile itself, spawn-on-trigger,
    // spawn-on-hit, and a projectile's own spawn-on-hit). A SpawnAbility that commands what it spawns
    // and can spawn two or more differently named tameable creatures is a group. Everything else -
    // the Dead Raiser, every ordinary tame, a creature in no group - keeps its own name, i.e. exactly
    // vanilla. Built once per ObjectDB, on first use, which is after Jotunn has registered modded items.
    //
    // It composes with SummonLeashPatch, which transpiles other instructions in the same method:
    // parked summons are counted by the remembered summoner there, and grouped by staff here.
    //
    // Turning it on does not dismiss anything by itself; the limit is applied the next time one of
    // the player's summons starts following or comes back into play near them.
    //
    // SERVER-SYNCED: it decides which creatures get dismissed, which is world state. Held at vanilla
    // until Jotunn's config sync confirms the server runs this mod, as VehicleDeconstructPatch does.
    [HarmonyPatch]
    internal static class SharedSummonLimitPatch {
        internal static ConfigEntry<bool> Enabled;

        // The ZNet instance whose session Jotunn confirmed this mod's config for. Identity-compared so
        // a new session invalidates it.
        private static ZNet _confirmedFor;

        // Character.m_name -> the SpawnAbility it is summoned by, only for staffs that summon more
        // than one kind. Rebuilt when ObjectDB is replaced (every world load).
        private static readonly Dictionary<string, string> Groups = new Dictionary<string, string>();
        private static ObjectDB _groupsFor;

        private static readonly FieldInfo NameField =
            AccessTools.Field(typeof(Character), nameof(Character.m_name));
        private static readonly MethodInfo SummonGroupMethod =
            AccessTools.Method(typeof(SharedSummonLimitPatch), nameof(SummonGroup));

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                "Tamed Creatures",
                "Shared Summon Limit",
                true,
                "Whether a staff that summons more than one kind of creature counts them all toward " +
                "its one summon limit. Vanilla counts each kind separately, so the Spirit Caller, " +
                "which calls a random wolf, boar, moose or bear, lets you keep that many of every " +
                "kind - four times what the staff allows - while the Dead Raiser, which only raises " +
                "skeletons, stops at its limit. On, every creature one staff can summon counts " +
                "together, and the oldest is dismissed first, as vanilla does. Also covers modded " +
                "staffs that summon several creatures. Server-synced; a client on a server without " +
                "this mod keeps vanilla behaviour.");

            SynchronizationManager.OnConfigurationSynchronized += OnConfigurationSynchronized;
        }

        private static void OnConfigurationSynchronized(object sender, ConfigurationSynchronizationEventArgs args) {
            if (args.UpdatedPluginGUIDs == null || !args.UpdatedPluginGUIDs.Contains(CommunityPatchExtras.PluginGUID)) { return; }

            if (ZNet.instance != null && !ZNet.instance.IsServer()) { _confirmedFor = ZNet.instance; }
        }

        private static bool Active() {
            if (Enabled == null || !Enabled.Value) { return false; }

            ZNet znet = ZNet.instance;
            if (znet == null) { return false; }

            return znet.IsServer() || ReferenceEquals(_confirmedFor, znet);
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Tameable), "UnsummonMaxInstances")]
        private static IEnumerable<CodeInstruction> UnsummonMaxInstancesTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>();
            int names = 0;
            foreach (CodeInstruction instruction in instructions) {
                CodeInstruction code = new CodeInstruction(instruction);
                if (code.LoadsField(NameField)) {
                    codes.Add(new CodeInstruction(OpCodes.Call, SummonGroupMethod).MoveLabelsFrom(code));
                    names++;
                } else {
                    codes.Add(code);
                }
            }

            if (names == 2) { return codes; }

            Logger.LogWarning(
                $"Tameable.UnsummonMaxInstances: expected two reads of Character.m_name, found {names}, " +
                "so Shared Summon Limit does not apply. A Valheim update or another mod has most likely " +
                "changed the method.");
            return instructions;
        }

        // Same stack and result as the `ldfld Character.m_name` it replaces.
        private static string SummonGroup(Character character) {
            string name = character.m_name;
            if (!Active()) { return name; }

            return GetGroups().TryGetValue(name, out string group) ? group : name;
        }

        private static Dictionary<string, string> GetGroups() {
            ObjectDB db = ObjectDB.instance;
            if (ReferenceEquals(db, _groupsFor)) { return Groups; }

            _groupsFor = db;
            Groups.Clear();
            if (db == null) { return Groups; }

            foreach (GameObject prefab in db.m_items) {
                ItemDrop item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (item == null) { continue; }

                ItemDrop.ItemData.SharedData shared = item.m_itemData.m_shared;
                AddAttack(shared.m_attack);
                AddAttack(shared.m_secondaryAttack);
            }

            return Groups;
        }

        private static void AddAttack(Attack attack) {
            if (attack == null) { return; }

            AddSpawner(attack.m_attackProjectile);
            AddSpawner(attack.m_spawnOnTrigger);
            AddSpawner(attack.m_spawnOnHit);
        }

        private static void AddSpawner(GameObject spawner) {
            if (spawner == null) { return; }

            AddGroup(spawner.GetComponent<SpawnAbility>());

            Projectile projectile = spawner.GetComponent<Projectile>();
            if (projectile != null && projectile.m_spawnOnHit != null) {
                AddGroup(projectile.m_spawnOnHit.GetComponent<SpawnAbility>());
            }
        }

        // Only a SpawnAbility that commands what it spawns is a summon staff, and only a tameable
        // creature can be commanded, so a boss's minion spawner (Fader's commands its charred, which
        // have no Tameable) or the Staff of the Wild's roots never form a group.
        private static void AddGroup(SpawnAbility ability) {
            if (ability == null || !ability.m_commandOnSpawn || ability.m_spawnPrefab == null) { return; }

            List<string> names = new List<string>();
            foreach (GameObject prefab in ability.m_spawnPrefab) {
                if (prefab == null || prefab.GetComponent<Tameable>() == null) { continue; }

                Character character = prefab.GetComponent<Character>();
                if (character != null && !names.Contains(character.m_name)) { names.Add(character.m_name); }
            }

            if (names.Count < 2) { return; }

            string group = ability.name;
            foreach (string name in names) {
                if (!Groups.ContainsKey(name)) { Groups[name] = group; }
            }

            Logger.LogDebug($"Shared summon limit: {group} counts {string.Join(", ", names)} together.");
        }
    }
}
