using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#pragma warning disable IDE0130
namespace CommunityPatchExtras {
#pragma warning restore IDE0130

    // Severity of one line of command output. Deliberately separate from the text: colour is applied
    // where the line is displayed, so markup never reaches the BepInEx log or the network payload.
    internal enum OutputLevel : byte { Info = 0, Detail = 1, Warning = 2, Error = 3 }

    // Completions for the token currently being typed. `input` is the whole console line split on
    // spaces, so a provider can answer differently per argument position and based on what the earlier
    // arguments already say. Vanilla's ConsoleOptionsFetcher takes no arguments and so cannot.
    internal delegate List<string> OptionProvider(string[] input);

    internal delegate void CommandAction(ModCommandArgs args);

    // One mod console command.
    //
    // Subclasses the vanilla Terminal.ConsoleCommand instead of using Jotunn's ConsoleCommand wrapper,
    // for two reasons:
    //
    //   1. Jotunn registers commands from a Console.Awake postfix. Console is client UI, so that hook
    //      never runs on a dedicated server and the server has no command table at all. This type only
    //      writes into the static Terminal.commands dictionary, so it can be built headless -- which is
    //      what lets the server dispatch a command relayed from an admin client.
    //   2. Jotunn only supports a single flat tab-option list, applied to argument 1. This carries an
    //      OptionProvider that sees the whole line; see TerminalManager's tab patches.
    internal class ModCommand : Terminal.ConsoleCommand {
        // The primary name, even on an alias instance, so an alias and its canonical name behave alike.
        internal readonly string Canonical;

        // Free-form grouping label for the help listing ("world", "debug", ...). A string rather than an
        // enum so adding a group needs no change to the framework.
        internal readonly string Area;

        internal readonly CommandAction Action;
        internal readonly OptionProvider Options;
        internal readonly bool HideFromHelp;

        // The action touches state only the server owns, so a connected client asks the server to run it
        // instead of running it locally. See TerminalManager.Execute.
        internal readonly bool ServerAuthoritative;

        // Hint for the help listing. The real gate is TerminalNetwork.SenderIsAdmin on the server.
        internal readonly bool RequiresAdmin;

        internal ModCommand(
            string command,
            string description,
            CommandAction action,
            string area = "general",
            OptionProvider options = null,
            bool isCheat = false,
            bool serverAuthoritative = false,
            bool requiresAdmin = false,
            bool hideFromHelp = false,
            string canonical = null,
            params string[] aliases)
            : base(command, description,
                  (Terminal.ConsoleEvent)(args => TerminalManager.Execute(command, args)),
                  isCheat,
                  isNetwork: false,
                  onlyServer: false,
                  // isSecret also keeps the name out of the tab-completion list, which is what an alias
                  // wants: it still runs when typed in full, but never suggests itself.
                  isSecret: hideFromHelp,
                  allowInDevBuild: false,
                  optionsFetcher: null) {
            Canonical = canonical ?? command;
            Area = string.IsNullOrEmpty(area) ? "general" : area;
            Action = action;
            Options = options;
            HideFromHelp = hideFromHelp;
            ServerAuthoritative = serverAuthoritative;
            RequiresAdmin = requiresAdmin;

            // Vanilla asks for the option list before the tab patches get a chance to replace it, so the
            // fetcher has to exist and has to be safe when there is no Console yet. The patches are what
            // actually make per-argument completion work.
            m_tabOptionsFetcher = () => GetTabOptions(CurrentInput());
            // Options depend on what is already typed, so a cached list is always one keystroke stale.
            m_alwaysRefreshTabOptions = true;

            TerminalManager.Register(this);

            foreach (string alias in aliases) {
                _ = new ModCommand(alias, description, action, area, options, isCheat, serverAuthoritative,
                    requiresAdmin, hideFromHelp: true, canonical: Canonical);
            }
        }

        internal List<string> GetTabOptions(string[] input) {
            if (Options == null) { return new List<string>(); }
            try {
                return Options(input) ?? new List<string>();
            } catch (Exception e) {
                // A broken option provider must never stop the console from accepting input.
                Logger.LogDebug($"Tab options for {Command} failed: {e.Message}");
                return new List<string>();
            }
        }

        // Best-effort read of the line being typed, for the vanilla fetcher path only.
        private static string[] CurrentInput() {
            Terminal console = global::Console.instance;
            string text = console?.m_input == null ? string.Empty : console.m_input.text;
            return (text ?? string.Empty).Split(' ');
        }
    }

    // What a command handler receives: the arguments with the command name already stripped, the point
    // the command should act around, and where its output goes.
    internal class ModCommandArgs {
        internal readonly string[] Args;
        internal readonly TerminalOutput Output;

        // Position of the player who asked. Locally that is Player.m_localPlayer; on a relayed command it
        // is the requesting client's position, which is the only centre a dedicated server can use since
        // it has no local player of its own.
        internal readonly Vector3 Center;
        internal readonly bool HasCenter;

        internal ModCommandArgs(string[] args, Vector3 center, bool hasCenter, TerminalOutput output) {
            Args = args ?? new string[0];
            Center = center;
            HasCenter = hasCenter;
            Output = output;
        }

        internal int Length => Args.Length;

        internal bool Has(string flag) => Args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    }
}
