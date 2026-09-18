using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace CommunityPatchExtras.Patches {
    // Controls how the game displays world save countdowns and completion notices.
    //
    // In native Valheim, Game.UpdateSaving broadcasts a 30-second autosave warning
    // ("$msg_worldsavewarning 30s") using MessageHud.MessageType.Center. This splashes a giant,
    // screen-filling yellow banner directly across the player's crosshair and combat view.
    // In addition, dedicated servers and admin scripts frequently broadcast similar save warnings
    // to all players using center-screen toasts.
    //
    // During combat, boss fights, sailing, or delicate building, having the middle of the screen
    // obscured by an autosave alert can easily cause deaths or misclicks.
    //
    // This patch intercepts MessageHud.ShowMessage and provides three modes:
    //  - Native (Default): Leaves notifications in the center of the screen unaltered.
    //  - RelocateToTopLeft: Converts center save notices into subtle top-left corner
    //    messages, keeping the player informed of pending saves without blocking their view.
    //  - Mute: Suppresses the center save alerts entirely.
    //
    // Client-side presentation setting: per-machine, not synced from the server, and inert on
    // headless servers where MessageHud is not rendered.
    [HarmonyPatch(typeof(MessageHud))]
    internal static class WorldSaveNoticePatch {
        public enum SaveNoticeMode {
            Native,
            RelocateToTopLeft,
            Mute
        }

        internal static ConfigEntry<SaveNoticeMode> NoticeMode;

        internal static void BindConfig() {
            NoticeMode = ValConfig.cfg.Bind(
                "HUD",
                "World Save Notice Mode",
                SaveNoticeMode.Native,
                new ConfigDescription(
                    "Controls how the autosave countdown warning and completion notifications are displayed. " +
                    "Native leaves them in the center of the screen (default). " +
                    "RelocateToTopLeft moves them to the subtle top-left feed. " +
                    "Mute hides them completely.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = false })
            );
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MessageHud.ShowMessage))]
        private static bool ShowMessagePrefix(ref MessageHud.MessageType type, ref string text) {
            if (NoticeMode == null || NoticeMode.Value == SaveNoticeMode.Native) {
                return true;
            }

            if (type != MessageHud.MessageType.Center || string.IsNullOrEmpty(text)) {
                return true;
            }

            if (IsWorldSaveMessage(text)) {
                if (NoticeMode.Value == SaveNoticeMode.Mute) {
                    return false;
                }

                type = MessageHud.MessageType.TopLeft;
            }

            return true;
        }

        private static bool IsWorldSaveMessage(string text) {
            // Raw localization keys used by native Game.UpdateSaving and ZNet.PrintWorldSaveMessage
            if (text.Contains("$msg_worldsave")) {
                return true;
            }

            // Localized text checks
            if (Localization.instance != null) {
                string warning = Localization.instance.Localize("$msg_worldsavewarning");
                if (!string.IsNullOrEmpty(warning) && text.Contains(warning)) {
                    return true;
                }

                string saved = Localization.instance.Localize("$msg_worldsaved");
                if (!string.IsNullOrEmpty(saved) && text.Contains(saved)) {
                    return true;
                }
            }

            // Dedicated server scripts or mods broadcasting plain English countdowns (e.g. "World save in 30s")
            if (text.IndexOf("world save", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("saving world", StringComparison.OrdinalIgnoreCase) >= 0) {
                return true;
            }

            return false;
        }
    }
}
