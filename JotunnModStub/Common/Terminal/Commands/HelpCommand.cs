using System.Collections.Generic;
using System.Linq;

#pragma warning disable IDE0130
namespace JotunnModStub {
#pragma warning restore IDE0130

    internal static partial class TerminalManager {
        private static void RegisterHelpCommand() {
            _ = new ModCommand($"{CommandPrefix}-help",
                "Format: [optional: area] Lists this mod's console commands, optionally just one area.",
                Help, "meta", HelpOptions);
        }

        private static List<string> HelpOptions(string[] input) {
            if (input.Length > 2) { return new List<string>(); }
            return Registry.Values
                .Where(command => command.HideFromHelp == false)
                .Select(command => command.Area)
                .Distinct()
                .OrderBy(area => area)
                .ToList();
        }

        private static void Help(ModCommandArgs args) {
            string filter = args.Args.GetString(0, null);
            if (filter != null && HelpOptions(new string[] { "", "" }).Contains(filter) == false) {
                args.Output.Warning($"Unknown area '{filter}'. Areas: {string.Join(", ", HelpOptions(new string[] { "", "" }))}");
                return;
            }

            // Help is for the person typing it; echoing the whole listing into the BepInEx log is noise.
            args.Output.Info($"{JotunnModStub.PluginName} commands:", log: false);

            foreach (IGrouping<string, ModCommand> group in Registry.Values
                .Where(command => command.HideFromHelp == false)
                .Where(command => filter == null || command.Area == filter)
                .GroupBy(command => command.Area)
                .OrderBy(group => group.Key)) {
                args.Output.Info($"  [{group.Key}]", log: false);
                foreach (ModCommand command in group.OrderBy(command => command.Command)) {
                    args.Output.Detail($"    {command.Command}{Tags(command)} - {command.Description}", log: false);
                    string aliases = AliasesOf(command);
                    if (aliases.Length > 0) {
                        args.Output.Detail($"      also accepts: {aliases}", log: false);
                    }
                }
            }
        }

        private static string Tags(ModCommand command) {
            List<string> tags = new List<string>();
            if (command.IsCheat) { tags.Add("cheat"); }
            if (command.RequiresAdmin) { tags.Add("admin"); }
            if (command.ServerAuthoritative) { tags.Add("server"); }
            return tags.Count == 0 ? string.Empty : $" ({string.Join(", ", tags)})";
        }

        // Aliases are registered as hidden commands so old or short names keep working; surface them here
        // so they stay discoverable rather than secret.
        private static string AliasesOf(ModCommand command) {
            return string.Join(", ", Registry.Values
                .Where(other => other.HideFromHelp && other.Canonical == command.Command)
                .Select(other => other.Command)
                .OrderBy(name => name));
        }
    }
}
