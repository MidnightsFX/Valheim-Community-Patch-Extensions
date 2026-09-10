using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using CommunityPatchExtras.Common;

namespace CommunityPatchExtras.Patches {
    // Fills the two gaps left by vanilla's Simulation Distance setting.
    //
    // WHAT CHANGED UNDER THIS PATCH. Valheim replaced ZoneSystem.m_activeArea and
    // m_activeDistantArea - the two plain scene fields this patch used to write - with a
    // SimulationDistance struct (near rings, far rings, and a "classic" flag choosing a square
    // area over a circular one) behind a real graphics setting, levels 0-6 mapped by
    // SimulationDistance.GetSimulationDistance. Vanilla now negotiates it as well: a client sends
    // its desired value to the server, the server grants min(desired, its own) in
    // ZNet.RPC_RequestValidSimulationDistance, and ZDOMan streams each peer objects at exactly the
    // distance that peer was granted (ZDOMan.cs:1272). That is the same server-authority contract
    // this patch used to enforce by hand, so the hand-rolled version is gone - vanilla's handshake
    // does it, and does it in more places than a mod could reach.
    //
    // WHAT IS STILL MISSING, which is what this patch now does:
    //
    //  1. THE FAR RING IS NO LONGER TUNABLE AT ALL. Every entry in vanilla's level table ships
    //     FarSimulationDistance = 2 - level 6 widens the near ring to 6 and still stops distant
    //     objects at two rings. The old m_activeDistantArea could be raised; nothing in vanilla
    //     can now. These are lightweight distant-flagged landmark props and are cheap per zone,
    //     which is why this mod raised them by default before and still does.
    //
    //  2. A SERVER CANNOT GRANT MORE THAN IT PICKED FOR ITSELF. The server's own value comes from
    //     its graphics settings, which default to level 2 - old vanilla - so a dedicated server
    //     caps every client at (2, 2) however high the player set the slider. Irongate's answer is
    //     the -simulationdistance launch argument (FejdStartup.cs:603), which plenty of hosting
    //     panels will not let an admin add and which needs a restart to change. Server Simulation
    //     Distance Cap is the same knob as a live, admin-only config entry. It only ever raises,
    //     so a server already set higher by either of vanilla's routes keeps what it had.
    //
    // HOW: one postfix on ZNet.GetDesiredSimulationDistance, the single method feeding the
    // handshake, the client's own min() against the granted reply, and GetSyncedSimulationDistance.
    // Widening its answer therefore widens every consumer - ZoneSystem.ApplySettings, ZDOMan,
    // Heightmap, Water - through vanilla's own paths, and leaves no second copy of the state to
    // keep honest.
    //
    // WHY THE NEAR RING IS OTHERWISE LEFT ALONE. It is now a real graphics setting, and how many
    // zones a machine can simulate is a property of that machine, not of the world. The cap below
    // bounds the player's choice; within that bound the choice is theirs, which is what vanilla
    // intends and what this mod's old "default 2 = exactly vanilla" was approximating anyway.
    //
    // WHY THE FAR RING STILL NEEDS A TRUST CHECK, when the near ring no longer does. Vanilla's
    // min() cannot arbitrate it: SimulationDistance's < operator only orders values whose far
    // rings match, so a client wanting (2, 4) against a vanilla server at (2, 2) is *incomparable*,
    // and both RPC_RequestValidSimulationDistance and RPC_ValidatedSimulationDistance fall through
    // to the right-hand operand when neither side is less. The client would end up applying its
    // own (2, 4) while the server keeps streaming distant objects at two rings. That is not a
    // stall - IsActiveAreaLoaded reads only the near ring - but it is two rings of zone generation
    // for objects that never arrive, so the far value is held at vanilla until Jotunn's config sync
    // confirms the server is running this mod too.
    //
    // Knock-ons of a raised cap, documented rather than patched:
    //  - The ownership/"activated" ring is derived from the near distance (ZDOMan.cs:956,
    //    ZNetScene.InActiveArea), so a client that takes up a raised cap actively simulates
    //    creatures, spawners and structural wear in a wider ring. That is vanilla's own trade for
    //    its own setting; the cap here defaults to vanilla and moves only if an admin moves it.
    //  - Raising the near ring past 2 also switches the shape from a classic square to a circle,
    //    because that is what vanilla's level table does for every level above 2.
    //  - Near-ring objects scale as roughly (2n+1)^2. The far ring holds only distant-flagged
    //    prefabs and is far cheaper per zone, which is why it is the one raised by default.
    [HarmonyPatch(typeof(ZNet))]
    internal static class ZoneLoadRadiusPatch {
        internal static ConfigEntry<int> ServerNearCap;
        internal static ConfigEntry<int> DistantArea;

        // The ZNet instance whose session Jotunn confirmed our synced values for. Identity-compared
        // so a new session self-invalidates; the null guard matters, or teardown reads as confirmed.
        private static ZNet _confirmedFor;

