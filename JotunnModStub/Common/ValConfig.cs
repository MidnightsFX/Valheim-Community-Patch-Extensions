using BepInEx;
using BepInEx.Configuration;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#pragma warning disable IDE0130
namespace JotunnModStub {
#pragma warning restore IDE0130
    internal class ValConfig {
        public static ConfigFile cfg;
        
        // Add Client sided config entries under here
        public static ConfigEntry<bool> EnableDebugMode;
        public static ConfigEntry<bool> EnableTerminalColors;
        public static ConfigEntry<bool> ShowQuickConfigButton;

        // Add Server synced config entries under here
        public static ConfigEntry<int> InMemoryModificationsPerTick;
        public static ConfigEntry<float> ConfigApplyDelay;
        public static ConfigEntry<float> ConfigPollIntervalSeconds;

        // Derived from the plugin rather than written out, because scripts/RenameSolution.ps1 only
        // rewrites the .sln, .cs, .csproj and AssemblyInfo.cs -- it never touches Common/. A hardcoded
        // literal here means every mod copied from this template ships with its config folder still
        // called "JotunnModStub". PluginName is rewritten, so deriving it fixes that for free.
        public static readonly string cfgFolder = JotunnModStub.PluginName;

        public ValConfig(ConfigFile cf) {
            // ensure all the config values are created
            cfg = cf;
            cfg.SaveOnConfigSet = true;
            CreateConfigValues(cf);
            Logger.SetDebugLogging(EnableDebugMode.Value);

            // Watch #1. Registering before ConfigFileWatcher.Initialize() only touches a dictionary, so
            // ordering against YamlConfigManager.Init() does not matter.
            ConfigFileWatcher.Register(cfg.ConfigFilePath, OnMainConfigFileChanged);
        }

        // A client must not reload: Jotunn has already replaced its in-memory values with the server's,
        // and a reload would clobber them with whatever this machine happens to have on disk.
        //
        // Not routed through ConfigChangeDebouncer -- Reload raises SettingChanged per entry, and those
        // are already debounced individually by whichever handler is attached to them.
        private static void OnMainConfigFileChanged(string _) {
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) { return; }
            Logger.LogInfo("Configuration file has been changed, reloading settings.");
            cfg.Reload();
        }

        public static void SaveOnSet(bool enabled) {
            cfg.SaveOnConfigSet = enabled;
            cfg.Save();
        }

        private void CreateConfigValues(ConfigFile Config) {
            // Debugmode
            EnableDebugMode = Config.Bind("Client config", "EnableDebugMode", false,
                new ConfigDescription("Enables Debug logging.",
                null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));
            EnableDebugMode.SettingChanged += Logger.EnableDebugLogging;
            Logger.CheckEnableDebugLogging();

            // Read by TerminalOutput. Only affects what the console shows; the BepInEx log is always
            // written as plain text.
            EnableTerminalColors = Config.Bind("Client config", "EnableTerminalColors", true,
                new ConfigDescription("Colours this mod's console command output by severity.",
                null,
                new ConfigurationManagerAttributes { }));

            // Whether this mod appears in the shared bottom-right config launcher. Client config and not
            // IsAdminOnly: it is a per-machine UI preference, not a game rule. The shared button hides
            // itself once every mod that uses it has opted out.
            ShowQuickConfigButton = Config.Bind("Client config", "ShowQuickConfigButton", true,
                new ConfigDescription("Show this mod in the shared in-game config launcher (bottom right of the main and pause menus).",
                null,
                new ConfigurationManagerAttributes { }));
            ShowQuickConfigButton.SettingChanged += (sender, args) => ExampleConfigPanel.ApplyRegistration();

            // Instantiate server synced config entries here
            InMemoryModificationsPerTick = BindServerConfig("Config", "Updates Per Tick", 20, "Number of updates per tick that are applied when modifying items or pieces.", true, 1, 150);
            ConfigApplyDelay = BindServerConfig("Config", "Config Apply Delay", 1f, "Delay in seconds before a changed config entry is applied in-game. Coalesces a burst of rapid edits (typing, file reloads, server sync) into a single apply. Set to 0 to apply instantly.", true, 0f, 10f);

            // Read by Common/Config/ConfigFileWatcher every frame, so it is bound rather than hardcoded:
            // an admin editing yaml wants a short interval, a busy server wants a long one.
            ConfigPollIntervalSeconds = BindServerConfig("Config", "Config Poll Interval", 30f, "Seconds between checks for edits to this mod's yaml config files and its BepInEx config file. Lower reacts faster to a hand edit, higher does less disk work.", true, 1f, 300f);
        }

