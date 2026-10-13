using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace CommunityPatchExtras.Patches {
    // Makes hitting a bird raise the skill of the weapon that hit it, as hitting a creature does.
    //
    // WHY VANILLA GIVES NOTHING. Crows, gulls and ash crows (Crow, Seagal, AshCrow, plus the crows
    // placed inside crypts and ruins) are not Characters. Each is a RandomFlyingBird with a plain
    // Destructible of DestructibleType.Default and 1 health. Every place a weapon raises its skill on
    // a hit asks whether the target was a creature, and a bird never is:
    //  - Attack.DoMeleeAttack and DoAreaAttack require (type & m_skillHitType) != None, and
    //    m_skillHitType defaults to Character (Attack.cs:202, 1187, 1384).
    //  - Projectile.OnHit and its DoAOE require `destructible is Character` (Projectile.cs:598, 696).
    //  - Aoe.OnHit requires the target to be a Character before it raises m_skill (Aoe.cs:683, 745).
    // So an arrow through a gull, a frost bolt or a sword swing is worth exactly nothing.
    //
    // THE HOOK. Every one of those paths builds a HitData and passes it to Destructible.Damage on the
    // attacker's own machine, and all but Aoe write the skill and amount they would raise into it
    // (m_skill, m_skillRaiseAmount). A prefix there grants that skill and amount when the target is a
    // bird, which covers melee, thrown weapons, bows, crossbows and staffs in one place, modded
    // weapons included, and attributes each hit to the skill vanilla itself chose for it (a
    // special-hit skill included). Aoe never writes m_skill, so it gets a postfix of its own that
    // mirrors its vanilla rule exactly: one raise of 1 per Aoe, via m_gaveSkill.
    //
    // ONCE PER SWING. Vanilla raises once per melee swing or area attack no matter how many creatures
    // it hits, and all those hits are applied in one call. A skill already raised by a bird hit this
    // frame is not raised again, so a sweep through a flock counts once, like a sweep through a pack.
    // A swing that hits a bird and a creature together raises twice; birds flee anyone close enough
    // for that to happen, so this was left alone rather than deferring every raise to the end of
    // the swing.
    //
    // A bird hit raises the plain per-hit amount, never the 1.5 bonus vanilla adds for a melee hit
    // on a creature: a 1-health bird is worth one ordinary hit. Only the local player is credited:
    // skills are saved on the player's own machine, and a hit is only ever computed there.
    //
    // SERVER-SYNCED: it changes how skills progress, which is a world rule. Held at vanilla until
    // Jotunn's config sync confirms the server runs this mod, as SharedSummonLimitPatch does.
    [HarmonyPatch]
    internal static class BirdHitSkillPatch {
        internal static ConfigEntry<bool> Enabled;

        // The ZNet instance whose session Jotunn confirmed this mod's config for. Identity-compared so
        // a new session invalidates it.
        private static ZNet _confirmedFor;

        // Skills a bird hit has already raised during _raisedFrame. See ONCE PER SWING above.
        private static readonly HashSet<Skills.SkillType> RaisedThisFrame = new HashSet<Skills.SkillType>();
        private static int _raisedFrame = -1;

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                "Skills",
                "Bird Hits Raise Skills",
                true,
                "Whether hitting a bird raises the skill of the weapon you hit it with, as hitting a " +
                "creature does. Vanilla gives nothing for it, because crows and gulls are not " +
                "creatures to the game. Covers swords, bows, spears, staffs and every other weapon, " +
                "and a swing or blast that hits several birds counts once, as it does for creatures. " +
                "Server-synced; a client on a server without this mod keeps vanilla behaviour.");

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

        private static bool CanRaise(Skills.SkillType skill) {
            return skill != Skills.SkillType.None && skill != Skills.SkillType.All;
        }

        // A prefix, not a postfix: the hit can kill the bird and invalidate its ZNetView before a
        // postfix runs, and the gate below has to see the bird as vanilla's Damage did.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Destructible), nameof(Destructible.Damage))]
        private static void DamagePrefix(Destructible __instance, HitData hit) {
            if (!Active() || hit == null || !CanRaise(hit.m_skill)) { return; }

            // The same gate vanilla applies before it sends the hit on.
            if (__instance.m_firstFrame || __instance.m_nview == null || !__instance.m_nview.IsValid()) { return; }
            if (__instance.GetComponent<RandomFlyingBird>() == null) { return; }

            Player player = Player.m_localPlayer;
            if (player == null || hit.m_attacker != player.GetZDOID()) { return; }

            int frame = Time.frameCount;
            if (frame != _raisedFrame) {
                _raisedFrame = frame;
                RaisedThisFrame.Clear();
            }
            if (!RaisedThisFrame.Add(hit.m_skill)) { return; }

            player.RaiseSkill(hit.m_skill, hit.m_skillRaiseAmount);
        }

        // OnHit returns true on every path that damaged a destructible, and false on every path that
        // skipped the target, so __result plus a bird at the collider means the Aoe hit a bird.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Aoe), nameof(Aoe.OnHit))]
        private static void AoeOnHitPostfix(Aoe __instance, Collider collider, bool __result) {
            if (!__result || !Active() || collider == null) { return; }
            if (__instance.m_gaveSkill || !__instance.m_canRaiseSkill || !CanRaise(__instance.m_skill)) { return; }

            Player player = Player.m_localPlayer;
            if (player == null || __instance.m_owner != player) { return; }

            GameObject target = Projectile.FindHitObject(collider);
            if (target == null || target.GetComponent<RandomFlyingBird>() == null) { return; }

            player.RaiseSkill(__instance.m_skill);
            __instance.m_gaveSkill = true;
        }
    }
}
