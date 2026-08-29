using HarmonyLib;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#pragma warning disable IDE0130
namespace CommunityPatchExtras {
#pragma warning restore IDE0130

    // Registration and dispatch for this mod's console commands.
    //
    // Commands are built once from the plugin's Awake, not from a Terminal or Console hook, because a
    // dedicated server has neither: Console.Awake and Terminal.InitTerminal only ever run on a client.
    // The server still needs the command table so it can dispatch a request relayed from an admin
    // client, and constructing a Terminal.ConsoleCommand only touches a static dictionary, so building
    // them headless is harmless.
    internal static partial class TerminalManager {
        // Prefix shared by every command this mod adds. Change it to something short and unique to your
        // mod; grouping commands under one prefix is what makes them findable by tab-completion.
        internal const string CommandPrefix = "jms";

        internal static readonly Dictionary<string, ModCommand> Registry = new Dictionary<string, ModCommand>();

        // Whichever terminal was last used to send a server-authoritative command, so the relayed reply
        // lands where the player typed rather than always in the console.
        private static Terminal responseTerminal;

        private static bool initialized;
        private static Harmony harmony;

        // Call once from the plugin's Awake.
        internal static void Init() {
            if (initialized) { return; }
            initialized = true;

            // Patched here rather than through [HarmonyPatch] attributes so this folder stays a drop-in:
            // a plugin that later calls Harmony.CreateAndPatchAll(assembly) will not pick these up a
            // second time and double-apply the prefixes.
            harmony = new Harmony(CommunityPatchExtras.PluginGUID + ".terminal");
            PatchTabCompletion(nameof(Terminal.tabCycle));
            PatchTabCompletion(nameof(Terminal.updateSearch));

            TerminalNetwork.Init();
            RegisterCommands();

            Logger.LogDebug($"Registered {Registry.Count} console commands.");
        }

        // Tolerate a future Valheim rename: losing the fancy per-argument completion is a much better
        // outcome than an exception in Awake that takes the rest of the mod down with it.
        private static void PatchTabCompletion(string methodName) {
            try {
                System.Reflection.MethodInfo target = AccessTools.Method(typeof(Terminal), methodName);
                if (target == null) {
                    Logger.LogWarning($"Terminal.{methodName} not found; per-argument tab completion is disabled.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(TerminalManager), nameof(ContextOptionsPrefix))));
            } catch (Exception e) {
                Logger.LogWarning($"Could not patch Terminal.{methodName} for tab completion: {e.Message}");
            }
        }

        internal static void Register(ModCommand command) {
            Registry[Key(command.Command)] = command;
        }

        private static string Key(string name) => (name ?? string.Empty).ToLowerInvariant();

        private static ModCommand Lookup(string name) {
            return Registry.TryGetValue(Key(name), out ModCommand command) ? command : null;
        }

        // Every command's vanilla action funnels through here.
        internal static void Execute(string name, Terminal.ConsoleEventArgs consoleArgs) {
            ModCommand command = Lookup(name);
            if (command == null) { return; }

            string[] args = consoleArgs.Args.Skip(1).ToArray();
            TerminalOutput output = TerminalOutput.Local(consoleArgs.Context);

            if (command.ServerAuthoritative == false) {
                Invoke(command, args, output);
                return;
            }

            if (ZNet.instance == null) {
                output.Error($"You must be in a world to use {command.Canonical}.");
                return;
            }
            // An integrated host is the server, so it just runs the thing.
            if (ZNet.instance.IsServer()) {
                Invoke(command, args, output);
                return;
            }
            // Client-side check is for a clear message only; the server re-checks the sender either way.
            if (command.RequiresAdmin && SynchronizationManager.Instance.PlayerIsAdmin == false) {
                output.Error($"Only server admins can run {command.Canonical} from a client.");
                return;
            }
            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null) {
                output.Error($"No server connection, so {command.Canonical} cannot be sent.");
                return;
            }

            responseTerminal = consoleArgs.Context;
            output.Info($"Asked the server to run {command.Canonical}; its output follows.");
            TerminalNetwork.SendRequest(server.m_uid, command.Canonical, args);
        }

        // Server side of the relay. The caller has already established that the sender is allowed.
        internal static void ExecuteFromNetwork(string name, string[] args, Vector3 center, bool hasCenter, TerminalOutput output) {
            ModCommand command = Lookup(name);
            // Never dispatch a name the client picked that is not one of ours, and never let the relay
            // reach a command that was not built to run server-side.
            if (command == null || command.ServerAuthoritative == false) {
                output.Error($"'{name}' is not a server-runnable command.");
                output.Flush();
                return;
            }
            Invoke(command, args, center, hasCenter, output);
        }

        private static void Invoke(ModCommand command, string[] args, TerminalOutput output) {
            Vector3 center = Vector3.zero;
            bool hasCenter = false;
            if (Player.m_localPlayer != null) {
                center = Player.m_localPlayer.transform.position;
                hasCenter = true;
            }
            Invoke(command, args, center, hasCenter, output);
        }

        private static void Invoke(ModCommand command, string[] args, Vector3 center, bool hasCenter, TerminalOutput output) {
            try {
                command.Action(new ModCommandArgs(args, center, hasCenter, output));
            } catch (Exception e) {
                // A command that throws must not take the console or the RPC handler with it.
                output.Error($"{command.Canonical} failed: {e.Message}");
                Logger.LogError($"{command.Canonical} threw: {e}");
            } finally {
                // Commands that finish synchronously are fully flushed here. One that started a coroutine
                // keeps using the same sink and flushes again as it goes.
                output.Flush();
            }
        }

        // A relayed line arriving back on the requesting client.
        internal static void PrintResponse(OutputLevel level, string line) {
            TerminalOutput.LogLine(level, line);
            // ?? is not enough for a UnityEngine.Object: a destroyed terminal is a non-null reference
            // that only compares equal to null through Unity's own operator.
            Terminal target = responseTerminal != null ? responseTerminal : null;
            if (target == null && global::Console.instance != null) { target = global::Console.instance; }
            TerminalOutput.PrintTo(target, level, line);
        }

        // -----------------------------------------------------------------------------------------
        // Tab completion
        //
        // Vanilla only ever completes the first argument: Terminal.Update hands tabCycle/updateSearch
        // strArray[1] and one flat list from ConsoleCommand.GetTabOptions(). This prefix swaps in a list
        // that depends on everything typed so far, and points `word` at the token actually being edited
        // -- tabCycle rewrites the input from the caret back by word.Length, so correcting `word` is what
        // keeps completion from mangling the line at the second argument and beyond.
        // -----------------------------------------------------------------------------------------

        private static void ContextOptionsPrefix(Terminal __instance, ref string word, ref List<string> options, bool usePrefix) {
            // usePrefix means the command name itself is being completed; vanilla already handles that.
            if (usePrefix || __instance == null || __instance.m_input == null) { return; }

            string[] tokens = (__instance.m_input.text ?? string.Empty).Split(' ');
            if (tokens.Length < 2) { return; }

            // Chat prefixes commands with m_tabPrefix ('/'), the console does not.
            string name = __instance.m_tabPrefix == char.MinValue
                ? tokens[0]
                : (tokens[0].Length == 0 ? string.Empty : tokens[0].Substring(1));

            ModCommand command = Lookup(name);
            if (command == null) { return; }

            List<string> resolved = command.GetTabOptions(tokens);
            if (resolved == null || resolved.Count == 0) { return; }

            options = resolved;
            word = tokens[tokens.Length - 1];
        }
    }
}
