using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CommunityPatchExtras.Patches {
    // An "add all" key for smelters, kilns, refineries and the other stations built on the Smelter
    // class: one press fills the input you are looking at from your inventory. Vanilla adds one item
    // per press of the use key, or one every 0.2 s while it is held.
    //
    // WHICH STATIONS. Smelter is the class behind the smelter, blast furnace, charcoal kiln, eitr
    // refinery, spinning wheel, windmill, frost kiln and bathtub, and modded stations that reuse it.
    // Each has up to two inputs, m_addOreSwitch for what it processes and m_addWoodSwitch for its fuel.
    // The key fills the one under the crosshair only, so topping up coal never pours your ore in as
    // well. Fireplaces, cooking stations and fermenters are other classes and are not covered.
    //
    // THROUGH VANILLA'S OWN FEED METHODS. Each item goes in through Smelter.OnAddOre / OnAddFuel, the
    // methods the use key reaches via Switch.Interact, called once per item. So every rule vanilla
    // applies to a press is applied to each item (allowed items, world level, room left), the network
    // sees exactly what that many presses would send, and other mods' hooks on those methods still
    // run. The one that matters most is the Community Patch's Fix Fuel And Ore Loss, which claims the
    // station before feeding it: the RPCs then resolve on this machine, and the queue the next call
    // checks already includes the item before it.
    //
    // THE ROOM IS STILL COUNTED UP FRONT. With that fix turned off and another peer owning the
    // station, the RPCs travel to the owner, and this machine's copy of the queue does not move until
    // the owner's update comes back. Vanilla's "is it full" check would then pass for every item in
    // the inventory, so the loop stops at the room there was before the first item. A second press
    // before the owner's update arrives can still over-fill a little. RPC_AddOre and RPC_AddFuel never
    // check capacity, so the extra is queued rather than lost, the same as holding the use key does
    // in vanilla.
    //
    // THE KEY. Checked in a Player.Update postfix, after vanilla has refreshed what the player is
    // hovering for the frame, so any key combination works, not just one built on the use key. With
    // the default Shift+E the E press also reaches Switch.Interact as an ordinary use. A prefix there
    // stands vanilla down on a station input while the shortcut is held, so that press does not add
    // an extra item, and holding the keys does not keep repeating vanilla's one-at-a-time add (and its
    // "it's full" message) after the fill. Read through ZInput, the game's own input layer. Keyboard
    // only; gamepad controls are unchanged.
    //
    // Client config: this is one player's controls. Nothing reaches the network that pressing the
    // use key the same number of times would not send, so it works on any server, modded or not.
    [HarmonyPatch]
    internal static class StationAddAllPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyboardShortcut> Shortcut;

        // Item names and counts for the closing message, in the order they went in.
        private static readonly List<KeyValuePair<string, int>> Added = new List<KeyValuePair<string, int>>();
        private static readonly StringBuilder Message = new StringBuilder();

        internal static void BindConfig() {
            Enabled = ValConfig.BindClientConfig(
                "Stations",
                "Add All To Stations",
                true,
                "Whether the Add All Shortcut fills a smelter, kiln, refinery, spinning wheel, windmill " +
                "or similar station from your inventory in one press. Look at the input you want filled, " +
                "the ore slot or the fuel slot, and press the shortcut; it adds as much as fits, as if you " +
                "had pressed use that many times. Per-machine; works on any server.");

            Shortcut = ValConfig.BindClientConfig(
                "Stations",
                "Add All Shortcut",
                new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift),
                "The key combination that fills the station input you are looking at. While it is held, " +
                "the use key no longer adds single items to station inputs, so the press is not counted twice.");
        }

        private static bool ShortcutActive(bool pressedThisFrame) {
            KeyboardShortcut shortcut = Shortcut.Value;
            if (shortcut.MainKey == KeyCode.None) { return false; }

            bool mainKey = pressedThisFrame
                ? ZInput.GetKeyDown(shortcut.MainKey, logWarning: false)
                : ZInput.GetKey(shortcut.MainKey, logWarning: false);
            if (!mainKey) { return false; }

            foreach (KeyCode modifier in shortcut.Modifiers) {
                if (!ZInput.GetKey(modifier, logWarning: false)) { return false; }
            }
            return true;
        }

        // A station's ore or fuel input. A placement ghost has the switches but never wired them up
        // (Smelter.Awake returns before that without a ZDO), hence the validity check.
        private static bool IsStationInput(Switch sw, out Smelter smelter, out bool fuel) {
            smelter = null;
            fuel = false;
            if (sw == null) { return false; }

            smelter = sw.GetComponentInParent<Smelter>();
            if (smelter == null || smelter.m_nview == null || !smelter.m_nview.IsValid()) { return false; }

            if (sw == smelter.m_addOreSwitch) { return true; }
            if (sw == smelter.m_addWoodSwitch && smelter.m_fuelItem != null) {
                fuel = true;
                return true;
            }
            return false;
        }

        // Runs for every Player every frame, so the cheap rejections come first: with the shortcut not
        // pressed this frame it costs a bool and one key read.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "Update")]
        private static void PlayerUpdatePostfix(Player __instance) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (__instance != Player.m_localPlayer || !ShortcutActive(pressedThisFrame: true)) { return; }

            // The same gates vanilla puts on the use key (Player.Update and Player.Interact).
            if (!__instance.TakeInput() || Hud.InRadial() || __instance.InAttack() || __instance.InDodge()) { return; }

            GameObject hovering = __instance.GetHoverObject();
            if (hovering == null) { return; }

            // Resolved the way Player.Interact resolves it, so this acts on whatever the use key would.
            Switch sw = hovering.GetComponentInParent<Interactable>() as Switch;
            if (!IsStationInput(sw, out Smelter smelter, out bool fuel)) { return; }

            int total = fuel ? AddAllFuel(__instance, sw, smelter) : AddAllOre(__instance, sw, smelter);
            if (total > 0) { __instance.DoInteractAnimation(sw.gameObject); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Switch), nameof(Switch.Interact))]
        private static bool SwitchInteractPrefix(Switch __instance, Humanoid character, ref bool __result) {
            if (Enabled == null || !Enabled.Value) { return true; }
            if (character != Player.m_localPlayer || !ShortcutActive(pressedThisFrame: false)) { return true; }
            if (!IsStationInput(__instance, out _, out _)) { return true; }

            __result = false;
            return false;
        }

        // The first call always goes to vanilla, so a station that is full, or a player with nothing
        // it accepts, gets vanilla's own message for it. After that the loop stops quietly when the
        // inventory runs out, and the closing message replaces vanilla's per-item one.
        private static int AddAllOre(Player player, Switch sw, Smelter smelter) {
            Inventory inventory = player.GetInventory();
            int room = smelter.m_maxOre - smelter.GetQueueSize();
            Added.Clear();

            int total = 0;
            do {
                // The item vanilla would pick itself, found here so it can be counted by name.
                ItemDrop.ItemData item = smelter.FindCookableItem(inventory);
                if (item == null && total > 0) { break; }

                string name = item?.m_shared.m_name;
                if (!smelter.OnAddOre(sw, player, item)) { break; }

                Count(name);
                total++;
            } while (total < room);

            ShowAdded(player, total);
            return total;
        }

        // Smelter.OnAddFuel calls a station full once its fuel is above max - 1, so the room is the
        // whole units that fit below max: floor(max - fuel) is zero exactly when vanilla says full.
        private static int AddAllFuel(Player player, Switch sw, Smelter smelter) {
            Inventory inventory = player.GetInventory();
            string name = smelter.m_fuelItem.m_itemData.m_shared.m_name;
            int room = Mathf.FloorToInt(smelter.m_maxFuel - smelter.GetFuel());
            Added.Clear();

            int total = 0;
            do {
                if (total > 0 && !inventory.HaveItem(name)) { break; }
                if (!smelter.OnAddFuel(sw, player, null)) { break; }

                Count(name);
                total++;
            } while (total < room);

            ShowAdded(player, total);
            return total;
        }

        private static void Count(string name) {
            int last = Added.Count - 1;
            if (last >= 0 && Added[last].Key == name) {
                Added[last] = new KeyValuePair<string, int>(name, Added[last].Value + 1);
            } else {
                Added.Add(new KeyValuePair<string, int>(name, 1));
            }
        }

        // Vanilla's "$msg_added <item>", with a count per item: "Added 7 Copper ore, 3 Tin ore".
        private static void ShowAdded(Player player, int total) {
            if (total == 0) { return; }

            Message.Clear();
            Message.Append("$msg_added ");
            for (int i = 0; i < Added.Count; i++) {
                if (i > 0) { Message.Append(", "); }
                Message.Append(Added[i].Value).Append(' ').Append(Added[i].Key);
            }
            player.Message(MessageHud.MessageType.Center, Message.ToString());
        }
    }
}
