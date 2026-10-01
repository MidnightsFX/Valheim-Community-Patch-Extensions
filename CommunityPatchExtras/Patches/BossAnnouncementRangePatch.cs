using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace CommunityPatchExtras.Patches {
    // Shows a boss's announcements only to the players near it. Vanilla sends them to every player
    // on the server, so someone building at home gets a centre-screen banner each time anyone,
    // anywhere in the world, summons, wakes or kills a boss.
    //
    // THREE MESSAGES, ALL ON BaseAI. Each is a serialized string on the creature's BaseAI, and each
    // is sent with MessageHud.MessageAll, which is a routed "ShowMessage" RPC to every peer:
    //  - m_spawnMessage, from Awake, on the owner, once: only while the ZDO's spawn time is still
    //    unset, i.e. the creature was just created. This is the summon message. Eikthyr, the Elder,
    //    Bonemass, Moder, Yagluth and the Hive.
    //  - m_alertedMessage, from SetAlerted, once per creature (ZDO s_shownAlertMessage). The bosses
    //    that are not summoned but wait in a room use this one instead: the Queen, Fader and the
    //    Frozen King.
    //  - m_deathMessage, from OnDeath, on the owner. All of the above.
    // (Checked against the Deep North rip: every prefab that sets one of the three is a boss, the
    // Hive included.) So swapping those three calls covers exactly the boss announcements, and a
    // modded creature that sets one is treated the same. Other centre messages are not touched:
    // BossStone's "completed" message is already vanilla-limited to 20 m, and the Fimbulvinter orb
    // (TriggerPersistentEventOnDestroy) starts a world event that is meant for everyone.
    //
    // THE SERVER DECIDES WHO IS NEAR. The owner sending the message is usually a client, and a
    // client only knows where the players it has loaded are. The server has every peer's reference
    // position, ZNetPeer.m_refPos, which each client sends every two seconds: its player, or its
    // camera while dead. So a transpiler swaps each MessageAll call for one that sends the text and
    // the boss's position to the server, and the server sends vanilla's own "ShowMessage" to each
    // peer in range. Because it arrives through the same RPC, a client without this mod still gets
    // the message when it is near, and other mods that patch ShowMessage still see it.
    //
    // The distance is measured flat (XZ). Dungeon interiors are placed 5000 m above their entrance,
    // and the Queen is fought inside one, so a 3D distance would leave out a party at the door.
    //
    // SERVER-SYNCED, and held at vanilla on a server that does not run this mod: that server would
    // drop the relay and nobody would see the message at all. Same per-session confirmation as
    // VehicleDeconstructPatch. The relay RPC is new in 0.4.0, and VersionStrictness.Minor keeps a
    // client from joining an older server that lacks it. A client without this mod that owns a boss
    // still broadcasts to everyone, as vanilla does; nothing here can stop that.
    [HarmonyPatch]
    internal static class BossAnnouncementRangePatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Range;

        private const string RpcName = "CPE_BossAnnouncement";
        private const string ShowMessageRpc = "ShowMessage";

        // The ZNet instance whose session Jotunn confirmed this mod's config for. Identity-compared so
        // a new session invalidates it.
        private static ZNet _confirmedFor;

        private static readonly MethodInfo MessageAllMethod =
            AccessTools.Method(typeof(MessageHud), nameof(MessageHud.MessageAll));
        private static readonly MethodInfo AnnounceMethod =
            AccessTools.Method(typeof(BossAnnouncementRangePatch), nameof(Announce));

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                "Bosses",
                "Limit Boss Announcements",
                true,
                "Whether a boss's announcements, when it is summoned, when it wakes and when it is " +
                "defeated, are shown only to players near it. Vanilla shows them to every player on " +
                "the server, wherever they are. Server-synced; a client connected to a server " +
                "without this mod keeps vanilla behaviour.");

            Range = ValConfig.BindServerConfig(
                "Bosses",
                "Boss Announcement Range",
                300f,
                "How close to the boss a player must be to see its announcements, in metres. The " +
                "default matches the Community Patch's Boss Defeat Key Range, so everyone credited " +
                "with a kill also sees it announced. 0 shows them to nobody. Ignored when Limit Boss " +
                "Announcements is off.",
                false,
                0f,
                2000f);

            SynchronizationManager.OnConfigurationSynchronized += OnConfigurationSynchronized;
        }

        private static void OnConfigurationSynchronized(object sender, ConfigurationSynchronizationEventArgs args) {
            if (args.UpdatedPluginGUIDs == null || !args.UpdatedPluginGUIDs.Contains(CommunityPatchExtras.PluginGUID)) { return; }

            if (ZNet.instance != null && !ZNet.instance.IsServer()) { _confirmedFor = ZNet.instance; }
        }

        private static bool Active() {
            if (Enabled == null || !Enabled.Value) { return false; }

            ZNet znet = ZNet.instance;
            if (znet == null || ZRoutedRpc.instance == null) { return false; }

            return znet.IsServer() || ReferenceEquals(_confirmedFor, znet);
        }

        // Game.Start is where vanilla registers its own routed RPCs. Registered everywhere, acted on
        // only by the server. Guarded because ZRoutedRpc.Register throws on a second registration.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), "Start")]
        private static void GameStartPostfix() {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null) { return; }
            if (rpc.m_functions.ContainsKey(RpcName.GetStableHashCode())) { return; }

            rpc.Register<int, string, Vector3>(RpcName, RPC_BossAnnouncement);
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(BaseAI), "Awake")]
        private static IEnumerable<CodeInstruction> AwakeTranspiler(IEnumerable<CodeInstruction> instructions) =>
            RouteMessageAll(instructions, "BaseAI.Awake");

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(BaseAI), "SetAlerted")]
        private static IEnumerable<CodeInstruction> SetAlertedTranspiler(IEnumerable<CodeInstruction> instructions) =>
            RouteMessageAll(instructions, "BaseAI.SetAlerted");

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(BaseAI), "OnDeath")]
        private static IEnumerable<CodeInstruction> OnDeathTranspiler(IEnumerable<CodeInstruction> instructions) =>
            RouteMessageAll(instructions, "BaseAI.OnDeath");

        // Turns `hud.MessageAll(type, text)` into `Announce(hud, type, text, this)`: the same stack
        // plus the BaseAI, so the replacement can fall back to vanilla's exact call.
        private static IEnumerable<CodeInstruction> RouteMessageAll(IEnumerable<CodeInstruction> instructions, string method) {
            List<CodeInstruction> codes = new List<CodeInstruction>();
            int calls = 0;
            foreach (CodeInstruction instruction in instructions) {
                CodeInstruction code = new CodeInstruction(instruction);
                if (code.Calls(MessageAllMethod)) {
                    codes.Add(new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code));
                    codes.Add(new CodeInstruction(OpCodes.Call, AnnounceMethod));
                    calls++;
                } else {
                    codes.Add(code);
                }
            }

            if (calls == 1) { return codes; }

            Logger.LogWarning(
                $"{method}: expected one call to MessageHud.MessageAll, found {calls}, so Limit Boss " +
                "Announcements does not cover this message. A Valheim update or another mod has most " +
                "likely changed the method.");
            return instructions;
        }

        private static void Announce(MessageHud hud, MessageHud.MessageType type, string text, BaseAI ai) {
            if (!Active()) {
                hud.MessageAll(type, text);
                return;
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(RpcName, (int)type, text, ai.transform.position);
        }

        private static void RPC_BossAnnouncement(long sender, int type, string text, Vector3 position) {
            ZNet znet = ZNet.instance;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (znet == null || rpc == null || !znet.IsServer()) { return; }

            // Turned off here while the sender still had it on, before the config sync reached it.
            if (Enabled == null || !Enabled.Value) {
                rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, ShowMessageRpc, type, text);
                return;
            }

            float range = Range.Value;
            foreach (ZNetPeer peer in znet.GetPeers()) {
                if (!peer.IsReady() || !InRange(peer.m_refPos, position, range)) { continue; }

                rpc.InvokeRoutedRPC(peer.m_uid, ShowMessageRpc, type, text);
            }

            // A listen server's own player is not one of its peers.
            if (!znet.IsDedicated() && InRange(znet.GetReferencePosition(), position, range)) {
                rpc.InvokeRoutedRPC(ZNet.GetUID(), ShowMessageRpc, type, text);
            }
        }

        private static bool InRange(Vector3 player, Vector3 boss, float range) =>
            range > 0f && Utils.DistanceXZ(player, boss) <= range;
    }
}
