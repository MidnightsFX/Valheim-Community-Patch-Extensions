# YAML configuration framework

Structured configuration in files an admin can actually read and edit, alongside BepInEx's flat
`ConfigEntry` values. Each file is one declaration: the framework owns the path, the default file, the
documented header, hot-reload, validation, failure policy, and server→client sync.

## Registering a file

Add your registrations to `RegisterConfigFiles()` in `Examples/ExampleYamlConfig.cs`:

```csharp
internal static partial class YamlConfigManager {
    internal static YamlConfigFile<Dictionary<string, ThingSettings>> Things;

    private static void RegisterConfigFiles() {
        Things = Register(new YamlConfigFile<Dictionary<string, ThingSettings>>("Things.yaml") {
            Header = ThingsHeader,
            Defaults = () => ThingData.BuiltIn,
            Apply = parsed => ThingData.Current = parsed,
            Validate = ThingData.Validate,
        });
    }
}
```

Read the loaded values through whatever `Apply` publishes them into, not through `Things.Value` — that
keeps the hot path a plain static field read.

| Property | Meaning |
| --- | --- |
| `FileName` | File name inside the mod's config folder. |
| `SubFolder` | Subfolder under it. Changing this for a shipped file is breaking — every install looks in the old place. |
| `Header` | Comment block written above the content. See below. |
| `Format` | `YamlFormat.Default` (PascalCase out), `.CamelCase`, or your own. Only affects writes. |
| `Defaults` | `Func<T>`, called on demand. Deferred so defaults may depend on game state. |
| `Apply` | Publishes the loaded values wherever the mod reads them. |
| `Validate` | `(newValue, previousValue) => ValidationReport`. |
| `OnFailure` | `KeepLastGood` (default), `RevertToDefaults`, `RestoreFileOnDisk`. |
| `Sync` | `ServerAuthoritative` (default) or `LocalOnly`. |
| `UnknownKeys` | `WarnAndContinue` (default), `Strict`, `Silent`. |
| `ClientWritesToDisk` | Whether a client mirrors the server's copy to its own disk. |
| `Watch` | Hot-reload on edit. Turn off for save data. |
| `NeedsPrefabs` | Set when `Validate` looks up prefabs — see below. |
| `SchemaVersion` | `0` disables versioning. Non-zero needs `GetSchemaVersion`, and a `Migrate` to move files forward. |

## The header

`Header` is written above the content on every write, and for most admins it is the **only**
documentation of the schema they will ever see. Spell out every field, every enum and its legal values,
and anything about how values combine that the names do not make obvious. A long header is a feature;
`StarLevelSystem`'s `LocationResetSettings.yaml` runs to 200 lines and that is the right call.

Because of this, **every write to a registered file must go through `YamlConfigManager`** —
`WriteCurrentToDisk`, `WriteRawToDisk` or `RestoreDefaults`. A bare `File.WriteAllText` silently deletes
the header and nobody notices until someone needs it.

## Defaults, and the `[DefaultValue]` trap

Generated files omit anything sitting on its default, which is what keeps them readable. But YamlDotNet
compares against `default(T)`, **not** against your C# initializer, unless the member carries
`[DefaultValue]`.

That makes `public bool Thing = true;` without `[DefaultValue(true)]` a silent data-loss bug: an admin
writes `Thing: false`, `false == default(bool)` so the serializer drops it, and the initializer sets it
back to `true` the next time the file is read. Their "off" becomes "on" the first time anything rewrites
the file.

**If a member's initializer is not `default(T)`, give it `[DefaultValue(<the same value>)]`.** Then
`false` is written and survives, and `true` is still omitted.

This is also why a client mirroring the server's copy writes the bytes it received rather than
re-serializing what it parsed — a round trip through the object model is lossy in exactly this way.

## When an admin gets it wrong

The design goal is that a mistake costs one setting, never the file.

- **Unknown key** (`Multiplierr: 2`) — the strict parse throws, the framework retries with
  `IgnoreUnmatchedProperties`, and logs the file, line and message. Everything else loads.
- **Unknown enum value** (`Mode: Multipy`) — `TolerantEnumConverter` claims every enum, substitutes the
  zero member, and warns with the full list of legal names.
- **Malformed document** — nothing can be salvaged, so `OnFailure` decides. The default `KeepLastGood`
  leaves the values that last loaded cleanly in memory and **does not touch the file**, so the admin can
  fix their edit in place.
- **Empty file, or only comments** — YamlDotNet returns `null` here without throwing, which is easy to
  miss in a hand-written loader. Treated as unusable and rewritten with defaults.

`Validate` returns warnings and errors as two separate lists. Warnings are logged and the file is used
anyway; errors route to `OnFailure`. Put "this prefab does not exist" in warnings and "there are no
entries at all" in errors.

