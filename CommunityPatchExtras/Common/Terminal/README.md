# Console command framework

A replacement for Jotunn's `ConsoleCommand` registration, with per-argument tab-completion, coloured
output, aliases, a generated help listing, and a relay that lets an admin client run server-side
commands on a dedicated server.

## Adding a command

Add a `partial TerminalManager` file under `Commands/` and call your registration method from
`RegisterCommands()` in `ExampleCommands.cs`:

```csharp
internal static partial class TerminalManager {
    private static void RegisterMyCommands() {
        _ = new ModCommand($"{CommandPrefix}-thing",
            "Format: [name] Does the thing.",
            DoThing, "myarea", ThingOptions);
    }

    private static List<string> ThingOptions(string[] input) {
        // `input` is the whole console line split on spaces, so options can depend on argument
        // position AND on what earlier arguments say.
        return input.Length <= 2 ? new List<string>() { "one", "two" } : new List<string>();
    }

    private static void DoThing(ModCommandArgs args) {
        args.Output.Info($"Did the thing to {args.Args.GetString(0, "nothing")}.");
    }
}
```

`ModCommand` options:

| Argument | Meaning |
| --- | --- |
| `area` | Grouping label in the help listing. Any string. |
| `options` | `OptionProvider` for tab-completion. |
| `isCheat` | Vanilla's cheat flag — see the caveat below. |
| `serverAuthoritative` | Relay to the server instead of running locally. |
| `requiresAdmin` | Gate the relay on the server's admin list. |
| `aliases` | Extra names that run the same action, hidden from help and autocomplete. |

Change `TerminalManager.CommandPrefix` to something short and unique to your mod. Grouping every
command under one prefix is what makes them findable by tab-completion.

## Output

Use `args.Output`, never `Logger` directly, so a relayed command's output reaches the player who
asked as well as the server log:

```csharp
args.Output.Info("green");
args.Output.Detail("soft blue");
args.Output.Warning("amber");
args.Output.Error("red");
```

Each call writes to the BepInEx log **and** to the terminal (or back over the network). Pass
`log: false` when you have already logged the line yourself. Colour is applied only where a line is
handed to a `Terminal`, so no `<color=…>` markup ever reaches a log file or the network payload — the
severity travels as a byte and each client colours it with its own `EnableTerminalColors` setting.

## Server-authoritative commands

`serverAuthoritative: true` means a connected client sends the command to the server instead of
running it locally. The server checks the sender against its admin list, runs the command, and streams
the output back into the requesting client's console, batched.

This exists because **a dedicated server has no console at all**: `Console` and `Chat` are client UI,
so neither `Console.Awake` nor `Terminal.InitTerminal` ever runs headless. A command that acts on
server-owned state is otherwise untypeable anywhere. Vanilla does have a relay (`remoteCommand` →
`ZNet.RPC_RemoteCommand`), but it ends in `Console.instance.TryRunCommand` and null-references on a
dedicated server, so it cannot be used for this.

The request carries the requesting player's position, exposed as `args.Center` / `args.HasCenter` —
the only centre point a headless server can act around. `jms-worldinfo` in `ExampleCommands.cs` is a
worked example.

## Cheat commands

Vanilla's `Terminal.IsCheatsEnabled()` is `m_cheat && ZNet.instance && ZNet.instance.IsServer()`, and
`ConsoleCommand.IsValid` rejects on `IsCheat && !IsCheatsEnabled()`. **An `isCheat` command's action is
never reached on a connected client**, admin or not. Either leave `isCheat` off and rely on
`requiresAdmin`, or expect your users to run
[Server devcommands](https://github.com/JereKuusela/valheim-dev), which admin-checks and bypasses that
gate.

## Why not Jotunn's ConsoleCommand

- `CommandManager` registers from a `Console.Awake` postfix, so on a dedicated server the command
  table is never built and the relay would have nothing to dispatch to. `ModCommand` subclasses the
  vanilla `Terminal.ConsoleCommand` directly; its constructor only writes into the static
  `Terminal.commands` dictionary, which is safe headless.
- Jotunn supports one flat option list, applied to argument 1 only. `Terminal.Update` hardcodes
  `strArray[1]` for both `tabCycle` and `updateSearch`, so `TerminalManager` prefixes both and corrects
  `word` as well as `options` — `tabCycle` rewrites the input from the caret back by `word.Length`, so
  leaving `word` pointing at argument 1 mangles the line from argument 2 onward.

## Dropping this into another mod

Copy `Common/Terminal/`, then:

1. Add a `ConfigEntry<bool> EnableTerminalColors` to your config class (`TerminalOutput` reads it and
   falls back to plain text if it is null).
2. Make sure your `Logger` exposes `LogDebug` / `LogInfo` / `LogWarning` / `LogError`.
3. Call `TerminalManager.Init()` from `Awake`.

It patches Harmony itself with a private instance rather than `[HarmonyPatch]` attributes, so a plugin
that also calls `Harmony.CreateAndPatchAll(assembly)` will not apply the tab prefixes a second time.
