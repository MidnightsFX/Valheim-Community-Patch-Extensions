using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using CommunityPatchExtras.Common;

namespace CommunityPatchExtras.Patches {
    // Exposes vanilla's zone load radii - ZoneSystem.m_activeArea (every object, shipped value 2 =
    // a 5x5-zone / 320 m square) and m_activeDistantArea (distant-flagged prefabs only, shipped 2,
    // extending the square by that many more zone rings). Both are plain scene-deserialized fields
    // that nothing in the game ever writes; the game reads them live everywhere that matters, so
    // writing them is the whole feature.
    //
    // Why: entering a heavily built-up area streams its objects in over the approach, and the
    // Community Patch's "Spawn Burst Divisor" throttles that stream to protect frame time. Each +1
    // of load radius hands the throttled stream ~64 m more approach runway, so pop-in completes
    // before arrival instead of during it.
    //
    // The one rule that makes this safe in every environment: THE SERVER'S VALUES ARE THE ONLY
    // TRUTH. ZDOMan.CreateSyncList sends each peer whatever the SERVER's fields say (ZDOMan.cs:738)
    // - the values are never negotiated - and a client that raises its own m_activeArea without the
    // server sending the extra ring does not just render empty ground: ZoneSystem.IsActiveAreaLoaded
    // then demands the larger square before ZNetScene.CreateObjectsSorted spawns ANYTHING
    // (ZNetScene.cs:157), which can stall all object population. So Apply() only uses the config
    // values when this process is the server, or when Jotunn's config sync has delivered the
    // server's values for this mod in this session; otherwise it holds the fields at the scene's
    // own vanilla values (captured in the Awake postfix rather than hardcoded, so an Irongate
    // change to the shipped numbers degrades gracefully). Jotunn additionally locks and resets
    // every IsAdminOnly entry on a pure client at ZNet.Start, closing the edit-while-connected
    // hole. Re-check SynchronizationManager's semantics on Jotunn major updates - this guard is
    // built on them.
    //
    // Knock-ons of raising the near radius, documented rather than patched (all verified in the
    // decompiled sources):
    //  - The ownership/"activated" ring is m_activeArea - 1 (ZDOMan.ReleaseNearbyZDOS,
    //    ZNetScene.InActiveArea), so creatures, spawners and structural wear are actively
    //    simulated in a wider ring around each player - a real gameplay-surface change, which is
    //    why the near default stays exactly vanilla.
    //  - Steady-state loaded objects scale as (2a+1)^2: radius 3 is ~double radius 2, radius 4
    //    ~triple. The distant ring holds only lightweight distant-flagged prefabs and is far
    //    cheaper per zone, which is why its default IS raised.
    //  - StaticPhysics copies m_activeArea per-instance in its Awake, so statics spawned before a
    //    runtime change keep the old radius until recreated; self-heals as areas re-stream.
    //  - The Community Patch's own ring-aware fixes (scene idle skip, spawn queue cache) snapshot
    //    the radii and re-validate on change - no coupling.
    [HarmonyPatch(typeof(ZoneSystem))]
    internal static class ZoneLoadRadiusPatch {
        internal static ConfigEntry<int> ActiveArea;
        internal static ConfigEntry<int> DistantArea;

        // The scene's own values, captured before anything touches them; what unconfirmed clients
        // are held at.
        private static int _vanillaActive;
        private static int _vanillaDistant;
        private static bool _vanillaCaptured;

        // The ZNet instance whose session Jotunn confirmed our synced values for. Identity-compared
        // so a new session self-invalidates; the null guard matters, or teardown reads as confirmed.
        private static ZNet _confirmedFor;

        internal static void BindConfig() {
            ActiveArea = ValConfig.BindServerConfig(
                "Zone Loading",
                "Zone Load Radius",
                2,
                "How many zone rings (64 m each) of full objects load around each player. 2 is " +
                "exactly vanilla. Each +1 gives the Community Patch's throttled spawn stream one " +
                "more zone of approach runway into built-up areas, but roughly doubles the " +
                "steady-state loaded object count and widens the ring where creatures and " +
                "spawners actively simulate. Server's value wins for everyone.",
                false,
                2,
                4);

            DistantArea = ValConfig.BindServerConfig(
                "Zone Loading",
                "Distant Zone Load Radius",
                4,
                "How many further zone rings of distant-flagged objects (lightweight landmark " +
                "props) load beyond the full ring. Vanilla is 2; these are cheap per zone, so the " +
                "default is raised for better long-range silhouettes. Server's value wins for " +
                "everyone.",
                false,
                2,
                6);

            ActiveArea.SettingChanged += (sender, args) => ScheduleApply();
            DistantArea.SettingChanged += (sender, args) => ScheduleApply();

            // The confirmation flag AND a re-apply: with Config Apply Delay at 0, the sync's own
            // SettingChanged applies synchronously BEFORE this event fires and clamps to vanilla -
            // scheduling again from here heals that ordering.
            SynchronizationManager.OnConfigurationSynchronized += OnConfigurationSynchronized;
        }

        private static void ScheduleApply() => ConfigChangeDebouncer.Schedule(typeof(ZoneLoadRadiusPatch), Apply);

        private static void OnConfigurationSynchronized(object sender, ConfigurationSynchronizationEventArgs args) {
            if (args.UpdatedPluginGUIDs == null || !args.UpdatedPluginGUIDs.Contains(CommunityPatchExtras.PluginGUID)) { return; }

            if (ZNet.instance != null && !ZNet.instance.IsServer()) { _confirmedFor = ZNet.instance; }

            ScheduleApply();
        }

        // The fields are freshly scene-deserialized here and nothing has written them yet.
        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(ZoneSystem __instance) {
            _vanillaActive = __instance.m_activeArea;
            _vanillaDistant = __instance.m_activeDistantArea;
            _vanillaCaptured = true;
        }

        // ZNet is alive by ZoneSystem.Start (vanilla's own Start dereferences it), so the
        // server-or-confirmed question is answerable. On a joining client this lands before the
        // config sync and is a deliberate no-op (vanilla over vanilla); the sync event applies the
        // server's values moments later.
        [HarmonyPostfix]
        [HarmonyPatch("Start")]
        private static void StartPostfix() => Apply("world start");

        private static void Apply() => Apply("setting changed");

        private static void Apply(string reason) {
            ZoneSystem zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null || !_vanillaCaptured) { return; }

            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            bool trusted = isServer || (_confirmedFor != null && ReferenceEquals(_confirmedFor, ZNet.instance));

            int area = trusted && ActiveArea != null ? ActiveArea.Value : _vanillaActive;
            int distant = trusted && DistantArea != null ? DistantArea.Value : _vanillaDistant;

            if (zoneSystem.m_activeArea == area && zoneSystem.m_activeDistantArea == distant) { return; }

            zoneSystem.m_activeArea = area;
            zoneSystem.m_activeDistantArea = distant;

            string authority = isServer ? "server authority" : trusted ? "server-synced" : "unconfirmed client, holding vanilla";
            Logger.LogInfo($"Zone load radius applied: active {area}, distant {distant} ({reason}; {authority}).");
        }
    }
}
