using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;

namespace CommunityPatchExtras.Patches {
    // Lets Hildir buy spare copies of her quest chests once she already has one. Vanilla has no use
    // for a second chest: give her one she already has and she only tells you so, and the chest
    // stays in your inventory as 200 unteleportable weight with nothing left to do but drop it.
    // Spares are not rare. Each of her three dungeons is placed up to three times per world
    // (m_quantity 3 in _LocationList_Hildir, Deep North rip), every boss room spawns the boss that
    // carries the chest regardless of world keys, and on a server anyone can run them.
    //
    // THE FIRST ONE IS STILL A GIFT. A chest becomes sellable only once the quest it belongs to is
    // done, and "done" is exactly the test vanilla's Trader.UseItem uses to answer "already have
    // that one": the global key that chest's use-item sets (Trader.cs:310). So selling can never
    // stand in for handing it over. Until the key is set the sell button ignores the chest and Give
    // is the only thing it is good for, so there is no way to sell away the copy that unlocks her
    // stock. It is per chest, not per quest line: returning the Brass Chest opens up selling spare
    // Brass Chests only, and the other two stay quest items until each is handed in.
    //
    // THE GATE COMES FROM THE TRADER, THE PRICES NAME THE CHESTS. Hildir's prefab lists her three
    // chests in Trader.m_useItems, each with the key it sets (Hildir1..3), and the gate walks that
    // list rather than hardcoding keys. Only the prices name chests, by prefab, because a price per
    // kind has to hang on something:
    //   chest_hildir1  Hildir's Brass Chest   Smouldering Tomb, Black Forest
    //   chest_hildir2  Hildir's Silver Chest  Howling Cavern, Mountains
    //   chest_hildir3  Hildir's Bronze Chest  Sealed Tower, Plains
    // (names from the Deep North rip's localization.txt). A chest another mod adds to her list has
    // no price here and is left alone. The same list is what keeps it Hildir's: Haldor and the Bog
    // Witch ship an empty m_useItems, so there is never a chest they would buy.
    //
    // WHY THE VALUE IS LENT, NOT SET. Vanilla's sell button sells the first inventory item whose
    // SharedData.m_value is above zero (Inventory.GetValuableItems) and pays m_value for it. The
    // chests ship at 0, which is the whole reason they are never offered. Giving them a value for
    // good would offer them to every trader, before the quest as well, and print a value in the
    // tooltip. So instead:
    //  - GetSellableItem gets a postfix that offers a returned chest when vanilla found nothing to
    //    sell. That alone lights the sell button.
    //  - SellItem gets the chest's m_value set to the price for that one call, and put back in a
    //    finalizer. Vanilla's own sale then runs unchanged - the coins, the "sold" message, the
    //    effects, Hildir's sell line, the cheated-item flag - and none of it is copied here.
    // SharedData is a single object per prefab, shared by every copy of the item, so for the length
    // of that synchronous call every chest of that type is worth the price. Nothing else runs in
    // between, and the finalizer puts it back even if the sale throws.
    //
    // Vanilla valuables go first: the postfix only fills in when vanilla found nothing, so a player
    // carrying gems and a spare chest sells exactly what vanilla would until the gems are gone.
    // Vanilla's button has never said what it will sell next, and this does not change that.
    //
    // SERVER-SYNCED, and held at vanilla on a server that does not run this mod, the same way as
    // VehicleDeconstructPatch. A sale is a local inventory change that no other machine checks, so
    // nothing can desync either way. But the price is an economy setting, and a client should not be
    // minting coins on a server whose admin never opted in.
    [HarmonyPatch]
    internal static class HildirChestSalePatch {
        internal static ConfigEntry<bool> Enabled;

        // Price per chest kind, keyed by the prefab its use-item on Hildir points at.
        private static readonly Dictionary<string, ConfigEntry<int>> Prices = new Dictionary<string, ConfigEntry<int>>();

        // The ZNet instance whose session Jotunn confirmed this mod's config for. Identity-compared so
        // a new session invalidates it.
        private static ZNet _confirmedFor;

