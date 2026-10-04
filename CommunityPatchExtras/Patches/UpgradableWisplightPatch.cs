using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace CommunityPatchExtras.Patches {
    // Lets the Wisplight be upgraded at the workbench, each level pushing the mist back further.
    // Vanilla ships it at max quality 1, so it never shows on the upgrade tab and its reach is fixed
    // for the whole of the Mistlands.
    //
    // WHERE THE REACH LIVES. Equipping the Wisplight adds SE_Demister (Demister.asset), whose
    // UpdateStatusEffect instantiates demister_ball and then steers it around the wearer. The ball's
    // "Particle System Force Field" child carries a Demister and a ParticleSystemForceField, and that
    // field's endRange (10 m in the Deep North rip) is the whole mechanic: the field pushes mist
    // particles out of its sphere, and ParticleMist reads the same endRange for where new mist may be
    // emitted and for IsInMist / IsMistBlocked, which BaseAI uses to decide whether a creature can see
    // through the mist. Scaling endRange scales all of it at once.
    //
    // UPGRADES ARE LENT, NOT SET. Two serialized values gate an upgrade: the item's
    // SharedData.m_maxQuality (InventoryGui.UpdateRecipeList skips anything at 1) and each recipe
    // requirement's m_amountPerLevel. Both sit on objects shared by every copy of the item that also
    // outlive a world load, so they are only written while the feature is active and are put back
    // exactly as found otherwise. Refresh restores and then lends again, so it is safe to call any
    // number of times, and it runs at each point the answer can change: Game.Start, the setting
    // changing, and Jotunn confirming the server's config.
    //  - Max quality 4, vanilla's usual cap. Recipe.GetRequiredStationLevel asks for one more workbench
    //    level per quality, so the last upgrade needs a level 4 workbench.
    //  - Each requirement's per-level amount becomes three times what crafting takes it (Silver and
    //    Wisp, 1 each in vanilla). Piece.Requirement.GetAmount then scales that by level the same way
    //    it does for every other item. Read from the recipe at the time, so a recipe another mod has
    //    changed stays three times that recipe.
    //  - The upgrade itself is vanilla's. InventoryGui.DoCrafting unequips the old Wisplight and hands
    //    back a new one a quality higher, so equipping it again is what makes a ball at the new reach.
    //
    // THE REACH IS MULTIPLIED, ONCE PER BALL: 1 + per-level x (quality - 1). It multiplies the endRange
    // the ball already has and never sets it, and that is the whole of the Epic Loot compatibility. Its
    // Demisting enchantment (ModifyWispRange) also multiplies endRange once per ball, under its own ZDO
    // key, so the two stack as a product in either order and neither needs to know about the other.
    // Each copy of a ball is scaled in exactly one of two places:
    //  - The wearer's, in a postfix on the SE_Demister update that created it. The multiplier is also
    //    written to the ball's ZDO.
    //  - Everyone else's, in a postfix on ZNetScene.CreateObject, which builds their copy from that ZDO.
    //    After Instantiate rather than in Demister.Awake, because the Demister sits two levels below
    //    the ZNetView and nothing promises the ZNetView has been handed its ZDO by then. The wearer's
    //    own ball never comes through CreateObject, and nobody else's copy can be built before the
    //    multiplier is on the ZDO: it is written in the same frame the ZDO is created, before any send.
    // Other machines need the right reach too, not just the wearer's: mist is drawn per machine, and a
    // creature's sight check runs on whichever machine owns that creature.
    //
    // A ball already in the world is not revisited. Changing the per-level amount, or turning the
    // feature off, applies from the next ball, which is the next time the Wisplight is equipped.
    //
    // SERVER-SYNCED, and held at vanilla on a server that does not run this mod, the same way as
    // VehicleDeconstructPatch: nothing is lent and no multiplier is written. A Wisplight upgraded on
    // another server keeps its quality there, at vanilla reach.
    [HarmonyPatch]
    internal static class UpgradableWisplightPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> RangePerLevel;

        private const string WisplightPrefab = "Demister";
        private const int MaxQuality = 4;
        private const int UpgradeCostMultiplier = 3;

        // Our own key, apart from Epic Loot's "el-demist", so each mod reads back only its own factor.
        private static readonly int RangeMultiplierKey = "CPE_WisplightRangeMultiplier".GetStableHashCode();

        // The ZNet instance whose session Jotunn confirmed this mod's config for. Identity-compared so
        // a new session invalidates it.
        private static ZNet _confirmedFor;

        // What Lend changed, so Restore can hand back exactly the values it found.
        private static ItemDrop.ItemData.SharedData _lentShared;
        private static int _originalMaxQuality;
        private static readonly Dictionary<Piece.Requirement, int> LentPerLevel = new Dictionary<Piece.Requirement, int>();

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                "Equipment",
                "Upgradable Wisplight",
                false,
                "Whether the Wisplight can be upgraded at the workbench, up to level 4. Each level " +
                "pushes the mist back further, by Wisplight Range Per Level, and costs three times " +
                "the Wisplight's crafting materials per level. Equip it again after upgrading. Stacks " +
                "with other mods that widen the Wisplight's reach, such as Epic Loot's Demisting " +
                "enchantment. Turning this off leaves upgraded Wisplights at their level but back at " +
                "vanilla reach. Server-synced; a client connected to a server without this mod keeps " +
                "vanilla behaviour.");

            // Capped at 100 because mist is re-emitted around the edge of the cleared sphere, so the
            // particle work grows with the square of the reach.
            RangePerLevel = ValConfig.BindServerConfig(
                "Equipment",
                "Wisplight Range Per Level",
                30f,
                "How much further each Wisplight upgrade pushes the mist back, as a percentage of its " +
                "reach before upgrading. At 30, levels 2, 3 and 4 clear 30%, 60% and 90% further. " +
                "Takes effect the next time the Wisplight is equipped. Ignored when Upgradable " +
                "Wisplight is off.",
                false,
                0f,
                100f);

            // Not through ConfigChangeDebouncer: Refresh is a handful of field writes, nothing to coalesce.
            Enabled.SettingChanged += (sender, args) => Refresh();
            SynchronizationManager.OnConfigurationSynchronized += OnConfigurationSynchronized;
        }

        private static void OnConfigurationSynchronized(object sender, ConfigurationSynchronizationEventArgs args) {
            if (args.UpdatedPluginGUIDs == null || !args.UpdatedPluginGUIDs.Contains(CommunityPatchExtras.PluginGUID)) { return; }

            if (ZNet.instance != null && !ZNet.instance.IsServer()) { _confirmedFor = ZNet.instance; }
            Refresh();
        }

        private static bool Active() {
            if (Enabled == null || !Enabled.Value) { return false; }

            ZNet znet = ZNet.instance;
            if (znet == null) { return false; }

            return znet.IsServer() || ReferenceEquals(_confirmedFor, znet);
        }

        // After every Awake in the scene, so ObjectDB is filled, Jotunn's items and recipes included. A
        // host is active from here; a client stays restored until the server's config arrives.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), "Start")]
        private static void GameStartPostfix() => Refresh();

        private static void Refresh() {
            Restore();
            if (Active()) { Lend(); }
        }

        private static void Restore() {
            if (_lentShared != null) {
                _lentShared.m_maxQuality = _originalMaxQuality;
                _lentShared = null;
            }

            foreach (KeyValuePair<Piece.Requirement, int> lent in LentPerLevel) {
                lent.Key.m_amountPerLevel = lent.Value;
            }
            LentPerLevel.Clear();
        }

        private static void Lend() {
            ObjectDB db = ObjectDB.instance;
            if (db == null) { return; }

            GameObject prefab = db.GetItemPrefab(WisplightPrefab);
            ItemDrop item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (item == null) { return; }

            ItemDrop.ItemData.SharedData shared = item.m_itemData.m_shared;
            _lentShared = shared;
            _originalMaxQuality = shared.m_maxQuality;
            shared.m_maxQuality = MaxQuality;

            // Matched by item name, as ObjectDB.GetRecipe does, and every match, so a second recipe
            // another mod adds for the Wisplight upgrades at the same rate.
            foreach (Recipe recipe in db.m_recipes) {
                if (recipe == null || recipe.m_item == null || recipe.m_resources == null) { continue; }
                if (recipe.m_item.m_itemData.m_shared.m_name != shared.m_name) { continue; }

                foreach (Piece.Requirement requirement in recipe.m_resources) {
                    if (requirement == null || LentPerLevel.ContainsKey(requirement)) { continue; }

                    LentPerLevel[requirement] = requirement.m_amountPerLevel;
                    requirement.m_amountPerLevel = requirement.m_amount * UpgradeCostMultiplier;
                }
            }
        }

        // SE_Demister builds its ball on the first update with none, then returns, so a null ball going
        // in and a ball coming out means this call made it. Every later update costs one null check.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SE_Demister), nameof(SE_Demister.UpdateStatusEffect))]
        private static void UpdateStatusEffectPrefix(SE_Demister __instance, out bool __state) {
            __state = __instance.m_ballInstance == null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SE_Demister), nameof(SE_Demister.UpdateStatusEffect))]
        private static void UpdateStatusEffectPostfix(SE_Demister __instance, bool __state) {
            if (!__state || __instance.m_ballInstance == null || !Active()) { return; }

            float multiplier = RangeMultiplier(__instance.m_character as Humanoid);
            if (multiplier <= 1f) { return; }

            ZNetView nview = __instance.m_ballInstance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }

            nview.GetZDO().Set(RangeMultiplierKey, multiplier);
            Scale(__instance.m_ballInstance, multiplier);
        }

        // Runs for every object a zone load builds, so it is a single ZDO lookup for anything that is
        // not one of these balls. Not gated on Active(): only an active wearer ever writes the key.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        private static void CreateObjectPostfix(GameObject __result, ZDO zdo) {
            if (__result == null || zdo == null) { return; }

            float multiplier = zdo.GetFloat(RangeMultiplierKey, 0f);
            if (multiplier <= 1f) { return; }

            Scale(__result, multiplier);
        }

        // The quality of the Wisplight the wearer has on. Searched among all equipped items rather
        // than the utility slot, so a mod that adds slots of its own is covered. Clamped to the
        // quality this patch lends, so a spawned-in higher one cannot reach further than an upgraded one.
        private static float RangeMultiplier(Humanoid wearer) {
            if (wearer == null || RangePerLevel == null) { return 1f; }

            int quality = 1;
            foreach (ItemDrop.ItemData item in wearer.GetInventory().GetEquippedItems()) {
                if (item.m_dropPrefab != null && item.m_dropPrefab.name == WisplightPrefab) {
                    quality = Mathf.Max(quality, item.m_quality);
                }
            }

            return 1f + RangePerLevel.Value / 100f * (Mathf.Min(quality, MaxQuality) - 1);
        }

        private static void Scale(GameObject ball, float multiplier) {
            foreach (Demister demister in ball.GetComponentsInChildren<Demister>()) {
                ParticleSystemForceField field = demister.GetComponent<ParticleSystemForceField>();
                if (field != null) { field.endRange *= multiplier; }
            }
        }
    }
}