Validators that look up prefabs must set `NeedsPrefabs` and the mod must call
`YamlConfigManager.RevalidateAll()` from `PrefabManager.OnPrefabsRegistered` — the prefab table does not
exist during `Awake`, so validating there would warn about every name in the file.

## Server sync

A `ServerAuthoritative` file gets a Jotunn `CustomRPC` and an initial-sync provider. On join the server
sends the file's text; on a server-side edit the watcher reloads and broadcasts it. Clients apply it in
memory, and additionally write it to disk if `ClientWritesToDisk` is set.

Client uploads on the main channel are **always rejected**. Jotunn's `IsAdminOnly` covers `ConfigEntry`
values only — a `CustomRPC` has no protection of its own and any peer can craft the package.

A file may opt into a separate, admin-gated upload channel with `AllowAdminEdit = true`. That registers a
second RPC (`<RpcName>_Edit`) on which a connected admin can send a replacement. The server checks
`SenderIsAdmin`, runs `DryRun` to validate **without applying**, and only then applies, writes and
re-broadcasts; a refusal goes back to the sender with the reason. Leave it off for save data.

Everything an editor changes goes through `YamlConfigManager.ApplyEdited(file, yaml, out message)` —
validate, apply, write with the header, broadcast. Note the broadcast there is explicit and has to be:
writing through the manager re-stamps the watcher, so the watcher-driven broadcast will *not* fire for
your own write.

Use `DryRun` rather than `LoadFrom` for anything that asks "would this be accepted?". `LoadFrom` routes
failure through `OnFailure`, so on a `RestoreFileOnDisk` file a rejected candidate would overwrite the
owner's good file with defaults.

`ConfigNetwork.ServerConfigsSynced` tells you whether this client has received the server's values yet.
It is reset on world unload, so a second join in the same session waits properly instead of drawing from
the previous server.

## BepInEx entries vs YAML

Both exist because they are good at different things:

- **BepInEx** — anything an admin should be able to change from the in-game Configuration Manager
  without alt-tabbing to a text editor. Flat scalars only. Jotunn syncs the `IsAdminOnly` ones for free.
- **YAML** — anything with structure. Lists, dictionaries, per-prefab tables, named groups.

Never make one value settable from both, unless one of them is an explicit, documented sentinel. Three
patterns cover essentially every real case:

| Pattern | Use when | Shape |
| --- | --- | --- |
| AND-gate | A feature has a master switch and per-group detail | `if (!ValConfig.EnableThing.Value) return false;` then the YAML flag |
| Sentinel override | An admin needs to retune one number live | `ConfigValidation.Prefer(bepInEx, yaml, sentinel: 0f)` |
| Infrastructure | The value configures the framework, not the feature | `ConfigPollIntervalSeconds` — no YAML equivalent exists |

Deliberately no declarative binding layer: these are three different things rather than three cases of
one, the composition point is a hot read, and the two halves sync by different paths at different times,
so a merged "effective value" would need its own invalidation story.

Two conventions instead. Name a gating entry `Enable<Feature>`, and **name the YAML file and key it
gates in its `ConfigDescription`** so the connection is discoverable from the Configuration Manager. To
re-check a validator when the BepInEx side moves, hook
`entry.SettingChanged += (s, e) => YamlConfigManager.RevalidateAll();`.

## Dropping this into another mod

Copy `Common/Config/`, then:

1. Add `ConfigEntry<float> ConfigPollIntervalSeconds` to your config class (`ConfigFileWatcher` reads it
   and falls back to 30s if it is null).
2. Make sure your `ValConfig` exposes `cfgFolder` and `ConfigApplyDelay`, and that
   `Common/ConfigChangeDebouncer.cs` is present.
3. Make sure your `Logger` exposes `LogDebug` / `LogInfo` / `LogWarning` / `LogError`.
4. Add `<PackageReference Include="YamlDotNet" Version="16.3.0" />` and
   `ValheimModding-YamlDotNet-16.3.1` to your Thunderstore manifest. Do **not** bundle the DLL — a second
   copy beside another mod's is an assembly identity conflict.
5. Replace `Examples/ExampleYamlConfig.cs` with your own `RegisterConfigFiles()`.
6. Register any `IYamlTypeConverter` of your own with `YamlFormat.AddTypeConverter` before calling
   `Init()`. `TolerantEnumConverter` is registered for you.
7. Call `YamlConfigManager.Init()` from `Awake`, after your config class is constructed.

It patches `ZNet.Shutdown` with a private Harmony instance rather than `[HarmonyPatch]` attributes, so a
plugin that also calls `Harmony.CreateAndPatchAll(assembly)` will not apply it a second time.

`ConfigNetwork.SenderIsAdmin` is duplicated from `Common/Terminal/TerminalNetwork.cs` on purpose, so the
two folders stay independently droppable. Do not extract it into a shared helper.
