using System.Collections.Generic;
using System.Linq;

#pragma warning disable IDE0130
namespace JotunnModStub {
#pragma warning restore IDE0130

    // Worked examples, one per framework feature. Delete these once you have your own commands; keep
    // RegisterHelpCommand().
    internal static partial class TerminalManager {
        // Called from Init(). Add your own Register*() calls here.
        private static void RegisterCommands() {
            RegisterHelpCommand();
            RegisterExampleCommands();
        }

        private static void RegisterExampleCommands() {
            // 1. Arguments and the four output severities. The first argument completes to a severity,
            //    the rest is free text.
            _ = new ModCommand($"{CommandPrefix}-echo",
                "Format: [info/detail/warning/error] [message] Prints a message back at the given severity.",
                Echo, "demo", EchoOptions,
                aliases: $"{CommandPrefix}-e");

            // 2. Acting around the player, with a clamped radius argument. HasCenter is false when there
            //    is no player to centre on (a headless server running this locally).
            _ = new ModCommand($"{CommandPrefix}-nearby",
                "Format: [optional: radius] Counts the creatures around you.",
                Nearby, "demo", TerminalArgs.RadiusPresets);

            // 3. Server-authoritative: a client cannot answer this locally, so the request is relayed to
            //    the server, which runs it and streams the output back into the client's console. This is
            //    the only way such a command is reachable on a dedicated server, which has no console.
            _ = new ModCommand($"{CommandPrefix}-worldinfo",
                "Reports server-side world state. Admins can run this from a connected client.",
                WorldInfo, "world",
                serverAuthoritative: true, requiresAdmin: true);
        }

        private static List<string> EchoOptions(string[] input) {
            // input is the whole line split on spaces, so options can differ per argument position.
            return input.Length <= 2 ? TerminalArgs.Names<OutputLevel>() : new List<string>();
        }

        private static void Echo(ModCommandArgs args) {
            if (args.Length < 2) {
                args.Output.Error($"Usage: {CommandPrefix}-echo [info/detail/warning/error] [message]");
                return;
            }
            OutputLevel level = args.Args.GetEnum(0, OutputLevel.Info);
            args.Output.Write(level, args.Args.GetStringFrom(1));
        }

        private static void Nearby(ModCommandArgs args) {
            if (args.HasCenter == false) {
                args.Output.Error("This needs a player position to measure from.");
                return;
            }
            float radius = args.ReadRadius(0, 32f, 256f);
            int count = Character.GetAllCharacters()
                .Count(c => c != null && c.IsPlayer() == false
                    && Utils.DistanceXZ(c.transform.position, args.Center) <= radius);
            args.Output.Info($"{count} creatures within {radius}m.");
        }

        private static void WorldInfo(ModCommandArgs args) {
            // Runs on the server, whether that is an integrated host or a dedicated server answering a
            // relayed request. args.Center is the requesting player's position either way.
            args.Output.Info($"World      : {ZNet.instance.GetWorldName()}");
            args.Output.Detail($"Seed       : {ZNet.m_world?.m_seedName}");
            args.Output.Detail($"Peers      : {ZNet.instance.GetPeers().Count}");
            args.Output.Detail($"ZDOs       : {(ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() : 0)}");
            args.Output.Detail($"Zones      : {(ZoneSystem.instance != null ? ZoneSystem.instance.m_generatedZones.Count : 0)}");
            if (args.HasCenter) {
                args.Output.Detail($"You are at : {args.Center.x:0}, {args.Center.z:0}");
            }
        }
    }
}
