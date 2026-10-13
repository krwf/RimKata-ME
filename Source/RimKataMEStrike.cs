using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KRWF.RimKata;
using Verse;
using Verse.AI;

namespace KRWF.RimKataME
{
    internal static class MEStrike
    {
        [ThreadStatic] internal static MEAttackPlan CurrentPlan;
        internal static bool Active => CurrentPlan != null;
        private static readonly AccessTools.FieldRef<Verb, LocalTargetInfo> CurrentTarget =
            AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentTarget");
        private static readonly Dictionary<Type, MethodInfo> ShotMethods = new Dictionary<Type, MethodInfo>();

        internal static bool TryTarget(MEAttackPlan plan, IntVec3 cell, out Pawn target)
        {
            target = null;
            Pawn pawn = plan?.pawn;
            Verb verb = plan?.verb;
            if (plan == null || plan.cancelled || pawn?.Spawned != true || pawn.Dead || pawn.Downed
                || pawn.stances?.stunner?.Stunned == true || verb?.IsMeleeAttack != true
                || pawn.Map == null || !cell.IsValid || !cell.InBounds(pawn.Map)) return false;
            if (plan.weapon != null && (plan.weapon.Destroyed
                || pawn.equipment?.AllEquipmentListForReading.Contains(plan.weapon) != true)) return false;

            List<Thing> things = cell.GetThingList(pawn.Map);
            for (int i = 0; i < things.Count; i++)
            {
                // Preserve the explicitly selected victim; only incidental occupants need the enemy filter.
                if (things[i] is Pawn candidate && candidate != pawn
                    && !candidate.Dead
                    && (candidate == plan.target
                        || RimKataTargeting.IsPawnTargetStateValid(candidate)
                            && RimKataTargeting.IsAutomaticEnemy(pawn, candidate)))
                {
                    target = candidate;
                    break;
                }
            }
            if (target == null || !pawn.CanReachImmediate(target, PathEndMode.Touch)
                || !verb.IsUsableOn(target) || !verb.Available()) return false;
            return true;
        }

        internal static void Execute(MEAttackPlan plan, IntVec3 cell)
        {
            if (!TryTarget(plan, cell, out Pawn target)) return;
            Pawn pawn = plan.pawn;
            Verb verb = plan.verb;

            Type type = verb.GetType();
            if (!ShotMethods.TryGetValue(type, out MethodInfo shot))
            {
                shot = AccessTools.Method(type, "TryCastShot", Type.EmptyTypes);
                ShotMethods[type] = shot;
            }
            if (shot == null) return;

            // Invoke one native hit, not WarmupComplete: the parent ME action owns the cooldown.
            LocalTargetInfo previousTarget = CurrentTarget(verb);
            MEAttackPlan previousPlan = CurrentPlan;
            Rot4 previousFacing = pawn.Rotation;
            var previousFire = RimKataFireContext.Begin(verb, pawn, target,
                false, false, false, null, false, false, RimKataCloseDefensePrecheck.None);
            CurrentPlan = plan;
            CurrentTarget(verb) = target;
            try
            {
                shot.Invoke(verb, null);
            }
            finally
            {
                CurrentTarget(verb) = previousTarget;
                CurrentPlan = previousPlan;
                RimKataFireContext.End(verb, previousFire);
                // A side-cell strike must not turn the body away from its actual combat target.
                if (pawn.Spawned && !pawn.Dead && !pawn.Downed) pawn.Rotation = previousFacing;
            }
        }
    }
}
