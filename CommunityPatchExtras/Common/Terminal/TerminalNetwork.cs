using Jotunn.Entities;
using Jotunn.Managers;
using System.Collections;
using UnityEngine;

#pragma warning disable IDE0130
namespace CommunityPatchExtras {
#pragma warning restore IDE0130

    // The admin-client -> server command relay.
    //
    // Needed because a dedicated server has no Terminal of its own -- Console.Awake and
    // Terminal.InitTerminal only ever run on a client -- so a command that acts on server-owned state
    // is otherwise untypeable anywhere. Vanilla does have a relay (ConsoleCommand remoteCommand ->
    // ZNet.RPC_RemoteCommand), but it ends in Console.instance.TryRunCommand and null-references
    // headless, so it cannot be used for this.
    internal static class TerminalNetwork {
        private static CustomRPC commandRequestRPC;
        private static CustomRPC commandOutputRPC;

        internal static void Init() {
            // Client -> server: run this command. Server -> client: the output it produced.
            commandRequestRPC = NetworkManager.Instance.AddRPC(
                CommunityPatchExtras.PluginName + "_CommandRequest", OnServerReceiveCommandRequest, NoOp);
            commandOutputRPC = NetworkManager.Instance.AddRPC(
                CommunityPatchExtras.PluginName + "_CommandOutput", NoOp, OnClientReceiveCommandOutput);
        }

        private static IEnumerator NoOp(long sender, ZPackage package) { yield break; }

        internal static void SendRequest(long serverUid, string command, string[] args) {
            ZPackage package = new ZPackage();
            package.Write(command);
            package.Write(args.Length);
            foreach (string arg in args) { package.Write(arg); }

            // The requesting player's position travels with the request: it is the only centre point a
            // dedicated server can act around, having no local player of its own.
            bool hasCenter = Player.m_localPlayer != null;
            package.Write(hasCenter);
            package.Write(hasCenter ? Player.m_localPlayer.transform.position : Vector3.zero);

            commandRequestRPC.SendPackage(serverUid, package);
        }

        internal static void SendOutput(long peerUid, ZPackage package) {
            commandOutputRPC.SendPackage(peerUid, package);
        }

        // Server handler. Gate on admin because any peer could craft this RPC; the client-side check is
        // only there to give a clearer message.
        private static IEnumerator OnServerReceiveCommandRequest(long sender, ZPackage package) {
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) { yield break; }

            string command = package.ReadString();
            if (RequiresAdmin(command) && SenderIsAdmin(sender) == false) {
                Logger.LogWarning($"Rejecting '{command}' from non-admin peer {sender}.");
                // Answer rather than going quiet, so the sender sees a refusal instead of nothing.
                TerminalOutput refusal = TerminalOutput.Remote(sender);
                refusal.Error($"Only server admins can run {command}.", log: false);
                refusal.Flush();
                yield break;
            }

            int argCount = package.ReadInt();
            string[] args = new string[argCount];
            for (int i = 0; i < argCount; i++) { args[i] = package.ReadString(); }
            bool hasCenter = package.ReadBool();
            Vector3 center = package.ReadVector3();

            TerminalManager.ExecuteFromNetwork(command, args, center, hasCenter, TerminalOutput.Remote(sender));
            yield return null;
        }

        // Client handler: a batch of output lines from a command this client asked the server to run.
        // Severity travels as a byte and the colour is applied on arrival, so the server's log never
        // contains markup and each client honours its own EnableTerminalColors setting.
        private static IEnumerator OnClientReceiveCommandOutput(long sender, ZPackage package) {
            int count = package.ReadInt();
            for (int i = 0; i < count; i++) {
                OutputLevel level = (OutputLevel)package.ReadByte();
                TerminalManager.PrintResponse(level, package.ReadString());
            }
            yield return null;
        }

        // Unknown names default to admin-only: an unregistered command is rejected downstream anyway, and
        // this way a typo can never be treated as public.
        private static bool RequiresAdmin(string command) {
            return TerminalManager.Registry.TryGetValue(command.ToLowerInvariant(), out ModCommand found) == false
                || found.RequiresAdmin;
        }

        // True when the peer uid belongs to a connected admin. The integrated host never routes through
        // an RPC, so it is not considered here.
        private static bool SenderIsAdmin(long sender) {
            ZNetPeer peer = ZNet.instance?.GetPeer(sender);
            if (peer == null || peer.m_socket == null) { return false; }
            return ZNet.instance.IsAdmin(peer.m_socket.GetHostName());
        }
    }
}
