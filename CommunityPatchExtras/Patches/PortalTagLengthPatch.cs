using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;

namespace CommunityPatchExtras.Patches {
    // Extends portal tag character limit from native's 10 to 40 characters.
    // Transpiles TeleportWorld.Interact to supply ExtendedTagLimit to TextInput.RequestText.
    // Server-synced; unmodded native servers revert to native 10-character limit.
    [HarmonyPatch]
    internal static class PortalTagLengthPatch {
        public const int ExtendedTagLimit = 40;
        public const int NativeTagLimit = 10;

        internal static ConfigEntry<bool> Enabled;

        private static ZNet _confirmedFor;

        private static readonly MethodInfo RequestTextMethod =
            AccessTools.DeclaredMethod(typeof(TextInput), nameof(TextInput.RequestText));

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                "Portals",
                "Extend Portal Tag Limit",
                true,
                "Whether the tag you give a portal can be up to 40 characters long instead of vanilla's 10. " +
                "Server-synced; a client on a server without this mod keeps vanilla behaviour.");

            SynchronizationManager.OnConfigurationSynchronized += OnConfigurationSynchronized;
        }

        private static void OnConfigurationSynchronized(object sender, ConfigurationSynchronizationEventArgs args) {
            if (args.UpdatedPluginGUIDs == null || !args.UpdatedPluginGUIDs.Contains(CommunityPatchExtras.PluginGUID)) { return; }

            if (ZNet.instance && !ZNet.instance.IsServer()) { _confirmedFor = ZNet.instance; }
        }

        private static bool Active() {
            if (Enabled == null || !Enabled.Value) { return false; }

            ZNet znet = ZNet.instance;
            if (!znet) { return false; }

            return znet.IsServer() || ReferenceEquals(_confirmedFor, znet);
        }

        internal static int GetTagLimit() => Active() ? ExtendedTagLimit : NativeTagLimit;

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Interact))]
        private static IEnumerable<CodeInstruction> InteractTranspiler(IEnumerable<CodeInstruction> instructions) {
            CodeMatcher matcher = new CodeMatcher(instructions);
            CodeMatch[] matches = new CodeMatch[] {
                new CodeMatch(instruction => instruction.LoadsConstant(10)),
                new CodeMatch(instruction => instruction.Calls(RequestTextMethod))
            };

            matcher.MatchForward(useEnd: false, matches);

            if (matcher.IsValid) {
                matcher.SetInstruction(Transpilers.EmitDelegate(GetTagLimit));
                return matcher.InstructionEnumeration();
            }

            Logger.LogWarning("TeleportWorld.Interact: failed to match portal tag limit instructions.");
            return instructions;
        }
    }
}
