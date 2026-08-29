using System.Collections.Generic;
using System.ComponentModel;

#pragma warning disable IDE0130
namespace JotunnModStub {
#pragma warning restore IDE0130

    // Worked examples, one per axis of the framework. Delete these once you have your own configs;
    // keep RegisterConfigFiles().
    internal static partial class YamlConfigManager {

        // Called from Init(). Register your own config files here.
        private static void RegisterConfigFiles() {
            RegisterExampleConfigs();
        }

        internal static YamlConfigFile<ExampleSettings> ExampleFile;
        internal static YamlConfigFile<Dictionary<string, int>> ExampleSavedDataFile;

        private static void RegisterExampleConfigs() {
            // 1. The common case: server-authoritative, hot-reloaded, validated, versioned.
            ExampleFile = Register(new YamlConfigFile<ExampleSettings>("ExampleSettings.yaml") {
                Header = ExampleHeader,
                Defaults = () => ExampleData.BuildDefaults(),
                Apply = parsed => ExampleData.Current = parsed,
                Validate = ExampleData.Validate,
                NeedsPrefabs = true,
                SchemaVersion = 1,
                GetSchemaVersion = settings => settings.Version,
                SetSchemaVersion = (settings, version) => settings.Version = version,
            });

            // 2. Save data, not configuration. Never synced, never watched: the game writes it far more
            //    often than a human does, so a watcher would fire on our own writes and a reload would
            //    fight whatever is in memory. Lives in a subfolder to keep it out of the admin's way.
            ExampleSavedDataFile = Register(new YamlConfigFile<Dictionary<string, int>>("ExampleSavedData.yaml") {
                SubFolder = "SavedData",
                Header = "# Save data written by this mod. Edit it with the game closed.",
                Defaults = () => new Dictionary<string, int>(),
                Apply = parsed => ExampleData.SavedCounters = parsed,
                Sync = ConfigSyncMode.LocalOnly,
                Watch = false,
            });

            // 3. A file whose on-disk format predates this framework and should not shift under existing
            //    installs, and which clients mirror to disk so they can read what the server gave them.
            //    Note that the camelCase choice only affects what gets WRITTEN -- the deserializer is
            //    case-insensitive, so a PascalCase file would still load fine either way.
            Register(new YamlConfigFile<Dictionary<string, ExampleEntry>>("ExampleLegacyFormat.yaml") {
                Header = "# A config kept in its original camelCase form for backwards compatibility.",
                Format = YamlFormat.CamelCase,
                Defaults = () => new Dictionary<string, ExampleEntry>(),
                Apply = parsed => ExampleData.LegacyEntries = parsed,
                ClientWritesToDisk = true,
            });
        }

        // The header is the only schema documentation most admins will ever read. Spell out every field,
        // every enum value, and anything about how the values combine that is not obvious from the names.
        private const string ExampleHeader = @"#################################################
# JotunnModStub - Example settings
#
# Entries is a map of <key>: <entry>. The key is the identity used elsewhere in this mod, so
# renaming one is a breaking change; DisplayName is only a label and is safe to change.
#
#   DisplayName  string   Shown to the player.
#   Multiplier   float    Scales the thing. 1.0 is unchanged. Range 0.1 - 10.
#   Mode         enum     Off | Add | Multiply
#   Prefabs      list     Prefab names this entry applies to. Unknown names are warned about
#                         and skipped, they do not break the file.
#
# A typo in a key or an enum value costs you that one setting and logs a warning naming the
# line; the rest of the file still loads.
#################################################";
    }

    public enum ExampleMode { Off, Add, Multiply }

    public class ExampleEntry {
        public string DisplayName {
            get; set;
        }

        // [DefaultValue] is what makes OmitDefaults respect the initializer below. Without it the
        // serializer compares against default(float) -- 0 -- and writes Multiplier: 1 into every entry.
        [DefaultValue(1f)]
        public float Multiplier {
            get; set;
        } = 1f;

        public ExampleMode Mode {
            get; set;
        }

        public List<string> Prefabs {
            get; set;
        } = new List<string>();
    }

    public class ExampleSettings {
        // Bump SchemaVersion in the registration above when a change to this shape cannot be read by the
        // previous version, and supply a Migrate to carry existing files across. Leave SchemaVersion at 0
        // and this is ignored entirely, which is the right answer for most files.
        public int Version {
            get; set;
        } = 1;

        public Dictionary<string, ExampleEntry> Entries {
            get; set;
        } = new Dictionary<string, ExampleEntry>();
    }

    internal static class ExampleData {
        internal static ExampleSettings Current = new ExampleSettings();
        internal static Dictionary<string, int> SavedCounters = new Dictionary<string, int>();
        internal static Dictionary<string, ExampleEntry> LegacyEntries = new Dictionary<string, ExampleEntry>();

        internal static ExampleSettings BuildDefaults() {
            return new ExampleSettings() {
                Version = 1,
                Entries = new Dictionary<string, ExampleEntry>() {
                    { "Example", new ExampleEntry() { DisplayName = "An example", Multiplier = 1.5f, Mode = ExampleMode.Multiply } },
                },
            };
        }

        // previous is null on the first load, and is the currently-live value on every load after that.
        // Diffing the two is how you warn about entries that were REMOVED -- the change an admin is least
        // likely to spot the consequences of on their own.
        internal static ValidationReport Validate(ExampleSettings next, ExampleSettings previous) {
            ValidationReport report = new ValidationReport();

            if (next.Entries == null || next.Entries.Count == 0) {
                return report.Error("it defines no entries");
            }

            foreach (KeyValuePair<string, ExampleEntry> entry in next.Entries) {
                if (entry.Value == null) {
                    report.Error($"entry '{entry.Key}' has no settings under it");
                    continue;
                }
                if (entry.Value.Multiplier < 0.1f || entry.Value.Multiplier > 10f) {
                    report.Warn($"entry '{entry.Key}' has Multiplier {entry.Value.Multiplier}, outside the " +
                        "supported range of 0.1 - 10. It will be used as written.");
                }
                if (entry.Value.Prefabs == null) { continue; }
                foreach (string prefab in entry.Value.Prefabs) {
                    // NeedsPrefabs on the registration is what makes this meaningful: during Awake the
                    // prefab table does not exist yet, so the mod re-runs validation from
                    // PrefabManager.OnPrefabsRegistered rather than warning about everything at startup.
                    if (Jotunn.Managers.PrefabManager.Instance != null
                        && Jotunn.Managers.PrefabManager.Instance.GetPrefab(prefab) == null) {
                        report.Warn($"entry '{entry.Key}' names prefab '{prefab}', which does not exist. " +
                            "That prefab will be skipped.");
                    }
                }
            }

            if (previous != null && previous.Entries != null) {
                foreach (string key in previous.Entries.Keys) {
                    if (next.Entries.ContainsKey(key) == false) {
                        report.Warn($"entry '{key}' was removed. Anything still referring to it will fall back.");
                    }
                }
            }

            return report;
        }
    }
}
