using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace CommunityPatchExtras.Patches {
    // Keeps a summoned minion that was told to stay tied to whoever summoned it. Without this,
    // Commandable Tames turns "stay" into a way out of every summon limit Valheim has.
    //
    // WHAT A SUMMON IS. Skeleton_Friendly (Dead Raiser) and the four *_spiritcaller animals (Spirit
    // Caller): Tameables that start tamed, ship m_commandable off, and set m_unsummonDistance 150 and
    // m_unsummonOnOwnerLogoutSeconds 120. Nothing else in the Deep North rip sets either unsummon
    // field, so they are the test (IsSummon). The troll and charred summons have no Tameable at all,
    // so Commandable Tames never reached them and neither does this.
    //
    // WHY "STAY" BROKE THEM. Vanilla never lets you command a summon. SpawnAbility commands it to
    // follow once, at spawn, and from then on the ZDO's "follow" key - the summoner's name - is the
    // only thing tying it to anyone. All three of vanilla's summon limits read that key or the follow
    // target it restores:
    //  - the cap: UnsummonMaxInstances, run when a summon starts following, counts the loaded summons
    //    whose "follow" is the summoner's name and dismisses the oldest past MaxInstances (the staff's
    //    quality, written onto each summon at spawn);
    //  - the leash: UpdateSummon dismisses it past m_unsummonDistance from its follow target;
    //  - the logout timer: UpdateSavedFollowTarget dismisses it once "follow" names nobody loaded.
    // The stay branch of RPC_Command clears both the follow target and "follow". So a parked summon
    // was uncounted and permanent - even standing next to you, a quality 1 staff could field ten
    // (SpawnAbility's own global check of loaded instances), and once it unloaded, unlimited.
    //
    // THE FIX: REMEMBER THE SUMMONER. Every time a summon starts following someone, the name is also
    // written to a key of our own, which "stay" does not clear. A summon with no follow target, an
    // empty "follow" and that key set is parked, and while parked it is held to the same three rules
    // against the remembered name:
    //  - the cap counts it. One transpiler on UnsummonMaxInstances reads our key where "follow" is
    //    empty, and lets the method find the summoner of a parked summon as well as a following one,
    //    so vanilla's own loop, oldest-first order, dismissal and message do all of the work;
    //  - the leash and the logout timer run from an UpdateSummon postfix, with vanilla's numbers.
    //
    // WALKING AWAY FREEZES IT, AND THAT IS DELIBERATE. Vanilla stops simulating a creature once its
    // owner is about 1.5 zones away (ZNetScene.PointInsideActiveArea) - 64 to 128 m, before the 150 m
    // leash can ever be measured. So a parked summon you walk away from simply waits, exactly as a
    // following summon left behind through a portal does in vanilla. What stops that becoming the old
    // exploit is the cap again: the moment a parked summon comes back into play - this machine gains
    // ownership of it - it re-applies its cap, as vanilla does when a following summon reloads and is
    // re-commanded. Park three, walk off, summon three more, come back: the three oldest go. Come back
    // without summoning and it is still there. Brought into play by someone else while its summoner is
    // away, the logout timer dismisses it. The leash does still fire where ownership survives the
    // distance - entering a dungeon, a teleport, another player's machine owning it.
    //
    // ON OWNERSHIP GAIN, NOT ON LOAD. Instances load further out than ownership reaches, and
    // ownership can lapse and return without the object ever reloading (walk 100 m off and back), so
    // a load-time check would usually find nothing it may act on. Gaining ownership catches every
    // way back into play, and parking a summon you already own never trims anything.
    //
    // NOT GATED ON COMMANDABLE TAMES. A parked summon has to stay leashed after the feature is turned
    // off, because nobody can call it back any more. Summons parked by 0.4.0 have no remembered name
    // and are left alone until someone tells one to follow, which writes it.
    //
    // Accepted, all vanilla's own behaviour:
    //  - "absent" means not loaded on the machine that owns the summon, so a non-summoner owner can
    //    start the logout timer while the summoner is just outside its loaded area;
    //  - the timer adds Time.fixedDeltaTime per frame, so it is frame-rate dependent (about 100 s at
    //    60 fps), and the remembered name is a player name, so two players sharing one share summons;
    //  - each summon carries the cap of the staff that made it, and the one re-applying it uses its own;
    //  - the max-summons message goes to whoever owns the summon, not necessarily its summoner.
    // A client without this mod that owns a parked summon runs none of this while it owns it, which is
    // no worse than before.
    [HarmonyPatch]
    internal static class SummonLeashPatch {
        // The last player the summon followed. A player name, like vanilla's own "follow" key.
        private static readonly int SummonerKey = "CPE_Summoner".GetStableHashCode();

        private sealed class State {
            internal bool Owned;
            internal bool TrimPending;
        }

        // Per instance, so it dies with the object: whether this machine owned it last frame, and
        // whether it has re-applied its cap since it last came back into play here.
        private static readonly ConditionalWeakTable<Tameable, State> States = new ConditionalWeakTable<Tameable, State>();

        // The (int, string) overload by its parameters, because ZDO also has GetString(int, out string).
        private static readonly MethodInfo GetStringMethod =
            AccessTools.Method(typeof(ZDO), nameof(ZDO.GetString), new[] { typeof(int), typeof(string) });
        private static readonly MethodInfo GetFollowTargetMethod =
            AccessTools.Method(typeof(MonsterAI), nameof(MonsterAI.GetFollowTarget));
        private static readonly MethodInfo SummonOwnerMethod =
            AccessTools.Method(typeof(SummonLeashPatch), nameof(SummonOwner));
        private static readonly MethodInfo CapAnchorMethod =
            AccessTools.Method(typeof(SummonLeashPatch), nameof(CapAnchor));

        internal static bool IsSummon(Tameable tameable) =>
            tameable.m_unsummonDistance > 0f || tameable.m_unsummonOnOwnerLogoutSeconds > 0f;

        // Matched by name, as UpdateSavedFollowTarget does: GetAllPlayers is the players loaded here.
        private static Player FindPlayer(string name) {
            foreach (Player player in Player.GetAllPlayers()) {
                if (player.GetPlayerName() == name) { return player; }
            }

            return null;
        }

        // The summoner's name if this summon is parked, otherwise null. A following summon whose
        // target is not loaded yet still has "follow" set, so it stays vanilla's business.
        private static string ParkedSummoner(Tameable tameable, ZDO zdo) {
            MonsterAI ai = tameable.m_monsterAI;
            if (ai == null || ai.GetFollowTarget() != null) { return null; }
            if (zdo.GetString(ZDOVars.s_follow).Length > 0) { return null; }

            string summoner = zdo.GetString(SummonerKey);
            return summoner.Length > 0 ? summoner : null;
        }

        // Runs on the owner, which is the only machine RPC_Command writes "follow" on. Copies a
        // non-empty "follow" into our key; ZDO.Set is a no-op when the value has not changed, so the
        // re-command on every reload costs nothing. IsValid is re-checked because the cap pass inside
        // RPC_Command can dismiss the very summon that was commanded. Summons only: the cap matches
        // by display name, so a key on an ordinary tame sharing that name would get it counted.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Tameable), "RPC_Command")]
        private static void RPC_CommandPostfix(Tameable __instance) {
            if (!IsSummon(__instance)) { return; }

            ZNetView nview = __instance.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }

            ZDO zdo = nview.GetZDO();
            string follow = zdo.GetString(ZDOVars.s_follow);
            if (follow.Length > 0) { zdo.Set(SummonerKey, follow); }
        }

        // Two swaps in UnsummonMaxInstances, all or nothing:
        //  - each sibling's `zdo.GetString(s_follow)` becomes SummonOwner, so parked siblings count;
        //  - `m_monsterAI.GetFollowTarget()` becomes CapAnchor(ai, this), so the method also runs for
        //    a parked summon, which has no follow target to take the summoner's name from.
        // If either does not match exactly once, vanilla's method is left as it is: parked summons
        // then go uncounted, and the trim below finds no summoner and does nothing.
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Tameable), "UnsummonMaxInstances")]
        private static IEnumerable<CodeInstruction> UnsummonMaxInstancesTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>();
            int reads = 0;
            int anchors = 0;
            foreach (CodeInstruction instruction in instructions) {
                CodeInstruction code = new CodeInstruction(instruction);
                if (code.Calls(GetStringMethod)) {
                    codes.Add(new CodeInstruction(OpCodes.Call, SummonOwnerMethod).MoveLabelsFrom(code));
                    reads++;
                } else if (code.Calls(GetFollowTargetMethod)) {
                    codes.Add(new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code));
                    codes.Add(new CodeInstruction(OpCodes.Call, CapAnchorMethod));
                    anchors++;
                } else {
                    codes.Add(code);
                }
            }

            if (reads == 1 && anchors == 1) { return codes; }

            Logger.LogWarning(
                $"Tameable.UnsummonMaxInstances: expected one follow read and one follow target, found " +
                $"{reads} and {anchors}, so summons told to stay do not count toward the summon limit. A " +
                "Valheim update or another mod has most likely changed the method.");
            return instructions;
        }

        // Same stack and result as the ZDO.GetString it replaces.
        private static string SummonOwner(ZDO zdo, int key, string defaultValue) {
            string value = zdo.GetString(key, defaultValue);
            if (key != ZDOVars.s_follow || value.Length > 0) { return value; }

            return zdo.GetString(SummonerKey, defaultValue);
        }

        // Vanilla only reaches UnsummonMaxInstances from RPC_Command's follow branch, where the target
        // is always set, and gets it back unchanged. Only the trim below arrives without one.
        private static GameObject CapAnchor(MonsterAI ai, Tameable tameable) {
            GameObject target = ai.GetFollowTarget();
            if ((object)target != null) { return target; }

            ZNetView nview = tameable.m_nview;
            if (nview == null || !nview.IsValid()) { return null; }

            string summoner = ParkedSummoner(tameable, nview.GetZDO());
            if (summoner == null) { return null; }

            Player player = FindPlayer(summoner);
            return player != null ? player.gameObject : null;
        }

        // The parked summon's leash, logout timer and cap, on its owner, every frame. Cheapest tests
        // first: every Tameable runs this, and anything that is not a summon leaves on two compares.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Tameable), "UpdateSummon")]
        private static void UpdateSummonPostfix(Tameable __instance) {
            if (!IsSummon(__instance)) { return; }

            ZNetView nview = __instance.m_nview;
            if (nview == null || !nview.IsValid()) { return; }

            State state = States.GetOrCreateValue(__instance);
            bool owner = nview.IsOwner();
            bool gained = owner && !state.Owned;
            state.Owned = owner;
            if (!owner) { return; }

            ZDO zdo = nview.GetZDO();
            string summoner = ParkedSummoner(__instance, zdo);
            if (summoner == null) {
                state.TrimPending = false;
                return;
            }

            if (gained) { state.TrimPending = true; }

            // Vanilla's logout timer, increment included (UpdateSavedFollowTarget).
            Player player = FindPlayer(summoner);
            if (player == null) {
                if (__instance.m_unsummonOnOwnerLogoutSeconds > 0f) {
                    __instance.m_unsummonTime += Time.fixedDeltaTime;
                    if (__instance.m_unsummonTime > __instance.m_unsummonOnOwnerLogoutSeconds) { __instance.UnSummon(); }
                }
                return;
            }

            // Vanilla's leash (UpdateSummon), measured to the summoner instead of a follow target.
            __instance.m_unsummonTime = 0f;
            if (__instance.m_unsummonDistance > 0f &&
                Vector3.Distance(player.transform.position, __instance.transform.position) > __instance.m_unsummonDistance) {
                __instance.UnSummon();
                return;
            }

            // Back in play near its summoner: count everything again, as a re-follow would. Waits for
            // the summoner to be loaded here, which on a fresh login can be a moment after the summon.
            if (!state.TrimPending) { return; }

            state.TrimPending = false;
            int max = zdo.GetInt(ZDOVars.s_maxInstances);
            if (max > 0) { __instance.UnsummonMaxInstances(max); }
        }
    }
}
