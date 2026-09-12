using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CommunityPatchExtras.Patches {
    // TEMPORARY - remove once the Player creation spam is understood. Reports who keeps creating
    // Player objects, off by default.
    //
    // THE SYMPTOM. Players report "[Jotunn.Managers.SkillManager] Registering N custom skills"
    // scrolling past many times a second while standing near workbenches, with the frame rate
    // falling through the floor. Nothing is re-registering anything: Jotunn prints that line from
    // a Skills.Awake postfix, and Player is the only in-game prefab carrying a Skills component. So
    // every line is a whole new Player object. A local repro showed a steady 20-40 per second in
    // multi-minute runs, which lines up with ZNetScene's 30 Hz create/destroy pass rather than
    // anything frame-bound.
    //
    // WHY A STACK TRACE ANSWERS IT. Unity runs Awake synchronously inside the managed Instantiate
    // or AddComponent call that made the object, so a trace taken in a Skills.Awake postfix names
    // the creator: ZNetScene.CreateObject for a networked player streaming in, or a mod's own
    // method if something is building Players itself. Vanilla only ever makes Players from
    // ZNetScene.CreateObject, Game.SpawnPlayer and the main menu's character preview.
    //
    // WHAT IS LOGGED, all prefixed [PlayerCreationDiag]:
    //  - once per distinct creator, the full trace plus the object and the ZDO it was built from
    //    (id, prefab, owner, player name, zone, distance from the local player);
    //  - every ten seconds, but only while more than three have been created in that window, a
    //    summary per creator: created, destroyed, average lifetime in frames, and which ZDO ids
    //    keep coming back.
    //
    // HOW TO READ IT. ZNetScene.CreateObject as the creator, the same ZDO id repeating, and
    // lifetimes around one frame is a create/destroy loop - whatever decides removals is
    // disagreeing with the create pass about that ZDO. A mod's method as the creator is that mod.
    // A single remote player walking into range shows up once and never reaches a summary.
    //
    // Client config: this is logging on one machine. The patches stay attached either way and
    // return on the first line when the toggle is off.
    [HarmonyPatch]
    internal static class PlayerCreationDiagnosticsPatch {
        internal static ConfigEntry<bool> Enabled;

        private const float SummaryInterval = 10f;
        private const int SummaryThreshold = 3;
        private const int SignatureFrames = 5;

        private class CreatorStats {
            public int Created;
            public int Destroyed;
            public long LifetimeFrames;
            public readonly Dictionary<string, int> CountByZdo = new Dictionary<string, int>();
        }

        private class LivePlayer {
            public string Creator;
            public int Frame;
        }

        private static readonly Dictionary<string, CreatorStats> _windowStats = new Dictionary<string, CreatorStats>();
        private static readonly HashSet<string> _reportedCreators = new HashSet<string>();
        private static readonly Dictionary<int, LivePlayer> _liveByInstance = new Dictionary<int, LivePlayer>();
        private static float _lastSummary;

        internal static void BindConfig() {
            Enabled = ValConfig.BindClientConfig(
                "Diagnostics",
                "Diagnose Player Object Creation",
                false,
                "Diagnostic. Logs a stack trace for each distinct source of new Player objects, and " +
                "a summary every ten seconds while they are being created in bulk. Turn this on " +
                "if 'Registering N custom skills' spams the log, then send the lines tagged " +
                "[PlayerCreationDiag]. Leave it off otherwise.",
                advanced: true);
        }

        // First, so the trace is taken before any other postfix on this method can throw.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(Skills), nameof(Skills.Awake))]
        private static void SkillsAwakePostfix(Skills __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            try {
                StackTrace trace = new StackTrace(1, false);
                string creator = CreatorSignature(trace);
                ZDO zdo = ResolveZdo(__instance);
                string zdoKey = zdo != null ? zdo.m_uid.ToString() : "no ZDO";

                if (!_windowStats.TryGetValue(creator, out CreatorStats stats)) {
                    stats = new CreatorStats();
                    _windowStats[creator] = stats;
                }
                stats.Created++;
                stats.CountByZdo.TryGetValue(zdoKey, out int zdoCount);
                stats.CountByZdo[zdoKey] = zdoCount + 1;
                _liveByInstance[__instance.gameObject.GetInstanceID()] = new LivePlayer { Creator = creator, Frame = Time.frameCount };

                if (_reportedCreators.Add(creator)) {
                    Logger.LogWarning($"[PlayerCreationDiag] New Player object from a creator not seen before.\n{DescribeObject(__instance, zdo)}\nStack:\n{trace}");
                }

                MaybeSummarize();
            } catch (System.Exception e) {
                Logger.LogWarning($"[PlayerCreationDiag] Failed to describe Skills.Awake: {e}");
            }
        }

        // A prefix, so the instance is still whole. Only Players seen waking above are tracked, so
        // anything created before the toggle was turned on is ignored.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), nameof(Player.OnDestroy))]
        private static void PlayerOnDestroyPrefix(Player __instance) {
            if (_liveByInstance.Count == 0) { return; }

            int id = __instance.gameObject.GetInstanceID();
            if (!_liveByInstance.TryGetValue(id, out LivePlayer live)) { return; }
            _liveByInstance.Remove(id);

            if (!_windowStats.TryGetValue(live.Creator, out CreatorStats stats)) { return; }
            stats.Destroyed++;
            stats.LifetimeFrames += Time.frameCount - live.Frame;
        }

        private static void MaybeSummarize() {
            float elapsed = Time.realtimeSinceStartup - _lastSummary;
            if (elapsed < SummaryInterval) { return; }

            int total = _windowStats.Values.Sum(s => s.Created);
            if (total > SummaryThreshold) {
                StringBuilder summary = new StringBuilder();
                summary.Append($"[PlayerCreationDiag] {total} Player objects created in the last {elapsed:F0}s (frame {Time.frameCount}, local player: {DescribeLocalPlayer()}).");
                foreach (KeyValuePair<string, CreatorStats> entry in _windowStats.OrderByDescending(e => e.Value.Created)) {
                    CreatorStats stats = entry.Value;
                    string lifetime = stats.Destroyed > 0 ? $"{(double)stats.LifetimeFrames / stats.Destroyed:F1} frames avg" : "none destroyed yet";
                    string zdos = string.Join(", ", stats.CountByZdo.OrderByDescending(z => z.Value).Take(3).Select(z => $"{z.Key} x{z.Value}"));
                    summary.Append($"\n  {stats.Created} created, {stats.Destroyed} destroyed ({lifetime}); top ZDOs: {zdos}\n    via {entry.Key}");
                }
                Logger.LogWarning(summary.ToString());
            }

            _windowStats.Clear();
            _lastSummary = Time.realtimeSinceStartup;
        }

        // ZNetScene.CreateObject parks the ZDO in ZNetView.m_initZDO before instantiating and
        // ZNetView.Awake moves it onto the view, so which one holds it depends on whether the view
        // woke before Skills did.
        private static ZDO ResolveZdo(Skills skills) {
            ZNetView nview = skills.GetComponent<ZNetView>();
            if (nview != null && nview.m_zdo != null) { return nview.m_zdo; }
            return ZNetView.m_initZDO;
        }

        private static string DescribeObject(Skills skills, ZDO zdo) {
            GameObject go = skills.gameObject;
            StringBuilder sb = new StringBuilder();
            sb.Append($"  object '{go.name}' id {go.GetInstanceID()} active {go.activeInHierarchy} parent '{(go.transform.parent != null ? go.transform.parent.name : "none")}' at {go.transform.position}");
            sb.Append($"\n  frame {Time.frameCount}, ZNetView.m_useInitZDO {ZNetView.m_useInitZDO}, m_forceDisableInit {ZNetView.m_forceDisableInit}, local player: {DescribeLocalPlayer()}");
            if (zdo == null) {
                sb.Append("\n  no ZDO");
                return sb.ToString();
            }

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            Vector3 pos = zdo.GetPosition();
            string distance = Player.m_localPlayer != null ? $"{Vector3.Distance(pos, Player.m_localPlayer.transform.position):F1}m from local player" : "no local player";
            sb.Append($"\n  ZDO {zdo.m_uid} prefab '{(prefab != null ? prefab.name : zdo.GetPrefab().ToString())}' owner {zdo.GetOwner()} (mine {zdo.IsOwner()}) persistent {zdo.Persistent} created {zdo.Created} valid {zdo.IsValid()}");
            sb.Append($"\n  ZDO position {pos} zone {ZoneSystem.GetZone(pos)}, {distance}, player name '{zdo.GetString(ZDOVars.s_playerName)}'");
            return sb.ToString();
        }

        private static string DescribeLocalPlayer() {
            Player local = Player.m_localPlayer;
            if (local == null) { return "none"; }

            ZDO zdo = local.m_nview != null ? local.m_nview.GetZDO() : null;
            return $"'{local.GetPlayerName()}' id {local.gameObject.GetInstanceID()} ZDO {(zdo != null ? zdo.m_uid.ToString() : "none")} zone {ZoneSystem.GetZone(local.transform.position)}";
        }

        // The first few managed frames past Unity's Instantiate/AddComponent plumbing, which is
        // enough to tell a ZNetScene create pass from a mod building Players itself. Harmony's
        // replacement for Skills.Awake has no declaring type and shows up as DMD<Skills::Awake>.
        private static string CreatorSignature(StackTrace trace) {
            List<string> frames = new List<string>();
            foreach (StackFrame frame in trace.GetFrames() ?? new StackFrame[0]) {
                MethodBase method = frame.GetMethod();
                if (method == null) { continue; }

                string typeName = method.DeclaringType?.FullName ?? "";
                if (typeName.StartsWith("UnityEngine.") || typeName == typeof(PlayerCreationDiagnosticsPatch).FullName
                    || typeName == typeof(Skills).FullName || method.Name.Contains("Skills::Awake")) { continue; }

                frames.Add(typeName.Length > 0 ? $"{typeName}.{method.Name}" : method.Name);
                if (frames.Count >= SignatureFrames) { break; }
            }
            return frames.Count > 0 ? string.Join(" <- ", frames) : "(no managed caller)";
        }
    }
}
