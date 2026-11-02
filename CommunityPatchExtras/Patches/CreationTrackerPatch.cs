using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CommunityPatchExtras.Patches {
    // Counts the networked objects created on this machine, per prefab, so a prefab being made far
    // more often than it should (a spawner gone wrong, a mod dropping items, an object torn down and
    // rebuilt every frame) can be named instead of guessed at. Off by default.
    //
    // WHAT COUNTS AS CREATED. Three different events, kept apart because they mean different things:
    //  - New: a world object this machine brought into existence. ZNetView.Awake makes a fresh ZDO for
    //    any networked prefab instantiated without one - a drop, a projectile, a spawned creature, a
    //    placed piece, a newly generated zone's vegetation.
    //  - Loaded: an existing world object built into the scene as it came into range. ZNetScene's
    //    30 Hz create pass instantiates it with ZNetView.m_initZDO set, and Awake adopts that ZDO
    //    instead of making one. Normal while moving; a prefab climbing here while standing still is
    //    being destroyed and rebuilt in a loop.
    //  - From players (server only): a world object a player's game created, reaching the server for
    //    the first time. ZDOMan.RPC_ZDOData makes a blank ZDO through the private
    //    CreateNewZDO(ZDOID, Vector3, int) overload, the only vanilla call that passes no prefab, and
    //    only then fills it in with ZDO.Deserialize, so the prefab is read in a Deserialize postfix
    //    for the ZDO that overload just returned. On a client the same path is every existing object
    //    streaming in from the server, which is not creation, so it is not counted there. A dedicated
    //    server only builds objects around its own reference point, so this is most of what it sees.
    //    A create for an object already destroyed is counted too, although vanilla destroys it again
    //    straight away: a player's game repeatedly resending one is worth seeing.
    //
    // Not counted: placement and zone-preview ghosts (ZNetView.m_forceDisableInit, m_ghost), which are
    // thrown away, and anything without a ZNetView (effects, UI), which never reaches the network.
    //
    // Read with jms-creations (this machine) or jms-creations-server (the server, relayed from an
    // admin client); see Common/Terminal/Commands/CreationCommands.cs. Turning the tracker on starts
    // a fresh count, and the clock for the per-minute rates starts at the first object counted.
    //
    // Client config: this is a diagnostic on one machine, like the debug toggle. Every hook returns on
    // its first line while it is off; on, it costs one dictionary update per object created, next to
    // the cost of instantiating the object at all.
    [HarmonyPatch]
    internal static class CreationTrackerPatch {
        internal static ConfigEntry<bool> Enabled;

        internal enum Sort { Total, New, Loaded, Players }

        private class PrefabCounts {
            public string Name;
            public int New;
            public int Loaded;
            public int FromPlayers;
            public int Total => New + Loaded + FromPlayers;
        }

        private static readonly Dictionary<int, PrefabCounts> CountsByPrefab = new Dictionary<int, PrefabCounts>();

        // Time.realtimeSinceStartup of the first object counted since the last reset, or -1 for none,
        // and of the moment the tracker was last turned off, which freezes the rates.
        private static float _countingSince = -1f;
        private static float _stoppedAt = -1f;

        // The blank ZDO RPC_ZDOData just made, waiting for the Deserialize that names its prefab.
        private static ZDO _pendingReceived;

        internal static void BindConfig() {
            Enabled = ValConfig.BindClientConfig(
                "Diagnostics",
                "Track Object Creation",
                false,
                "Diagnostic. Counts the objects created on this machine by prefab, for finding something " +
                "that spawns far more than it should. Read the counts with the jms-creations console " +
                "command, or jms-creations-server for the server's. Turning this on starts a fresh count. " +
                "Leave it off otherwise.",
                advanced: true);

            Enabled.SettingChanged += (_, __) => {
                if (Enabled.Value) {
                    Reset();
                } else {
                    _stoppedAt = Time.realtimeSinceStartup;
                }
            };
        }

        internal static void Reset() {
            CountsByPrefab.Clear();
            _countingSince = -1f;
            _stoppedAt = -1f;
            _pendingReceived = null;
        }

        // Awake clears m_initZDO as it adopts it, so whether this is a load has to be read beforehand.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Awake))]
        private static void ZNetViewAwakePrefix(out bool __state) {
            __state = ZNetView.m_initZDO != null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Awake))]
        private static void ZNetViewAwakePostfix(ZNetView __instance, bool __state) {
            if (Enabled == null || !Enabled.Value) { return; }

            // No ZDO: m_forceDisableInit or no world, and the view destroyed itself.
            ZDO zdo = __instance.m_zdo;
            if (zdo == null || __instance.m_ghost) { return; }

            PrefabCounts counts = CountsFor(zdo.GetPrefab());
            if (__state) {
                counts.Loaded++;
            } else {
                counts.New++;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), new[] { typeof(ZDOID), typeof(Vector3), typeof(int) })]
        private static void CreateNewZdoPostfix(ZDO __result, int prefabHashIn) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (prefabHashIn != 0) { return; }
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { return; }

            _pendingReceived = __result;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        private static void ZdoDeserializePostfix(ZDO __instance) {
            if (_pendingReceived == null || !ReferenceEquals(__instance, _pendingReceived)) { return; }

            _pendingReceived = null;
            CountsFor(__instance.GetPrefab()).FromPlayers++;
        }

        private static PrefabCounts CountsFor(int prefabHash) {
            if (!CountsByPrefab.TryGetValue(prefabHash, out PrefabCounts counts)) {
                if (CountsByPrefab.Count == 0) { _countingSince = Time.realtimeSinceStartup; }
                counts = new PrefabCounts { Name = PrefabName(prefabHash) };
                CountsByPrefab[prefabHash] = counts;
            }
            return counts;
        }

        // Named when first seen rather than when reported: the names come from ZNetScene, which is gone
        // once the world is left.
        private static string PrefabName(int prefabHash) {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            return prefab != null ? prefab.name : $"unknown prefab {prefabHash}";
        }

        // Lists the `top` prefabs with the highest `sort` count, or all of them when top is 0 or less.
        internal static void Report(TerminalOutput output, Sort sort, int top) {
            if (Enabled == null || !Enabled.Value) {
                output.Warning("Creation tracking is off. Turn on 'Track Object Creation' under Diagnostics to count.");
            }
            if (CountsByPrefab.Count == 0) {
                output.Info("No objects counted yet.");
                return;
            }

            float seconds = Elapsed();
            float minutes = Mathf.Max(seconds / 60f, 1f / 60f);
            int total = CountsByPrefab.Values.Sum(c => c.Total);
            output.Info($"{total:N0} objects created across {CountsByPrefab.Count:N0} prefabs in {FormatDuration(seconds)}, by {Label(sort)}:");

            List<PrefabCounts> rows = CountsByPrefab.Values
                .Where(c => Count(c, sort) > 0)
                .OrderByDescending(c => Count(c, sort))
                .ThenBy(c => c.Name)
                .ToList();
            if (rows.Count == 0) {
                // From players on a client, which never counts that column.
                output.Detail($"  None counted as {Label(sort)} here.");
                return;
            }

            int shown = top > 0 ? Mathf.Min(top, rows.Count) : rows.Count;
            for (int i = 0; i < shown; i++) {
                PrefabCounts c = rows[i];
                int count = Count(c, sort);
                output.Detail($"  {c.Name}: {count:N0} {Label(sort)} ({count / minutes:F1}/min) - new {c.New:N0}, loaded {c.Loaded:N0}, from players {c.FromPlayers:N0}");
            }
            if (shown < rows.Count) {
                output.Detail($"  ...and {rows.Count - shown:N0} more prefabs. Add 'all' to list every one.");
            }
        }

        private static float Elapsed() {
            if (_countingSince < 0f) { return 0f; }

            float end = _stoppedAt >= 0f ? _stoppedAt : Time.realtimeSinceStartup;
            return Mathf.Max(0f, end - _countingSince);
        }

        private static int Count(PrefabCounts counts, Sort sort) {
            switch (sort) {
                case Sort.New: return counts.New;
                case Sort.Loaded: return counts.Loaded;
                case Sort.Players: return counts.FromPlayers;
                default: return counts.Total;
            }
        }

        private static string Label(Sort sort) {
            switch (sort) {
                case Sort.New: return "new";
                case Sort.Loaded: return "loaded";
                case Sort.Players: return "from players";
                default: return "total";
            }
        }

        private static string FormatDuration(float seconds) {
            int whole = Mathf.FloorToInt(seconds);
            if (whole >= 3600) { return $"{whole / 3600}h {whole % 3600 / 60:00}m"; }
            if (whole >= 60) { return $"{whole / 60}m {whole % 60:00}s"; }
            return $"{whole}s";
        }
    }
}
