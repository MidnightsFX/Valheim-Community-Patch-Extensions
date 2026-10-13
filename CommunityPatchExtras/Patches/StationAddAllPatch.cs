using System;
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
    // THE HOVER LINE. Each input's hover text gets a line under vanilla's own, in vanilla's format:
    // "[LeftShift + E] Add 10 Coal". The count is what a press would add right now, worked out the way
    // the fill picks items (conversion order, world level, room left), so it moves as the station
    // fills and as the inventory changes. The line is left off whenever pressing the key would do
    // nothing: the feature turned off, no key set, a gamepad in use, or nothing that would go in.
    // "Add" is vanilla's $piece_smelter_add, the word its fuel line already uses, so it is translated.
    //
    // Client config: this is one player's controls. Nothing reaches the network that pressing the
    // use key the same number of times would not send, so it works on any server, modded or not.
    [HarmonyPatch]
    internal static class StationAddAllPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyboardShortcut> Shortcut;

        // Item names and counts, in the order they go in: what a fill added, for its closing message,
        // or what a press would add, for the hover line. Filled and read within one call of either.
        private static readonly List<KeyValuePair<string, int>> Counts = new List<KeyValuePair<string, int>>();
        private static readonly StringBuilder Text = new StringBuilder();

        // The shortcut as the hover line shows it. Rebuilt only when the setting changes, since the
        // hover text is asked for every frame the crosshair is on a station.
        private static string _shortcutText;

        internal static void BindConfig() {
            Enabled = ValConfig.BindClientConfig(
                "Stations",
                "Add All To Stations",
                true,
                "Whether the Add All Shortcut fills a smelter, kiln, refinery, spinning wheel, windmill " +
                "or similar station from your inventory in one press. Look at the input you want filled, " +
                "the ore slot or the fuel slot, and press the shortcut; it adds as much as fits, as if you " +
                "had pressed use that many times. The station's hover text shows the shortcut and how " +
                "much it would add. Per-machine; works on any server.");

            Shortcut = ValConfig.BindClientConfig(
                "Stations",
                "Add All Shortcut",
                new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift),
                "The key combination that fills the station input you are looking at. While it is held, " +
                "the use key no longer adds single items to station inputs, so the press is not counted twice.");
            Shortcut.SettingChanged += OnShortcutChanged;
        }

        private static void OnShortcutChanged(object sender, EventArgs e) {
            _shortcutText = null;
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

        // Modifiers first, as the keys are pressed. KeyboardShortcut.ToString puts the main key first.
        private static string ShortcutText() {
            if (_shortcutText != null) { return _shortcutText; }

            KeyboardShortcut shortcut = Shortcut.Value;
            StringBuilder text = new StringBuilder();
            foreach (KeyCode modifier in shortcut.Modifiers) {
                text.Append(modifier).Append(" + ");
            }
            text.Append(shortcut.MainKey);

            _shortcutText = text.ToString();
            return _shortcutText;
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

        private static int OreRoom(Smelter smelter) {
            return smelter.m_maxOre - smelter.GetQueueSize();
        }

        // Smelter.OnAddFuel calls a station full once its fuel is above max - 1, so the room is the
        // whole units that fit below max: floor(max - fuel) is zero exactly when vanilla says full.
        private static int FuelRoom(Smelter smelter) {
            return Mathf.FloorToInt(smelter.m_maxFuel - smelter.GetFuel());
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
            int room = OreRoom(smelter);
            Counts.Clear();

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

        private static int AddAllFuel(Player player, Switch sw, Smelter smelter) {
            Inventory inventory = player.GetInventory();
            string name = smelter.m_fuelItem.m_itemData.m_shared.m_name;
            int room = FuelRoom(smelter);
            Counts.Clear();

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
            int last = Counts.Count - 1;
            if (last >= 0 && Counts[last].Key == name) {
                Counts[last] = new KeyValuePair<string, int>(name, Counts[last].Value + 1);
            } else {
                Counts.Add(new KeyValuePair<string, int>(name, 1));
            }
        }

        private static bool Counted(string name) {
            for (int i = 0; i < Counts.Count; i++) {
                if (Counts[i].Key == name) { return true; }
            }
            return false;
        }

        // "7 $item_copperore, 3 $item_tinore"
        private static void AppendCounts() {
            for (int i = 0; i < Counts.Count; i++) {
                if (i > 0) { Text.Append(", "); }
                Text.Append(Counts[i].Value).Append(' ').Append(Counts[i].Key);
            }
        }

        // Vanilla's "$msg_added <item>", with a count per item: "Added 7 Copper ore, 3 Tin ore".
        private static void ShowAdded(Player player, int total) {
            if (total == 0) { return; }

            Text.Clear();
            Text.Append("$msg_added ");
            AppendCounts();
            player.Message(MessageHud.MessageType.Center, Text.ToString());
        }

        // Whether a press could do anything from this machine, before counting what it would add.
        private static bool HintPlayer(out Player player) {
            player = null;
            if (Enabled == null || !Enabled.Value || Shortcut.Value.MainKey == KeyCode.None) { return false; }
            if (ZInput.IsGamepadActive()) { return false; }

            player = Player.m_localPlayer;
            return player != null;
        }

        // What AddAllOre would add: FindCookableItem takes the first conversion whose item is in the
        // inventory until it runs out, then the next. CountItems applies the same world-level filter
        // GetItem does. A source listed twice is counted once.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Smelter), "OnHoverAddOre")]
        private static void OnHoverAddOrePostfix(Smelter __instance, ref string __result) {
            if (!HintPlayer(out Player player)) { return; }

            Inventory inventory = player.GetInventory();
            int room = OreRoom(__instance);
            Counts.Clear();

            foreach (Smelter.ItemConversion conversion in __instance.m_conversion) {
                if (room <= 0) { break; }
                if (conversion.m_from == null) { continue; }

                string name = conversion.m_from.m_itemData.m_shared.m_name;
                if (Counted(name)) { continue; }

                int amount = Mathf.Min(inventory.CountItems(name), room);
                if (amount <= 0) { continue; }

                Counts.Add(new KeyValuePair<string, int>(name, amount));
                room -= amount;
            }

            AppendHint(ref __result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Smelter), "OnHoverAddFuel")]
        private static void OnHoverAddFuelPostfix(Smelter __instance, ref string __result) {
            if (!HintPlayer(out Player player) || __instance.m_fuelItem == null) { return; }

            string name = __instance.m_fuelItem.m_itemData.m_shared.m_name;
            int amount = Mathf.Min(player.GetInventory().CountItems(name), FuelRoom(__instance));
            Counts.Clear();
            if (amount > 0) { Counts.Add(new KeyValuePair<string, int>(name, amount)); }

            AppendHint(ref __result);
        }

        // Vanilla's hover methods return their text already localized, so the line is localized on
        // its own before it is appended.
        private static void AppendHint(ref string hoverText) {
            if (Counts.Count == 0) { return; }

            Text.Clear();
            Text.Append("\n[<color=yellow><b>").Append(ShortcutText()).Append("</b></color>] $piece_smelter_add ");
            AppendCounts();
            hoverText += Localization.instance.Localize(Text.ToString());
        }
    }
}
