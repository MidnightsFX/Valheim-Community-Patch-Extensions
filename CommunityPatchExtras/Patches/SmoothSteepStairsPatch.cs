using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace CommunityPatchExtras.Patches {
    // Lets the local player walk up steep stairs (the Wood Ladder and Grausten Steep Stairs pieces, and
    // the world's Ashlands steep stairs and Fuling ladders) in one smooth motion instead of a run of hops.
    //
    // WHY VANILLA HOPS. Those pieces climb at 45 degrees, and a player slides off anything steeper than
    // 38 (Character.GetSlideAngle), so the game cannot let you simply walk up them. Instead each piece
    // carries three AutoJumpLedge triggers along its treads, and AutoJumpLedge.OnTriggerStay calls
    // Character.OnAutoJump, which throws a grounded character moving up the stair at 6 m/s up and 3 m/s
    // forward, at most once every 0.5 s, with the jump sound. Measured on 1.0.21: a 4 m climb is five or
    // six hops, airborne about 90% of the way, dropping back a little after each.
    //
    // THE CHANGE. While the local player touches one of those triggers and walks up the stair, the
    // trigger's hop is skipped, and the velocity Character.UpdateWalking aims for (at its last stop,
    // ApplyGroundForce) is turned to run along the stair: the speed the player is walking at goes
    // along a 45-degree line instead of along the ground. The physics engine still moves the body, so
    // it collides with walls, other pieces and creatures as usual, the climb happens at the player's
    // own walking, running or sprinting speed, and nothing is teleported. Leaving the stair, stopping,
    // walking down or sideways, jumping, swimming, riding or dodging all hand control straight back to
    // vanilla. At the top the climb's upward speed is dropped, so the player steps onto the floor
    // above rather than hopping over it (measured: no higher than vanilla's last hop lands).
    //
    // Measured on 1.0.21 for the same 4 m climb: no hops and no time in the air, in 4.5 s walking,
    // 1.5 s running and 0.9 s sprinting, where vanilla's hops take 1.5 to 2.7 s.
    //
    // Only the local player is affected. Creatures keep vanilla's hops, as does a player whose game
    // doesn't run this mod: each character's movement is simulated on the machine that owns it, and
    // other players see whichever climb that machine produced.
    //
    // SERVER-SYNCED: it changes how players move through the world, which is a world rule. Held at
    // vanilla until Jotunn's config sync confirms the server runs this mod, as PortalTagLengthPatch does.
    [HarmonyPatch]
    internal static class SmoothSteepStairsPatch {
        internal static ConfigEntry<bool> Enabled;

        // The ZNet instance whose session Jotunn confirmed this mod's config for. Identity-compared so
        // a new session invalidates it.
        private static ZNet _confirmedFor;

        // Every vanilla steep stair rises 2 m over 2 m of run.
        private const float Rise = 0.70710678f;   // sin 45
        private const float Run = 0.70710678f;    // cos 45

        // Vanilla's own test for "walking up the stair" in Character.OnAutoJump.
        private const float MinAlignment = 0.5f;

        // An upward speed this far above the climb's own is a jump or some other push: left alone.
        private const float JumpMargin = 2f;

        // The stair the local player is climbing: its object, its direction, and the physics step one of
        // its triggers was last touched in. The triggers sit along the treads, so a climb mostly runs
        // from one to the next. Where they leave a gap - the Grausten stairs' first block stands taller
        // than their first trigger, and a slow walk rises out of it before it is over the block - the
        // climb goes on while the player is pressed against the stair itself, for at most Hold seconds.
        // At the top the stair ends, nothing is in front, and the climb stops there.
        private static Transform _stair;
        private static Vector3 _climbDir;
        private static float _touchedAt = float.NegativeInfinity;
        private const float Hold = 0.5f;

        // The top of the stair's highest trigger: a climb that ends with the feet above it is at the top.
        private static float _stairTop = float.NegativeInfinity;

        // The physics step the climb last set the player's velocity in.
        private static float _climbedAt = float.NegativeInfinity;

        // How far ahead of the player's capsule "pressed against the stair" looks.
        private const float Reach = 0.1f;
        private static readonly Collider[] Overlaps = new Collider[32];

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                "Movement",
                "Smooth Steep Stairs",
                true,
                "Whether you walk up ladders and other steep stairs in one smooth motion, at your " +
                "walking, running or sprinting speed. Vanilla throws you up them in a series of hops. " +
                "Only changes how your own character climbs; creatures keep vanilla behaviour. " +
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

        // Whether the player is walking up a stair that climbs along dir, in a state vanilla would hop.
        private static bool WalkingUp(Player player, Vector3 dir) {
            if (player.IsDead() || player.IsAttached() || player.IsSwimming() || player.IsTeleporting() ||
                player.InDodge() || player.IsKnockedBack() || player.IsStaggering() || player.IsDebugFlying()) {
                return false;
            }

            Vector3 move = player.m_moveDir;
            move.y = 0f;
            return move.sqrMagnitude > 0.01f && Vector3.Dot(move.normalized, dir) >= MinAlignment;
        }

        private static Vector3 Flat(Vector3 v) {
            v.y = 0f;
            return v;
        }

        // Whether the climb that started at a trigger still holds this step.
        private static bool Climbing(Player player) {
            float since = Time.fixedTime - _touchedAt;
            if (since <= Time.fixedDeltaTime * 2f) { return true; }

            return since <= Hold && _stair && AgainstStair(player);
        }

        // Whether the player's capsule, moved a little up the stair, runs into the stair's own colliders.
        private static bool AgainstStair(Player player) {
            CapsuleCollider capsule = player.m_collider;
            Transform t = capsule.transform;
            Vector3 center = t.TransformPoint(capsule.center) + _climbDir * Reach;
            float radius = capsule.radius * Mathf.Max(t.lossyScale.x, t.lossyScale.z);
            float half = Mathf.Max(0f, capsule.height * t.lossyScale.y * 0.5f - radius);

            int count = Physics.OverlapCapsuleNonAlloc(center - Vector3.up * half, center + Vector3.up * half, radius,
                Overlaps, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++) {
                if (Overlaps[i].transform.IsChildOf(_stair)) { return true; }
            }
            return false;
        }

        // The ledge's trigger: claims the climb for the local player and skips its hop.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AutoJumpLedge), nameof(AutoJumpLedge.OnTriggerStay))]
        private static bool OnTriggerStayPrefix(AutoJumpLedge __instance, Collider collider) {
            Player player = Player.m_localPlayer;
            if (!player || collider.gameObject != player.gameObject || !Active()) { return true; }

            Vector3 dir = Flat(__instance.transform.forward).normalized;
            if (!WalkingUp(player, dir)) { return true; }

            // A climb starts on the ground, as vanilla's hop does, and then carries itself.
            if (!player.IsOnGround() && !Climbing(player)) { return true; }

            ZNetView view = __instance.GetComponentInParent<ZNetView>();
            Transform stair = view ? view.transform : __instance.transform.root;
            if (stair != _stair) {
                _stair = stair;
                _stairTop = float.NegativeInfinity;
                foreach (AutoJumpLedge ledge in stair.GetComponentsInChildren<AutoJumpLedge>()) {
                    Collider trigger = ledge.GetComponent<Collider>();
                    if (trigger) { _stairTop = Mathf.Max(_stairTop, trigger.bounds.max.y); }
                }
            }
            _climbDir = dir;
            _touchedAt = Time.fixedTime;
            return false;
        }

        // The last word on the velocity Character.UpdateWalking aims for this step: vanilla turns the
        // difference between `vel` and the body's velocity into a velocity change right after this
        // returns, so setting the climb here makes the body move up the stair and nothing else.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Character), nameof(Character.ApplyGroundForce))]
        private static void ApplyGroundForcePostfix(Character __instance, ref Vector3 vel, Vector3 targetVel) {
            if (Time.fixedTime - _touchedAt > Hold && Time.fixedTime - _climbedAt > Hold) { return; }

            Player player = Player.m_localPlayer;
            if (!player || !ReferenceEquals(__instance, player)) { return; }

            bool climbedLastStep = Time.fixedTime - _climbedAt <= Time.fixedDeltaTime * 1.5f;
            if (Climb(player, ref vel, targetVel)) {
                _climbedAt = Time.fixedTime;
                return;
            }

            // Past the stair's top trigger the climb is over: its upward speed is not carried on into a
            // hop over the floor above. Below it this is only the gap between two triggers, which the
            // body crosses on its own speed. A jump ends a climb too, but Climb forgets the climb then.
            if (climbedLastStep && vel.y > 0f && player.transform.position.y >= _stairTop) { vel.y = 0f; }
        }

        // Sets this step's velocity up the stair; false when the player is not climbing.
        private static bool Climb(Player player, ref Vector3 vel, Vector3 targetVel) {
            if (!Climbing(player)) { return false; }

            if (!Active() || !WalkingUp(player, _climbDir)) {
                _touchedAt = float.NegativeInfinity;
                return false;
            }

            // targetVel is the walking velocity the player's input asks for, laid along the ground. Its
            // length is the speed whatever the ground's slope (against a tread's edge it points almost
            // straight up), and the input says how much of it goes up the stair. Taken from input rather
            // than from the body, which a tread holds back.
            float speed = targetVel.magnitude * Vector3.Dot(Flat(player.m_moveDir).normalized, _climbDir);
            if (speed <= 0f) { return false; }

            float rise = speed * Rise;
            if (vel.y > rise + JumpMargin) {
                _touchedAt = float.NegativeInfinity;
                _climbedAt = float.NegativeInfinity;
                return false;
            }

            // Keep any sideways part of the movement; the part along the stair goes up it.
            Vector3 flat = Flat(vel);
            Vector3 sideways = flat - _climbDir * Vector3.Dot(flat, _climbDir);
            vel = sideways + _climbDir * (speed * Run) + Vector3.up * rise;

            // The climber stands on the stair. Without this, a step in which the body happened not to
            // touch a tread counts as airborne, and vanilla then applies only a tenth of the change
            // (m_airControl) and plays the falling animation.
            player.m_lastGroundTouch = 0f;

            // Pressed against a tread's edge, the walking friction vanilla gives a grounded player
            // (UpdateBodyFriction, 0.4) holds the body back from sliding up it. Frictionless, as vanilla
            // makes a player in the air, for this step; Character.UpdateMotion sets it again next step.
            PhysicsMaterial material = player.m_collider.material;
            material.staticFriction = 0f;
            material.dynamicFriction = 0f;
            return true;
        }
    }
}
