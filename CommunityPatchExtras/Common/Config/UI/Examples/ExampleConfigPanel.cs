using Jotunn.Managers;
using System;
using System.Collections.Generic;
using UnityEngine;

#pragma warning disable IDE0130
namespace CommunityPatchExtras {
#pragma warning restore IDE0130

    // A worked example of a QuickConfig panel, exercising one of every widget in the kit. Delete this file
    // and point ApplyRegistration's replacement at your own panel.
    //
    // It edits ExampleData.Current, which is the in-memory value behind ExampleSettings.yaml, and saves
    // through YamlConfigManager.ApplyEdited -- the same path a real editor uses. That is deliberate: the
    // example is only worth having if it proves the whole chain, launcher through to a validated write.
    internal static class ExampleConfigPanel {
        private const string EntryName = "CommunityPatchExtras";
        private const float PanelW = 620f;
        private const float PanelH = 520f;
        private const float LabelW = 190f;

        private static GameObject panel;
        private static ExampleSettings staged;
        private static UnityEngine.UI.Text messages;

        internal static void Init() {
            ConfigUILauncher.Init();
            ApplyRegistration();
        }

        // Called from Init and whenever ShowQuickConfigButton changes, so the entry appears and disappears
        // without a restart.
        internal static void ApplyRegistration() {
            if (ValConfig.ShowQuickConfigButton == null || ValConfig.ShowQuickConfigButton.Value) {
                ConfigUILauncher.Register(EntryName, OpenPanel);
            } else {
                ConfigUILauncher.Unregister(EntryName);
            }
        }

        internal static void OpenPanel() {
            ClosePanel();

            // Work on a copy taken through the serializer, never on the live object: the live one is what
            // the rest of the mod is reading right now, and a round trip also resets any lazy caches a
            // config type may have accumulated.
            YamlConfigFile<ExampleSettings> file = YamlConfigManager.ExampleFile;
            if (file == null) { return; }
            staged = file.EffectiveFormat.Deserializer.Deserialize<ExampleSettings>(
                file.EffectiveFormat.Serializer.Serialize(file.Value));
            if (staged == null) { staged = ExampleData.BuildDefaults(); }

            panel = ConfigUI.CreatePanel("Example settings", PanelW, PanelH, out Transform body);

            List<GameObject> rows = new List<GameObject>();
            rows.Add(ConfigUI.AddHeaderRow(body, PanelW - 40f, "Entries"));

            foreach (KeyValuePair<string, ExampleEntry> pair in staged.Entries) {
                ExampleEntry entry = pair.Value;
                rows.Add(ConfigUI.AddTextRow(body, PanelW - 40f, ConfigUI.SubRowHeight, pair.Key, 15,
                    GUIManager.Instance.ValheimOrange));
                rows.Add(ConfigUI.AddTextFieldRow(body, PanelW - 40f, LabelW, 240f, "Display name",
                    entry.DisplayName, s => entry.DisplayName = s, null, 64));
                rows.Add(ConfigUI.AddSliderRow(body, PanelW - 40f, LabelW, 200f, 60f, "Multiplier",
                    0.1f, 10f, entry.Multiplier, false, v => entry.Multiplier = v));
                rows.Add(ConfigUI.AddEnumCycleRow(body, PanelW - 40f, LabelW, 150f, "Mode",
                    Enum.GetNames(typeof(ExampleMode)), (int)entry.Mode, i => entry.Mode = (ExampleMode)i));
                rows.Add(ConfigUI.AddPickerRow(body, PanelW - 40f, LabelW, 260f, "First prefab",
                    entry.Prefabs.Count > 0 ? entry.Prefabs[0] : "", PrefabNames, s => {
                        if (entry.Prefabs.Count > 0) { entry.Prefabs[0] = s; } else { entry.Prefabs.Add(s); }
                    }, IsKnownPrefab));
                rows.Add(ConfigUI.AddDividerRow(body, PanelW - 40f));
            }

            ConfigUI.LayoutColumn(rows, 20f, 60f);

            messages = ConfigUI.AddText(body, 20f, PanelH - 108f, PanelW - 40f, 46f, "", 13,
                TextAnchor.UpperLeft);

            ConfigUI.AddButton(body, 20f, PanelH - 56f, 130f, "Validate", Validate);
            ConfigUI.AddButton(body, PanelW - 320f, PanelH - 56f, 130f, "Cancel", ClosePanel);
            ConfigUI.AddButton(body, PanelW - 170f, PanelH - 56f, 150f, "Apply & Save", ApplyAndSave);
        }

        private static void Validate() {
            YamlConfigFile<ExampleSettings> file = YamlConfigManager.ExampleFile;
            if (file == null || staged == null) { return; }
            ValidationReport report = file.DryRun(YamlConfigManager.SerializeForEdit(file, staged), out string parseError);
            if (parseError != null) {
                ConfigUI.SetMessages(messages, new List<string>() { parseError }, null);
                return;
            }
            if (report.Errors.Count == 0 && report.Warnings.Count == 0) {
                messages.text = "Looks good.";
                return;
            }
            ConfigUI.SetMessages(messages, report.Errors, report.Warnings);
        }

        private static void ApplyAndSave() {
            YamlConfigFile<ExampleSettings> file = YamlConfigManager.ExampleFile;
            if (file == null || staged == null) { return; }

            try {
                string yaml = YamlConfigManager.SerializeForEdit(file, staged);
                if (YamlConfigManager.ApplyEdited(file, yaml, out string message) == false) {
                    // Panel stays OPEN on refusal. Closing over a rejected save is how an admin ends up
                    // believing a change landed when it did not.
                    ConfigUI.SetMessages(messages, new List<string>() { message }, null);
                    return;
                }
                ClosePanel();
            } catch (Exception e) {
                Logger.LogError($"Example config panel failed to apply: {e}");
                ConfigUI.SetMessages(messages, new List<string>() { e.Message }, null);
            }
        }

        private static void ClosePanel() {
            if (panel == null) { return; }
            UnityEngine.Object.Destroy(panel);
            panel = null;
            messages = null;
        }

        // Empty in the main menu, which is fine -- the picker just shows nothing to choose from.
        private static IList<string> PrefabNames() {
            List<string> names = new List<string>();
            if (ZNetScene.instance == null) { return names; }
            foreach (GameObject prefab in ZNetScene.instance.m_prefabs) {
                if (prefab != null) { names.Add(prefab.name); }
            }
            return names;
        }

        private static bool IsKnownPrefab(string name) {
            // Treat everything as known until the prefab table exists, so the main menu does not paint
            // every field with a warning marker.
            if (PrefabManager.Instance == null) { return true; }
            return PrefabManager.Instance.GetPrefab(name) != null;
        }
    }
}
