using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;

namespace CommunityPatchExtras.Patches {
    // Lets the hammer's remove click take down boats and carts, which vanilla refuses. The only
    // vanilla way to get rid of a boat or cart is to hit it until it breaks. That already gives
    // back the full build cost and drops the cargo in a crate, so this adds no new loot. It just
    // saves the walk around swinging an axe at your own longship.
    //
    // THE BLOCK IS ONE SERIALIZED BOOL. Every vehicle prefab ships Piece.m_canBeRemoved = false
    // (Cart, Raft, Karve, VikingShip, VikingShip_Ashlands, Trailership, BatteringRam, Catapult
    // and Sled, checked against the Deep North rip), and Player.RemovePiece reads that field and
    // quietly gives up (Player.cs:2962). Nothing further down cares that the piece is a vehicle:
    // the ward, no-build-zone and crafting-station checks all apply as they do to a wall, and
    // WearNTear.Remove goes through the same Destroy -> DropResources -> m_onDestroyed chain that
    // smashing the vehicle does. So the removal itself is entirely vanilla's.
    //
    // WHY A TRANSPILER, AND ONLY ON RemovePiece. Setting the field to true on each vehicle would
    // also work, but m_canBeRemoved and Piece.CanBeRemoved are read elsewhere. WearNTear.UpdateWear
    // skips weather damage for anything CanBeRemoved says no to (WearNTear.cs:679), so the obvious
    // "add the occupancy check to Piece.CanBeRemoved" postfix would also make a cart immune to rain
    // and water while someone pulls it. Rewriting the two reads inside RemovePiece limits the change
    // to exactly the hammer click, and a toggle takes effect on the next click with nothing loaded
    // left to fix up.
    //
    // "NOBODY ATTACHED". The rule is checked on the machine doing the removing, so it has to be
    // readable from any machine, not only the vehicle's owner:
    //  - Carts: Vagon.IsAttached() falls back to the ZDO's attachJoint bool when the joint isn't
    //    local, so a cart someone else is pulling still reads as attached. The sled also has a seat,
    //    and Chair.IsInUse() is a distance check against every Player, so a rider is visible too.
    //  - Boats: nobody may be on board at all, not just at the helm. Removing a ship drops everyone
    //    on deck into the water. Vanilla has this check (Ship.CanBeRemoved), but Piece.CanBeRemoved
    //    returns early for any piece with a Container, so it only ever runs for the Raft. The
    //    Karve, Longship, Drakkar and Trailership all have a hold. So the check is repeated here
    //    against Ship.m_players. That list is filled by trigger volume, which remote players' colliders
    //    set off as well, and dead entries are skipped: a player who logs off or dies on deck is
    //    destroyed without OnTriggerExit and stays in the list.
    // A blocked removal shows vanilla's own "$msg_cantremovenow", the message a Raft with someone
    // aboard already shows.
    //
    // Deliberately NOT bypassed: the crafting station. Every vehicle, the Raft included, names the
    // workbench as its station, and CheckCanRemovePiece wants one in range. That is vanilla's rule
    // for every other piece too, so a boat moored away from base needs a workbench nearby (or the
    // NoWorkbench world modifier).
    //
    // SERVER-SYNCED, and held at vanilla on a server that does not run this mod. RPC_Remove is
    // honoured by any owner, modded or not, so without that hold a client could take apart boats on
    // a server whose admin never opted in. Uses the same per-session confirmation as
    // ZoneLoadRadiusPatch: this process is the server, or Jotunn has delivered this mod's config for
    // the current session.
    [HarmonyPatch]
    internal static class VehicleDeconstructPatch {
        internal static ConfigEntry<bool> Enabled;

        // The ZNet instance whose session Jotunn confirmed this mod's config for. Identity-compared so
        // a new session invalidates it.
        private static ZNet _confirmedFor;

        private static readonly FieldInfo CanBeRemovedField =
            AccessTools.Field(typeof(Piece), nameof(Piece.m_canBeRemoved));
        private static readonly MethodInfo CanBeRemovedMethod =
            AccessTools.Method(typeof(Piece), nameof(Piece.CanBeRemoved));
        private static readonly MethodInfo AllowRemovalMethod =
            AccessTools.Method(typeof(VehicleDeconstructPatch), nameof(AllowRemoval));
        private static readonly MethodInfo AllowRemovalNowMethod =
            AccessTools.Method(typeof(VehicleDeconstructPatch), nameof(AllowRemovalNow));

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                "Building",
                "Deconstruct Boats And Carts",
                true,
                "Whether the hammer's remove click can take down boats and carts, which vanilla " +
                "refuses. Vanilla's only way is to smash them, which gives back the same materials, " +
                "so this changes how, not what. Covers every boat, the cart, and the battering " +
                "ram, catapult and sled, which are built on the same cart class. Refused while " +
                "anyone is pulling or riding the cart, or standing anywhere on the boat. Cargo " +
                "drops in a crate, as when a vehicle breaks. The usual removal rules still apply: " +
                "wards, no-build zones, and a workbench in range. Server-synced; a client " +
                "connected to a server without this mod keeps vanilla behaviour.");