        // Every overload below marks the entry IsAdminOnly, which is what makes Jotunn's
        // SynchronizationManager push the server's value to every client. Use cfg.Bind directly for
        // anything that is genuinely per-machine (UI, logging, client-side toggles).
        //
        // The numeric overloads come in two shapes: pass a min and max for the common bounded case, or
        // pass an AcceptableValueBase (null for unbounded) when a range is wrong for the setting.

        /// <summary>
        /// Binds a server-synced float array within a shared range.
        /// </summary>
        /// <param name="category">Config file section.</param>
        /// <param name="key">Entry name within the section.</param>
        /// <param name="value">Default value.</param>
        /// <param name="description">Shown in the config file and the Configuration Manager.</param>
        /// <param name="advanced">Hides the entry behind the Advanced toggle.</param>
        /// <param name="valMin">Lowest accepted value.</param>
        /// <param name="valMax">Highest accepted value.</param>
        public static ConfigEntry<float[]> BindServerConfig(string category, string key, float[] value, string description, bool advanced = false, float valMin = 0, float valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Binds a server-synced bool.
        /// </summary>
        /// <param name="category">Config file section.</param>
        /// <param name="key">Entry name within the section.</param>
        /// <param name="value">Default value.</param>
        /// <param name="description">Shown in the config file and the Configuration Manager.</param>
        /// <param name="acceptableValues">Optional constraint. Normally null for a bool.</param>
        /// <param name="advanced">Hides the entry behind the Advanced toggle.</param>
        public static ConfigEntry<bool> BindServerConfig(string category, string key, bool value, string description, AcceptableValueBase acceptableValues = null, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Binds a server-synced int constrained to a range.
        /// </summary>
        /// <param name="category">Config file section.</param>
        /// <param name="key">Entry name within the section.</param>
        /// <param name="value">Default value.</param>
        /// <param name="description">Shown in the config file and the Configuration Manager.</param>
        /// <param name="advanced">Hides the entry behind the Advanced toggle.</param>
        /// <param name="valMin">Lowest accepted value.</param>
        /// <param name="valMax">Highest accepted value.</param>
        public static ConfigEntry<int> BindServerConfig(string category, string key, int value, string description, bool advanced = false, int valMin = 0, int valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<int>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Binds a server-synced int with an explicit constraint. Pass null for an unbounded value.
        /// </summary>
        /// <param name="category">Config file section.</param>
        /// <param name="key">Entry name within the section.</param>
        /// <param name="value">Default value.</param>
        /// <param name="description">Shown in the config file and the Configuration Manager.</param>
        /// <param name="acceptableValues">Constraint to apply, or null for no constraint.</param>
        /// <param name="advanced">Hides the entry behind the Advanced toggle.</param>
        public static ConfigEntry<int> BindServerConfig(string category, string key, int value, string description, AcceptableValueBase acceptableValues, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Binds a server-synced float constrained to a range.
        /// </summary>
        /// <param name="category">Config file section.</param>
        /// <param name="key">Entry name within the section.</param>
        /// <param name="value">Default value.</param>
        /// <param name="description">Shown in the config file and the Configuration Manager.</param>
        /// <param name="advanced">Hides the entry behind the Advanced toggle.</param>
        /// <param name="valMin">Lowest accepted value.</param>
        /// <param name="valMax">Highest accepted value.</param>
        public static ConfigEntry<float> BindServerConfig(string category, string key, float value, string description, bool advanced = false, float valMin = 0, float valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Binds a server-synced float with an explicit constraint. Pass null for an unbounded value.
        /// </summary>
        /// <param name="category">Config file section.</param>
        /// <param name="key">Entry name within the section.</param>
        /// <param name="value">Default value.</param>
        /// <param name="description">Shown in the config file and the Configuration Manager.</param>
        /// <param name="acceptableValues">Constraint to apply, or null for no constraint.</param>
        /// <param name="advanced">Hides the entry behind the Advanced toggle.</param>
        public static ConfigEntry<float> BindServerConfig(string category, string key, float value, string description, AcceptableValueBase acceptableValues, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Binds a server-synced string, optionally restricted to a fixed list of values.
        /// </summary>
        /// <param name="category">Config file section.</param>
        /// <param name="key">Entry name within the section.</param>
        /// <param name="value">Default value.</param>
        /// <param name="description">Shown in the config file and the Configuration Manager.</param>
        /// <param name="acceptableValues">Allowed values, or null to accept anything.</param>
        /// <param name="advanced">Hides the entry behind the Advanced toggle.</param>
        public static ConfigEntry<string> BindServerConfig(string category, string key, string value, string description, AcceptableValueList<string> acceptableValues = null, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(
                    description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }
    }
}
