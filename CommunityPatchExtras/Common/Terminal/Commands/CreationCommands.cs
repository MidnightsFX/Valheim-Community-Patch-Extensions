using System;
using System.Collections.Generic;
using System.Linq;
using CommunityPatchExtras.Patches;

#pragma warning disable IDE0130
namespace CommunityPatchExtras {
#pragma warning restore IDE0130

    // Reports for Patches/CreationTrackerPatch. One action, registered twice: a client's counts and a
    // dedicated server's are different numbers, and the server's can only be read by relaying.
    internal static partial class TerminalManager {
        private const int CreationDefaultTop = 20;

        private static void RegisterCreationCommands() {
            _ = new ModCommand($"{CommandPrefix}-creations",
                "Format: [optional: total/new/loaded/players] [optional: count/all] [optional: reset] " +
                "Lists the prefabs this machine has created the most of, while Track Object Creation is on.",
                Creations, "diagnostics", CreationOptions);

            _ = new ModCommand($"{CommandPrefix}-creations-server",
                "Format: [optional: total/new/loaded/players] [optional: count/all] [optional: reset] " +
                "Lists the prefabs the server has created the most of, while its Track Object Creation is on.",
                Creations, "diagnostics", CreationOptions,
                serverAuthoritative: true, requiresAdmin: true);
        }

        private static List<string> CreationOptions(string[] input) {
            List<string> options = TerminalArgs.Names<CreationTrackerPatch.Sort>().Select(n => n.ToLowerInvariant()).ToList();
            options.AddRange(new[] { "10", "50", "all", "reset" });
            return options;
        }

        // Arguments in any order: a sort column, a count or 'all', and 'reset' to start over.
        private static void Creations(ModCommandArgs args) {
            CreationTrackerPatch.Sort sort = CreationTrackerPatch.Sort.Total;
            int top = CreationDefaultTop;
            foreach (string arg in args.Args) {
                if (string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase)) {
                    CreationTrackerPatch.Reset();
                    args.Output.Info("Creation counts cleared.");
                    return;
                }
                if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase)) {
                    top = 0;
                } else if (int.TryParse(arg, out int parsed)) {
                    top = parsed;
                } else if (Enum.TryParse(arg, true, out CreationTrackerPatch.Sort parsedSort)) {
                    sort = parsedSort;
                } else if (arg.Length > 0) {
                    args.Output.Warning($"Ignored '{arg}': expected total, new, loaded, players, a count, all or reset.");
                }
            }

            CreationTrackerPatch.Report(args.Output, sort, top);
        }
    }
}