            SynchronizationManager.OnConfigurationSynchronized += OnConfigurationSynchronized;
        }

        private static void OnConfigurationSynchronized(object sender, ConfigurationSynchronizationEventArgs args) {
            if (args.UpdatedPluginGUIDs == null || !args.UpdatedPluginGUIDs.Contains(CommunityPatchExtras.PluginGUID)) { return; }

            if (ZNet.instance != null && !ZNet.instance.IsServer()) { _confirmedFor = ZNet.instance; }
        }

        private static bool Active() {
            if (Enabled == null || !Enabled.Value) { return false; }

            ZNet znet = ZNet.instance;
            if (znet == null) { return false; }

            return znet.IsServer() || ReferenceEquals(_confirmedFor, znet);
        }

        // The game has two vehicle classes. Ship is every boat. Vagon is everything pulled: the
        // cart, plus the battering ram, catapult and sled, which reuse it. GetComponentInChildren
        // matches how Piece.CanBeRemoved looks for its Ship. Only reached on a remove click, so the
        // hierarchy walk costs nothing that matters.
        private static bool IsVehicle(Piece piece, out Ship ship, out Vagon vagon) {
            ship = piece.GetComponentInChildren<Ship>();
            vagon = ship == null ? piece.GetComponentInChildren<Vagon>() : null;
            return ship != null || vagon != null;
        }

        private static bool IsOccupied(Ship ship, Vagon vagon) {
            if (ship != null) {
                foreach (Player player in ship.m_players) {
                    if (player != null) { return true; }
                }
                return false;
            }

            if (vagon.IsAttached()) { return true; }

            foreach (Chair chair in vagon.GetComponentsInChildren<Chair>()) {
                if (chair.IsInUse()) { return true; }
            }
            return false;
        }

        // Replaces RemovePiece's read of piece.m_canBeRemoved. Everything vanilla already allows
        // stays allowed; the only thing added is a vehicle while the feature is active.
        private static bool AllowRemoval(Piece piece) {
            if (piece.m_canBeRemoved) { return true; }

            return Active() && IsVehicle(piece, out _, out _);
        }

        // Replaces RemovePiece's call to piece.CanBeRemoved(), the "not right now" check that comes
        // after the ward and station checks. Vanilla's answer is kept as is. The occupancy rule only
        // applies to vehicles this feature opened up: a modded vehicle that ships removable keeps
        // whatever rules its author gave it.
        private static bool AllowRemovalNow(Piece piece) {
            if (!piece.CanBeRemoved()) { return false; }
            if (piece.m_canBeRemoved) { return true; }
            if (!IsVehicle(piece, out Ship ship, out Vagon vagon)) { return true; }

            return !IsOccupied(ship, vagon);
        }

        // Both replacements take the same stack as the instruction they replace: ldfld pops the
        // Piece and pushes the bool, and so does a static call taking the Piece. Works on a copy, and
        // returns the untouched input if either site is not there exactly once. That usually means a
        // game update moved it or another mod already rewrote the method; either way the feature
        // stays off rather than half-applied.
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Player), "RemovePiece")]
        private static IEnumerable<CodeInstruction> RemovePieceTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in instructions) { codes.Add(new CodeInstruction(instruction)); }

            int flagReads = 0;
            int checks = 0;
            foreach (CodeInstruction code in codes) {
                if (code.LoadsField(CanBeRemovedField)) {
                    code.opcode = OpCodes.Call;
                    code.operand = AllowRemovalMethod;
                    flagReads++;
                } else if (code.Calls(CanBeRemovedMethod)) {
                    code.opcode = OpCodes.Call;
                    code.operand = AllowRemovalNowMethod;
                    checks++;
                }
            }

            if (flagReads == 1 && checks == 1) { return codes; }

            Logger.LogWarning(
                $"Player.RemovePiece: expected one read of Piece.m_canBeRemoved and one call to " +
                $"Piece.CanBeRemoved, found {flagReads} and {checks}, so Deconstruct Boats And Carts " +
                "is inactive. A Valheim update or another mod has most likely changed the method.");
            return instructions;
        }
    }
}