        // What SellItemPrefix lent, so the finalizer can hand back exactly the value it found.
        private sealed class Loan {
            internal ItemDrop.ItemData.SharedData Shared;
            internal int OriginalValue;
        }

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                "Traders",
                "Hildir Buys Spare Chests",
                true,
                "Whether Hildir will buy extra copies of her quest chests. Vanilla has no use for a " +
                "second chest: she only tells you she already has it, and it cannot be sold " +
                "anywhere. On, a chest becomes sellable to Hildir once that same chest has been " +
                "handed to her, so the first one of each still has to be given to unlock her stock. " +
                "Each chest counts separately: handing in one does not make the other two " +
                "sellable. Uses the ordinary sell button, after any gems or other valuables. " +
                "Prices are set per chest below. Server-synced; a client connected to a server " +
                "without this mod keeps vanilla behaviour.");

            BindPrice("chest_hildir1", "Brass Chest Price", 500, "Brass Chest", "Smouldering Tomb");
            BindPrice("chest_hildir2", "Silver Chest Price", 700, "Silver Chest", "Howling Cavern");
            BindPrice("chest_hildir3", "Bronze Chest Price", 900, "Bronze Chest", "Sealed Tower");

            SynchronizationManager.OnConfigurationSynchronized += OnConfigurationSynchronized;
        }

        private static void BindPrice(string prefab, string key, int value, string chest, string dungeon) {
            Prices[prefab] = ValConfig.BindServerConfig(
                "Traders",
                key,
                value,
                $"Coins Hildir pays for each spare Hildir's {chest}, the chest from the {dungeon}, " +
                "once the first one has been handed in to her. For scale, the most a trader pays " +
                "for any vanilla valuable is 175. Ignored when Hildir Buys Spare Chests is off.",
                false,
                1,
                5000);
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

        // What the trader pays for this item, or 0 when it is not a chest she has already been given.
        // Mirrors Trader.UseItem exactly: the first use-item with a matching name decides, and
        // "given" means the key it sets is already set. Use-items that set no key are not quests, and
        // one with no price entry is not a chest this patch knows.
        //
        // Anything at or below 0 means "not for sale" to both callers. A price of 0 would lend
        // nothing, and vanilla would then sell the chest for no coins. The config range already rules
        // that out; this keeps a hand-edited file from getting there.
        private static int SalePrice(Trader trader, ItemDrop.ItemData item) {
            foreach (Trader.TraderUseItem useItem in trader.m_useItems) {
                if (useItem.m_prefab == null) { continue; }
                if (item.m_shared.m_name != useItem.m_prefab.m_itemData.m_shared.m_name) { continue; }

                if (string.IsNullOrEmpty(useItem.m_setsGlobalKey)) { return 0; }
                if (ZoneSystem.instance == null || !ZoneSystem.instance.GetGlobalKey(useItem.m_setsGlobalKey)) { return 0; }
                if (!Prices.TryGetValue(useItem.m_prefab.name, out ConfigEntry<int> price)) { return 0; }

                return price.Value;
            }
            return 0;
        }

        // Runs every frame the store is open, via UpdateSellButton. Cheap: it only gets past the first
        // line when vanilla found nothing to sell, and then only walks the inventory once against a
        // three-entry list.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StoreGui), "GetSellableItem")]
        private static void GetSellableItemPostfix(StoreGui __instance, ref ItemDrop.ItemData __result) {
            if (__result != null || !Active()) { return; }

            Trader trader = __instance.m_trader;
            if (trader == null || trader.m_useItems.Count == 0) { return; }

            foreach (ItemDrop.ItemData item in Player.m_localPlayer.GetInventory().GetAllItems()) {
                if (SalePrice(trader, item) > 0) {
                    __result = item;
                    return;
                }
            }
        }

        // Asks the same question the sale is about to ask. Anything already worth coins is vanilla's
        // own sale and is left alone. A worthless answer can only have come from the postfix above,
        // so that is the chest to lend its kind's price to.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StoreGui), "SellItem")]
        private static void SellItemPrefix(StoreGui __instance, out Loan __state) {
            __state = null;
            if (!Active()) { return; }

            Trader trader = __instance.m_trader;
            if (trader == null) { return; }

            ItemDrop.ItemData item = __instance.GetSellableItem();
            if (item == null || item.m_shared.m_value > 0) { return; }

            int price = SalePrice(trader, item);
            if (price <= 0) { return; }

            __state = new Loan { Shared = item.m_shared, OriginalValue = item.m_shared.m_value };
            item.m_shared.m_value = price;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(StoreGui), "SellItem")]
        private static void SellItemFinalizer(Loan __state) {
            if (__state != null) { __state.Shared.m_value = __state.OriginalValue; }
        }
    }
}
