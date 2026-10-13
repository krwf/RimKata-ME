using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KRWF.RimKata;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKataME
{
    internal sealed class MEMovementState : IExposable
    {
        public MEMovementState() { }
        internal Pawn pawn;
        internal Thing target;
        internal ThingWithComps weapon;
        internal IntVec3 destination, backward;
        internal Vector3 startDraw;
        internal Rot4 facing;
        internal int startTick, jobId;
        internal bool straight, flying, resumed;
        internal bool wasMoving;
        internal LocalTargetInfo resumeTarget;
        internal PathEndMode resumeMode;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref target, "target");
            Scribe_References.Look(ref weapon, "weapon");
            Scribe_Values.Look(ref destination, "destination");
            Scribe_Values.Look(ref backward, "backward");
            Scribe_Values.Look(ref startDraw, "startDraw");
            Scribe_Values.Look(ref facing, "facing");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref jobId, "jobId", -1);
            Scribe_Values.Look(ref straight, "straight");
            Scribe_Values.Look(ref flying, "flying");
            Scribe_Values.Look(ref resumed, "resumed");
            Scribe_Values.Look(ref wasMoving, "wasMoving");
            Scribe_TargetInfo.Look(ref resumeTarget, "resumeTarget");
            Scribe_Values.Look(ref resumeMode, "resumeMode");
        }
    }

    public sealed class MEMovementRegistry : GameComponent
    {
        private readonly Game game;
        internal readonly ConcurrentDictionary<Pawn, MEMovementState> states =
            new ConcurrentDictionary<Pawn, MEMovementState>();
        private readonly List<MEMovementState> scratch = new List<MEMovementState>();
        private List<MEMovementState> saved;
        internal bool IsCurrent => ReferenceEquals(game, Current.Game);

        public MEMovementRegistry(Game game) { this.game = game; MEMovement.SetRegistry(this); }

        public override void GameComponentTick()
        {
            if (states.IsEmpty) return;
            scratch.Clear();
            scratch.AddRange(states.Values);
            foreach (MEMovementState state in scratch)
                if (states.TryGetValue(state.pawn, out var live) && live == state)
                    MEMovement.Tick(state);
            scratch.Clear();
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) saved = new List<MEMovementState>(states.Values);
            Scribe_Collections.Look(ref saved, "meBacksteps", LookMode.Deep);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            states.Clear();
            if (saved != null)
                foreach (MEMovementState state in saved)
                    if (state?.pawn != null) states[state.pawn] = state;
            saved = null;
        }

        public override void FinalizeInit()
        {
            MEMovement.SetRegistry(this);
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                if (!IsCurrent) return;
                foreach (MEMovementState state in states.Values)
                    MEMovement.Restore(state);
            });
        }
    }

    internal static class MEMovement
    {
        private const int BackstepTicks = 6;
        private const int RiseTicks = 6;
        private static readonly IntVec3[] Directions = { IntVec3.North, IntVec3.NorthEast,
            IntVec3.East, IntVec3.SouthEast, IntVec3.South, IntVec3.SouthWest,
            IntVec3.West, IntVec3.NorthWest };
        internal static readonly Type FlightType = AccessTools.TypeByName("KRWF.RimKata.RimKataFlyingKick");
        internal static readonly Type ApproachType = AccessTools.TypeByName("KRWF.RimKata.RimKataFlyingKickApproach");
        private static readonly Type MovementType = AccessTools.TypeByName("KRWF.RimKata.RimKataReactiveMovement");
        private static readonly Type ParticipantType = AccessTools.TypeByName("KRWF.RimKata.RimKataResponseVisualParticipantCache");
        private static readonly Func<Pawn, IntVec3, bool> Enter = AccessTools.MethodDelegate<Func<Pawn, IntVec3, bool>>(
            AccessTools.Method(MovementType, "Enter"));
        private static readonly Action<Pawn, IntVec3> RefreshPursuers = AccessTools.MethodDelegate<Action<Pawn, IntVec3>>(
            AccessTools.Method(MovementType, "RefreshNearbyPursuers"));
        private static readonly Func<Pawn, RimKataPawnCombatState, bool> CanStart =
            AccessTools.MethodDelegate<Func<Pawn, RimKataPawnCombatState, bool>>(AccessTools.Method(FlightType, "CanStart"));
        private static readonly Action<RimKataPawnCombatState> ClearFlight =
            AccessTools.MethodDelegate<Action<RimKataPawnCombatState>>(AccessTools.Method(FlightType, "Clear"));
        private static readonly MethodInfo BodyEntry = AccessTools.Method(ParticipantType, "BodyVisualFor");
        private static readonly MethodInfo StartFlight = AccessTools.Method(FlightType, "TryStart");
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, PathEndMode> PathMode =
            AccessTools.FieldRefAccess<Pawn_PathFollower, PathEndMode>("peMode");
        private static readonly AccessTools.FieldRef<RimKataFlyingKickState, int> Age =
            AccessTools.FieldRefAccess<RimKataFlyingKickState, int>("age");
        private static readonly AccessTools.FieldRef<RimKataFlyingKickState, int> ContactTicks =
            AccessTools.FieldRefAccess<RimKataFlyingKickState, int>("contactTicks");
        private static readonly AccessTools.FieldRef<RimKataFlyingKickState, int> ContactStartAge =
            AccessTools.FieldRefAccess<RimKataFlyingKickState, int>("contactStartAge");
        private static readonly AccessTools.FieldRef<RimKataFlyingKickState, int> TravelTicks =
            AccessTools.FieldRefAccess<RimKataFlyingKickState, int>("contactTravelTicks");
        private static MEMovementRegistry registry;
        [ThreadStatic] internal static MEMovementState starting;
        [ThreadStatic] internal static MEMovementState ticking;

        internal static void SetRegistry(MEMovementRegistry value)
        {
            if (registry == value) return;
            registry?.states.Clear();
            registry = value;
            starting = ticking = null;
        }

        internal static MEMovementState Get(Pawn pawn)
            => pawn != null && registry?.IsCurrent == true
                && registry.states.TryGetValue(pawn, out var state) ? state : null;

        internal static bool Blocks(Pawn pawn) => Get(pawn) != null;

        private static RimKataPawnCombatState Combat(Pawn pawn)
            => pawn?.Map?.GetComponent<RimKataMapComponent>()?.GetState(pawn, false);

        private static bool Valid(MEMovementState state)
            => state.pawn?.Spawned == true && !state.pawn.Dead && !state.pawn.Downed
                && state.pawn.Awake() && !state.pawn.InMentalState && !state.pawn.IsBurning()
                && state.pawn.stances?.stunner?.Stunned != true
                && state.pawn.CurJob?.loadID == state.jobId && MECombat.IsQualified(state.pawn)
                && state.weapon != null && !state.weapon.Destroyed
                && state.pawn.equipment?.AllEquipmentListForReading.Contains(state.weapon) == true;

        internal static void BeginBackstep(MEAttackPlan plan)
        {
            Pawn pawn = plan?.pawn;
            if (pawn == null || registry?.IsCurrent != true || Blocks(pawn)
                || pawn.pather == null || pawn.CurJob == null || !MECombat.IsQualified(pawn)) return;
            RimKataPawnCombatState combat = Combat(pawn);
            if (combat == null || combat.flyingKick != null || !CanStart(pawn, combat)) return;
            Thing target = plan.target;
            if (target?.Spawned != true || target.Map != pawn.Map) return;
            IntVec3 difference = target.Position - pawn.Position;
            if (difference == IntVec3.Zero) return;
            int behind = (Mathf.RoundToInt(Mathf.Repeat(difference.AngleFlat, 360f) / 45f) + 4) & 7;
            int side = Rand.Bool ? 1 : -1;
            int[] offsets = { 0, side, -side, side * 2, -side * 2 };
            IntVec3 destination = IntVec3.Invalid;
            foreach (int offset in offsets)
            {
                IntVec3 candidate = pawn.Position + Directions[(behind + offset + 8) & 7];
                if (CanEnter(pawn, candidate)) { destination = candidate; break; }
            }
            if (!destination.IsValid) return;
            var state = new MEMovementState {
                pawn = pawn, target = target, weapon = plan.weapon,
                destination = destination, backward = Directions[behind], startDraw = pawn.DrawPos,
                facing = Rot4.FromAngleFlat(difference.AngleFlat), startTick = Find.TickManager.TicksGame,
                jobId = pawn.CurJob.loadID, straight = destination - pawn.Position == Directions[behind],
                wasMoving = pawn.pather.Moving, resumeTarget = pawn.pather.Destination,
                resumeMode = PathMode(pawn.pather)
            };
            IntVec3 origin = pawn.Position;
            // Native entry claims the cell before any visual travel, just as sliding does.
            if (!Enter(pawn, destination)) { Resume(state); return; }
            if (!Valid(state)) return;
            registry.states[pawn] = state;
            combat.CancelCloseCombat();
            combat.dualCloseCombatActive = false;
            combat.dualCloseTarget = null;
            Face(state);
            RefreshPursuers(pawn, origin);
        }

        private static bool CanEnter(Pawn pawn, IntVec3 cell)
        {
            Map map = pawn.Map;
            if (!cell.InBounds(map) || !cell.WalkableBy(map, pawn)
                || !pawn.CanReachImmediate(cell, PathEndMode.Touch)) return false;
            Building_Door door = cell.GetDoor(map);
            if (door != null && (!door.Open || door.TicksTillFullyOpened > 0)) return false;
            foreach (Thing thing in cell.GetThingList(map))
                if (thing is Pawn other && other != pawn) return false;
            IntVec3 step = cell - pawn.Position;
            if (step.x != 0 && step.z != 0)
            {
                var context = map.pathing.For(pawn);
                if (PathUtility.BlocksDiagonalMovement(pawn.Position.x + step.x, pawn.Position.z,
                        context, pawn.CurJob.canBashFences)
                    || PathUtility.BlocksDiagonalMovement(pawn.Position.x, pawn.Position.z + step.z,
                        context, pawn.CurJob.canBashFences)) return false;
            }
            return true;
        }

        internal static void Tick(MEMovementState state)
        {
            RimKataPawnCombatState combat = Combat(state.pawn);
            if (!Valid(state) || combat == null)
            { Remove(state, false, true); return; }
            if (state.flying)
            {
                if (combat.flyingKick == null) { Remove(state, true, false); return; }
                if (!state.resumed && Age(combat.flyingKick) >= RiseTicks)
                {
                    state.resumed = true;
                    if (LiveTarget(state)) state.pawn.pather.StartPath(state.target, PathEndMode.Touch);
                }
                return;
            }
            if (state.pawn.Position != state.destination || !CanStart(state.pawn, combat))
            { Remove(state, false, false); return; }
            Face(state);
            if (Find.TickManager.TicksGame - state.startTick < BackstepTicks) return;
            // Exactly one admission check. A diagonal fallback or a pursuing victim does not retry later.
            if (state.straight && LiveTarget(state)
                && state.target.Position - state.pawn.Position == -state.backward * 2
                && TryStartFlight(state)) return;
            Remove(state, true, false);
        }

        private static bool LiveTarget(MEMovementState state)
            => state.target?.Spawned == true && !state.target.Destroyed && state.target.Map == state.pawn.Map
                && (!(state.target is Pawn victim) || !victim.Dead && !victim.Downed);

        private static bool TryStartFlight(MEMovementState state)
        {
            object entry = BodyEntry.Invoke(null, new object[] { state.pawn });
            if (entry == null) return false;
            MEMovementState previous = starting;
            starting = state;
            bool started;
            try
            {
                started = (bool)StartFlight.Invoke(null, new object[] { entry, state.target, -state.backward });
            }
            finally { starting = previous; }
            if (!started) return false;
            state.flying = true;
            state.resumed = false;
            state.pawn.pather.StopDead();
            MECombat.FlyingKickStarted(state.pawn, state.weapon);
            return true;
        }

        internal static void Restore(MEMovementState state)
        {
            if (!Valid(state) || !state.flying && state.pawn.Position != state.destination
                || state.flying && Combat(state.pawn)?.flyingKick == null)
            { Remove(state, false, true); return; }
            if (!state.flying || Age(Combat(state.pawn).flyingKick) < RiseTicks)
                state.pawn.pather?.StopDead();
            if (state.flying) state.resumed = false;
        }

        private static void Remove(MEMovementState state, bool resume, bool clearFlight)
        {
            registry?.states.TryRemove(state.pawn, out _);
            if (clearFlight && state.flying)
            {
                var combat = Combat(state.pawn);
                if (combat?.flyingKick != null) ClearFlight(combat);
            }
            state.pawn.Drawer?.tweener?.ResetTweenedPosToRoot();
            if (resume) Resume(state);
        }

        private static void Resume(MEMovementState state)
        {
            if (state.wasMoving && state.pawn?.Spawned == true && !state.pawn.Dead && !state.pawn.Downed
                && state.pawn.CurJob?.loadID == state.jobId && state.resumeTarget.IsValid
                && (!state.resumeTarget.HasThing || !state.resumeTarget.ThingDestroyed))
                state.pawn.pather?.StartPath(state.resumeTarget, state.resumeMode);
        }

        internal static void Face(MEMovementState state)
        {
            if (LiveTarget(state))
            {
                IntVec3 difference = state.target.Position - state.pawn.Position;
                if (difference != IntVec3.Zero) state.facing = Rot4.FromAngleFlat(difference.AngleFlat);
            }
            state.pawn.Rotation = state.facing;
        }

        internal static bool HoldPath(Pawn pawn)
        {
            var state = Get(pawn);
            if (state == null) return false;
            return !state.flying || Combat(pawn)?.flyingKick is RimKataFlyingKickState flight && Age(flight) < RiseTicks;
        }

        internal static void DrawPosition(Pawn pawn, ref Vector3 position)
        {
            MEMovementState state = Get(pawn);
            if (state == null || state.flying || pawn.Position != state.destination) return;
            float progress = Mathf.Clamp01((Find.TickManager.TicksGame - state.startTick) / (float)BackstepTicks);
            Vector3 location = Vector3.Lerp(state.startDraw, state.destination.ToVector3Shifted(), progress);
            position.x = location.x;
            position.z = location.z;
        }

        internal static void DelayContact(RimKataPawnCombatState combat)
        {
            var flight = combat?.flyingKick;
            if (starting?.pawn != combat?.pawn || flight == null || Age(flight) != 0) return;
            ContactStartAge(flight) = RiseTicks;
            ContactTicks(flight) += RiseTicks;
            // The rise takes place at the claimed cell center; native glide, hit, descent and fall stay intact.
            TravelTicks(flight) = Math.Max(1, ContactTicks(flight) - RiseTicks);
        }

        internal static bool Rising(RimKataPawnCombatState combat)
            => Get(combat?.pawn)?.flying == true && combat.flyingKick != null
                && Age(combat.flyingKick) < RiseTicks;

        internal static void KeepRiseStationary(RimKataPawnCombatState combat)
        {
            if (!Rising(combat)) return;
            ContactStartAge(combat.flyingKick) = RiseTicks;
            TravelTicks(combat.flyingKick) = Math.Max(1, ContactTicks(combat.flyingKick) - RiseTicks);
        }

        internal static Thing FlightJobTarget(Job job)
        {
            MEMovementState state = starting ?? ticking;
            return state != null && state.jobId == job?.loadID ? state.target : job?.targetA.Thing;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DrawPos), MethodType.Getter)]
    internal static class Patch_MEBackstepDraw
    {
        private static void Postfix(Pawn __instance, ref Vector3 __result)
            => MEMovement.DrawPosition(__instance, ref __result);
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick))]
    internal static class Patch_MEBackstepPath
    {
        private static bool Prefix(Pawn ___pawn) => !MEMovement.HoldPath(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.UpdateRotation))]
    internal static class Patch_MEBackstepFacing
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Pawn ___pawn)
        {
            MEMovementState state = MEMovement.Get(___pawn);
            if (state == null || state.flying) return true;
            MEMovement.Face(state);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_MEStationaryKickAdmission
    {
        private static MethodBase TargetMethod() => AccessTools.Method(MEMovement.FlightType, "ApprovedApproach");
        private static bool Prefix(Pawn pawn, Thing target, ref bool __result)
        {
            if (MEMovement.starting?.pawn != pawn || MEMovement.starting.target != target) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_MEStationaryKickRepeatGate
    {
        private static MethodBase TargetMethod() => AccessTools.Method(MEMovement.ApproachType, "WasUsedForCurrentJob");
        private static bool Prefix(Pawn pawn, ref bool __result)
        {
            if (MEMovement.starting?.pawn != pawn) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_MEStationaryKickRise
    {
        private static MethodBase TargetMethod() => AccessTools.Method(MEMovement.FlightType, "BeginContact");
        private static void Postfix(RimKataPawnCombatState combat) => MEMovement.DelayContact(combat);
    }

    [HarmonyPatch]
    internal static class Patch_MEStationaryKickRiseContact
    {
        private static MethodBase TargetMethod() => AccessTools.Method(MEMovement.FlightType, "Strike");
        private static bool Prefix(RimKataPawnCombatState combat, ref bool __result)
        {
            if (!MEMovement.Rising(combat)) return true;
            // A target stepping close during takeoff must not turn the stationary rise into an early hit.
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_MEStationaryKickRiseRetarget
    {
        private static MethodBase TargetMethod() => AccessTools.Method(MEMovement.FlightType, "RefreshApproach");
        private static void Postfix(RimKataPawnCombatState combat) => MEMovement.KeepRiseStationary(combat);
    }

    [HarmonyPatch]
    internal static class Patch_MEStationaryKickContext
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(MEMovement.FlightType, "Tick");
            yield return AccessTools.Method(MEMovement.FlightType, "Strike");
        }
        private static void Prefix(RimKataPawnCombatState combat, out MEMovementState __state)
        {
            __state = MEMovement.ticking;
            MEMovement.ticking = MEMovement.Get(combat?.pawn);
        }
        private static void Finalizer(MEMovementState __state) => MEMovement.ticking = __state;
    }

    [HarmonyPatch]
    internal static class Patch_MEStationaryKickTarget
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(MEMovement.FlightType, "Tick");
            yield return AccessTools.Method(MEMovement.FlightType, "Strike");
            yield return AccessTools.Method(MEMovement.FlightType, "StraightPath");
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var codes = new List<CodeInstruction>(instructions);
            FieldInfo target = AccessTools.Field(typeof(Job), nameof(Job.targetA));
            MethodInfo thing = AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Thing));
            MethodInfo replacement = AccessTools.Method(typeof(MEMovement), nameof(MEMovement.FlightJobTarget));
            int replacements = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (i + 1 < codes.Count && codes[i].opcode == OpCodes.Ldflda && Equals(codes[i].operand, target)
                    && codes[i + 1].Calls(thing))
                {
                    var code = new CodeInstruction(OpCodes.Call, replacement);
                    code.labels.AddRange(codes[i].labels);
                    code.labels.AddRange(codes[i + 1].labels);
                    code.blocks.AddRange(codes[i].blocks);
                    code.blocks.AddRange(codes[i + 1].blocks);
                    yield return code;
                    replacements++;
                    i++;
                }
                else yield return codes[i];
            }
            if (replacements == 0)
                throw new InvalidOperationException("[RimKata ME] Flying kick target hook missing: " + __originalMethod.Name);
        }
    }
}