        internal static void BindConfig() {
            ServerNearCap = ValConfig.BindServerConfig(
                "Zone Loading",
                "Server Simulation Distance Cap",
                2,
                "The highest Simulation Distance this server will grant a client, in zone rings of " +
                "64 m. 2 is exactly vanilla's default and changes nothing. Vanilla lets a player " +
                "pick up to 6 in the graphics menu but caps them at whatever the server itself is " +
                "set to, which on a dedicated server is 2 unless it was launched with " +
                "-simulationdistance - so raising this is what lets players actually use the " +
                "setting. It only raises the ceiling: each client still runs at its own chosen " +
                "value, and a wider ring costs that client roughly (2n+1)^2 loaded objects and " +
                "widens the ring where creatures and spawners actively simulate.",
                false,
                2,
                6);

            DistantArea = ValConfig.BindServerConfig(
                "Zone Loading",
                "Distant Zone Load Radius",
                4,
                "How many further zone rings of distant-flagged objects (lightweight landmark " +
                "props) load beyond the simulated ring. Vanilla ships 2 at every Simulation " +
                "Distance and offers no way to change it. These are cheap per zone, so the default " +
                "is raised for better long-range silhouettes. Server's value wins for everyone; a " +
                "client connected to a server without this mod stays at vanilla.",
                false,
                2,
                6);

            ServerNearCap.SettingChanged += (sender, args) => ScheduleRenegotiate();
            DistantArea.SettingChanged += (sender, args) => ScheduleRenegotiate();

            // The confirmation flag AND a re-negotiation: with Config Apply Delay at 0, the sync's own
            // SettingChanged runs synchronously BEFORE this event fires and so negotiates while the
            // far value is still clamped to vanilla - scheduling again from here heals that ordering.
            SynchronizationManager.OnConfigurationSynchronized += OnConfigurationSynchronized;
        }

        private static void ScheduleRenegotiate() =>
            ConfigChangeDebouncer.Schedule(typeof(ZoneLoadRadiusPatch), Renegotiate);

        private static void OnConfigurationSynchronized(object sender, ConfigurationSynchronizationEventArgs args) {
            if (args.UpdatedPluginGUIDs == null || !args.UpdatedPluginGUIDs.Contains(CommunityPatchExtras.PluginGUID)) { return; }

            if (ZNet.instance != null && !ZNet.instance.IsServer()) { _confirmedFor = ZNet.instance; }

            ScheduleRenegotiate();
        }

        // Whether this process may act on the synced far value: it is the server, or Jotunn has
        // delivered the server's config for this mod in this very session.
        private static bool TrustedForSyncedValues(ZNet znet) =>
            znet.IsServer() || (_confirmedFor != null && ReferenceEquals(_confirmedFor, znet));

        // Vanilla's own re-entry point. It re-reads GetDesiredSimulationDistance - and so this
        // patch - then applies the result directly when this process is the server, or re-requests
        // it from the server otherwise, which is also the only thing that refreshes the per-peer
        // record ZDOMan streams from. It compares against the value already in force first, so a
        // change that turns out to be a no-op costs nothing.
        private static void Renegotiate() {
            ZNet znet = ZNet.instance;
            if (znet == null) { return; }

            znet.SimulationDistanceServerHandshake();
        }

        // This is a hot path, not a startup one: GetSyncedSimulationDistance calls through here, and
        // ZNetScene.PointInsideActiveArea calls that once per point tested, so WearNTear,
        // StaticPhysics, SpawnArea and ZDOMan reach it thousands of times a frame. Hence the
        // ordering below - the config comparison is a field read and settles the common case where
        // both entries are at their defaults, before either the IsServer() or the trust check runs.
        // Both branches only ever raise, so a value already higher by any other route survives.
        //
        // IsServer() covers a dedicated server, a listen server's host and singleplayer alike -
        // ZNet.IsSinglePlayer is defined as a server that is not open, so it implies this.
        [HarmonyPostfix]
        [HarmonyPatch("GetDesiredSimulationDistance")]
        private static void GetDesiredSimulationDistancePostfix(ZNet __instance, ref SimulationDistance __result) {
            int near = __result.NearSimulationDistance;
            int far = __result.FarSimulationDistance;
            bool classic = __result.IsClassic;

            if (ServerNearCap != null && ServerNearCap.Value > near && __instance.IsServer()) {
                near = ServerNearCap.Value;
                // Vanilla's level table is classic only at levels 0 and 2; every wider level is
                // circular, which is the cheaper shape and the one the rest of the game is tuned for.
                classic = false;
            }

            if (DistantArea != null && DistantArea.Value > far && TrustedForSyncedValues(__instance)) {
                far = DistantArea.Value;
            }

            if (near == __result.NearSimulationDistance
                && far == __result.FarSimulationDistance
                && classic == __result.IsClassic) {
                return;
            }

            __result = new SimulationDistance(near, far, classic);
        }

        // The one honest place to report from: vanilla routes both the server applying its own
        // value and a client applying what the server granted it through here, so this logs what
        // is actually in force rather than what was asked for.
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZNet.ApplySimulationDistance))]
        private static void ApplySimulationDistancePostfix(SimulationDistance simulationDistance) {
            Logger.LogInfo(
                $"Simulation distance in effect: near {simulationDistance.NearSimulationDistance}, " +
                $"far {simulationDistance.FarSimulationDistance} " +
                $"({(simulationDistance.IsClassic ? "square" : "circular")}).");
        }
    }
}
