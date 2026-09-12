using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;
using CommunityPatchExtras.Common;

namespace CommunityPatchExtras
{
    // Hard dependency on the Community Patch: everything here is designed on top of its fixes
    // (the zone-radius tunable, for one, assumes its spawn-stream throttling exists), so refusing
    // to load without it beats a half-working install.
    //
    // VersionCheckOnly like the Community Patch itself: every feature here is guarded to be safe
    // one-sided (see each patch's header), but when both sides do have the mod, mismatched
    // versions should be refused up front rather than debugged later.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [BepInDependency("MidnightsFX.ValheimCommunityPatch", BepInDependency.DependencyFlags.HardDependency)]
    [NetworkCompatibility(CompatibilityLevel.VersionCheckOnly, VersionStrictness.Minor)]
    internal class CommunityPatchExtras : BaseUnityPlugin
    {
        public const string PluginGUID = "MidnightsFX.ValheimCommunityPatchExtras";
        public const string PluginName = "ValheimCommunityPatchExtras";
        public const string PluginVersion = "0.1.3";

        internal static ManualLogSource Log;
        internal ValConfig cfg;
        public static CustomLocalization Localization = LocalizationManager.Instance.GetLocalization();
        public static AssetBundle EmbeddedResourceBundle;

        private readonly Harmony harmony = new Harmony(PluginGUID);

        public void Awake() {
            Log = this.Logger;
            cfg = new ValConfig(Config);

            // Patches after the config: every patch reads its entries at call time, and the guards
            // in each are written to no-op until the game state they need exists.
            harmony.PatchAll(typeof(CommunityPatchExtras).Assembly);

            // All startup hooks should go after the config & Logger have been wired up
            
            //EmbeddedResourceBundle = AssetUtils.LoadAssetBundleFromResources("CommunityPatchExtras.Assets.embedded_bundle", typeof(CommunityPatchExtras).Assembly);
            LocalizationLoader.AddLocalizations();

            // Console commands. Registered here rather than from a Terminal/Console hook so the command
            // table also exists on a dedicated server, which never builds either. See Common/Terminal.
            TerminalManager.Init();

            // Yaml configs. After ValConfig, which owns cfgFolder and the poll/apply intervals this reads.
            // A config whose defaults need game state can Register() later -- registration after Init does
            // the per-file work immediately. See Common/Config.
            YamlConfigManager.Init();

            // Configs are not written until after they are all wired up, they exist in memory before this.
            // Flushing all of the configs at once is a significant speedup in mod load time
            ValConfig.SaveOnSet(true);
        }

        public void OnDestroy() {
            harmony?.UnpatchSelf();
        }
    }
}