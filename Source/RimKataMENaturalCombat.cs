using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KRWF.RimKata;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKataME
{
    internal sealed class MENaturalSchedule : IExposable
    {
        public MENaturalSchedule() { }
        internal Pawn pawn;
        internal MEAttackPlan next, executing;
        internal List<MEAttackPlan> tails = new List<MEAttackPlan>();
        internal bool hasAttacked, resultPending, reduced;
        internal Verb completedVerb;
        internal int completionTick = -1, completionCooldown;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Deep.Look(ref next, "next");
            Scribe_Collections.Look(ref tails, "tails", LookMode.Deep);
            Scribe_Values.Look(ref hasAttacked, "hasAttacked");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                tails ??= new List<MEAttackPlan>();
                executing = null;
                resultPending = reduced = false;
                completedVerb = null;
                completionTick = -1;
            }
        }
    }

    public sealed class MENaturalCombatComponent : GameComponent
    {
        internal List<MENaturalSchedule> schedules = new List<MENaturalSchedule>();
        internal readonly Dictionary<Pawn, MENaturalSchedule> byPawn = new Dictionary<Pawn, MENaturalSchedule>();
        public MENaturalCombatComponent(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref schedules, "RimKataME_naturalSchedules", LookMode.Deep);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            schedules ??= new List<MENaturalSchedule>();
            byPawn.Clear();
            foreach (MENaturalSchedule slot in schedules)
                if (slot?.pawn != null) byPawn[slot.pawn] = slot;
        }
        public override void GameComponentTick() => MENaturalCombat.Tick(this);
    }

    internal static class MENaturalCombat
    {
        internal sealed class ShotScope
        {
            internal MENaturalSchedule slot;
            internal Verb verb;
            internal LocalTargetInfo originalTarget;
            internal Rot4 originalFacing;
            internal bool redirected;
        }
        private static readonly AccessTools.FieldRef<Verb, LocalTargetInfo> CurrentTarget =
            AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentTarget");
        private static readonly Func<Pawn, IReadOnlyList<Verb>, Thing, bool, bool, Verb> Select =
            AccessTools.MethodDelegate<Func<Pawn, IReadOnlyList<Verb>, Thing, bool, bool, Verb>>(
                AccessTools.Method(typeof(RimKataDualWeaponController), "SelectMeleeVerb"));
        private static readonly IntVec3[] Directions = { IntVec3.North, IntVec3.NorthEast,
            IntVec3.East, IntVec3.SouthEast, IntVec3.South, IntVec3.SouthWest,
            IntVec3.West, IntVec3.NorthWest };
        private static int Now => Find.TickManager?.TicksGame ?? 0;
        private static MENaturalCombatComponent Data => Current.Game?.GetComponent<MENaturalCombatComponent>();

        private static MENaturalSchedule Slot(Pawn pawn)
        {
            MENaturalCombatComponent data = Data;
            return pawn != null && data != null && data.byPawn.TryGetValue(pawn, out var state) ? state : null;
        }

        private static bool Unarmed(Pawn pawn)
            => MECombat.IsQualified(pawn) && pawn.equipment?.Primary == null
                && RimKataWeaponSlotUtility.SecondaryWeapon(pawn) == null;

        private static bool Live(Thing target, Pawn pawn)
            => target?.Spawned == true && !target.Destroyed && target.Map == pawn?.Map
                && (!(target is Pawn victim) || !victim.Dead && !victim.Downed);

        private static bool NativeFlight(Pawn pawn)
            => pawn?.Map?.GetComponent<RimKataMapComponent>()?.GetState(pawn, false)?.flyingKick != null;

        private static bool Engaged(MENaturalSchedule slot)
        {
            Pawn pawn = slot?.pawn;
            if (!Unarmed(pawn) || MEMovement.Blocks(pawn) || NativeFlight(pawn)) return false;
            Job job = pawn.CurJob;
            if (job == null || job.def != JobDefOf.AttackMelee && job.def != RimKataDefOf.RimKata_Attack
                && job.def != JobDefOf.Wait_Combat) return false;
            Thing target = slot.executing?.target ?? slot.next?.target;
            if (!Live(target, pawn) && slot.tails.Count > 0) target = slot.tails[slot.tails.Count - 1].target;
            return Live(target, pawn) && pawn.CanReachImmediate(target, PathEndMode.Touch);
        }

        private static Verb Choose(Pawn pawn, Thing target, bool first, out bool hasSpecial)
        {
            hasSpecial = false;
            var verbs = new List<Verb>();
            foreach (VerbEntry entry in pawn.meleeVerbs.GetUpdatedAvailableVerbsList(false))
            {
                Verb verb = entry.verb;
                if (verb?.EquipmentSource != null || !(verb is Verb_MeleeAttackDamage)
                    || MEAttackTypes.IsCombatExtended(verb) || !verb.IsStillUsableBy(pawn)
                    || !verb.Available() || target != null && !verb.IsUsableOn(target)) continue;
                MEAttackKind kind = MEAttackTypes.Kind(verb);
                hasSpecial |= kind != MEAttackKind.Simple;
                if (!first || kind != MEAttackKind.Blunt) verbs.Add(verb);
            }
            return Select(pawn, verbs, target, true, target != null);
        }

        private static MEAttackPlan MakePlan(MENaturalSchedule slot, Verb verb, Thing target, bool first, bool reduced)
        {
            int ticks = Math.Max(1, RimKataCombatMath.CooldownTicksForSingleShot(verb, slot.pawn, reduced));
            var plan = new MEAttackPlan {
                pawn = slot.pawn, target = target, verb = verb, kind = MEAttackTypes.Kind(verb),
                firstAttack = first, cooldown = ticks, segmentTicks = ticks / 8,
                strikeTick = first ? Now : Now + ticks, turnSign = Rand.Bool ? 1 : -1
            };
            plan.SetRoles();
            return plan;
        }

        internal static bool PrepareAttempt(Pawn pawn, Thing target, ref Verb verb, out MENaturalSchedule active)
        {
            active = null;
            if (MEStrike.Active || !Unarmed(pawn) || !(target is Pawn)
                || verb?.EquipmentSource != null || MEAttackTypes.IsCombatExtended(verb)
                || NativeFlight(pawn)) return true;
            if (MEMovement.Blocks(pawn)) return false;
            MENaturalSchedule slot = Slot(pawn);
            if (slot == null)
            {
                if (Data == null || pawn.stances.FullBodyBusy || !pawn.CanReachImmediate(target, PathEndMode.Touch)) return true;
                Verb chosen = Choose(pawn, target, true, out _);
                if (chosen == null) return false;
                slot = new MENaturalSchedule { pawn = pawn };
                slot.next = MakePlan(slot, chosen, target, true, false);
                Data.schedules.Add(slot);
                Data.byPawn[pawn] = slot;
            }
            Restore(slot.next);
            if (slot.next != null && Now < slot.next.strikeTick) return false;
            if (pawn.stances.FullBodyBusy || !pawn.CanReachImmediate(target, PathEndMode.Touch)) return true;
            if (slot.next?.verb == null || !slot.next.verb.IsStillUsableBy(pawn) || !slot.next.verb.Available())
            {
                Verb chosen = Choose(pawn, target, !slot.hasAttacked, out _);
                if (chosen == null) return true;
                slot.next = MakePlan(slot, chosen, target, !slot.hasAttacked, false);
                if (slot.hasAttacked) { ApplyCooldown(slot); return false; }
            }
            if (!slot.next.locked) slot.next.target = target;
            slot.next.strikeTick = Now;
            slot.executing = slot.next;
            verb = slot.next.verb;
            active = slot;
            return true;
        }

        internal static void FinishAttempt(MENaturalSchedule slot)
        {
            // Failed reach/stance checks have not consumed the reserved action.
            MEAttackPlan plan = slot?.executing;
            if (plan == null || plan.mainConsumed) return;
            if (plan.verb?.WarmingUp == true)
            {
                plan.strikeTick = Now + plan.verb.WarmupTicksLeft;
                return;
            }
            slot.executing = null;
        }

        internal static bool BeginShot(Verb verb, out ShotScope scope)
        {
            scope = null;
            if (MEStrike.Active || !Unarmed(verb?.CasterPawn) || verb.EquipmentSource != null) return false;
            MENaturalSchedule found = Slot(verb.CasterPawn);
            MEAttackPlan plan = found?.executing;
            if (plan == null || plan.verb != verb || plan.mainConsumed) return false;
            scope = new ShotScope { slot = found, verb = verb, originalTarget = CurrentTarget(verb),
                originalFacing = plan.pawn.Rotation };
            plan.strikeTick = Now;
            plan.mainConsumed = true;
            Lock(plan);
            if (plan.cancelled || found.resultPending) return true;
            if (plan.kind == MEAttackKind.Cut)
            {
                if (!MEStrike.TryTarget(plan, plan.mainCell, out Pawn victim)) return true;
                // Execute the outermost native verb once. Re-entering an override would duplicate
                // extra effects such as venom even when its base melee damage ran only once.
                CurrentTarget(verb) = victim;
                scope.redirected = true;
            }
            return plan.kind == MEAttackKind.Blunt;
        }

        internal static void EndShot(ShotScope scope)
        {
            if (scope?.redirected != true) return;
            CurrentTarget(scope.verb) = scope.originalTarget;
            Pawn pawn = scope.slot.pawn;
            if (pawn?.Spawned == true && !pawn.Dead && !pawn.Downed) pawn.Rotation = scope.originalFacing;
        }

        internal static void FinishShot(MENaturalSchedule slot)
        {
            MEAttackPlan plan = slot?.executing;
            if (plan == null || !plan.mainConsumed) return;
            slot.executing = null;
            slot.next = null;
            slot.hasAttacked = true;
            if (!plan.cancelled && !slot.resultPending) slot.tails.Add(plan);
            bool reduced = slot.resultPending && slot.reduced;
            slot.resultPending = slot.reduced = false;
            Reserve(slot, plan.target, reduced);
            slot.completedVerb = plan.verb;
            slot.completionTick = Now;
            slot.completionCooldown = Math.Max(1, (slot.next?.strikeTick ?? Now + plan.cooldown) - Now);
            // Native WarmupComplete may assign the old verb's cooldown after TryCastShot returns.
            // The SetStance hook below makes that assignment use the already chosen next action.
            ApplyCooldown(slot);
            if (!plan.cancelled && plan.segmentTicks == 0)
            {
                // Complete and reserve first: an immediate follow-through may itself consume this slot.
                if (Unarmed(slot.pawn) && !MEMovement.Blocks(slot.pawn)) Advance(plan);
                slot.tails.Remove(plan);
            }
        }

        internal static void AbortShot(MENaturalSchedule slot)
        {
            if (slot?.executing == null) return;
            slot.executing.cancelled = true;
            slot.executing = null;
            slot.resultPending = slot.reduced = false;
        }

        private static void Reserve(MENaturalSchedule slot, Thing target, bool reduced)
        {
            Thing current = slot.pawn.CurJob?.targetA.Thing;
            if (Live(current, slot.pawn)) target = current;
            Verb verb = Choose(slot.pawn, target, false, out _);
            slot.next = verb == null ? null : MakePlan(slot, verb, target, false, reduced);
        }

        private static void ApplyCooldown(MENaturalSchedule slot)
        {
            Pawn pawn = slot.pawn;
            if (pawn?.Spawned != true || pawn.Dead || pawn.Downed || pawn.stances == null) return;
            int ticks = Math.Max(1, (slot.next?.strikeTick ?? Now + slot.completionCooldown) - Now);
            pawn.stances.SetStance(new Stance_Cooldown(ticks, slot.next?.target, slot.next?.verb));
        }

        internal static void RewriteCooldown(Pawn pawn, ref Stance newStance)
        {
            MENaturalSchedule slot = Slot(pawn);
            if (slot?.completionTick != Now || !(newStance is Stance_Cooldown cooldown)
                || cooldown.verb != slot.completedVerb || slot.completionCooldown <= 0) return;
            // A same-tick response after the hit may have already shortened the next reservation.
            int ticks = slot.next != null ? Math.Max(1, slot.next.strikeTick - Now) : slot.completionCooldown;
            newStance = new Stance_Cooldown(ticks, cooldown.focusTarg, cooldown.verb);
        }

        internal static void Consume(Pawn pawn, bool reduced)
        {
            MENaturalSchedule slot = Slot(pawn);
            if (slot == null || slot.resultPending || !Unarmed(pawn)) return;
            Thing target = slot.executing?.target ?? slot.next?.target;
            foreach (MEAttackPlan tail in slot.tails) tail.cancelled = true;
            slot.tails.Clear();
            if (slot.next != null) slot.next.cancelled = true;
            if (slot.executing != null)
            {
                slot.executing.cancelled = true;
                slot.resultPending = true;
                slot.reduced = reduced;
                return;
            }
            slot.hasAttacked = true;
            Reserve(slot, target, reduced);
            ApplyCooldown(slot);
        }

        internal static bool HasCombatPlan(Pawn pawn)
        {
            MENaturalSchedule slot = Slot(pawn);
            return Engaged(slot) && (slot.next != null || slot.tails.Count > 0);
        }

        private static MEAttackPlan PhasePlan(MENaturalSchedule slot)
        {
            if (!Engaged(slot)) return null;
            MEAttackPlan selected = null;
            foreach (MEAttackPlan tail in slot.tails)
                if (HasPhase(tail) && (selected == null || tail.strikeTick > selected.strikeTick)) selected = tail;
            MEAttackPlan next = slot.executing ?? slot.next;
            if (HasPhase(next)
                && (selected == null || next.strikeTick > selected.strikeTick)) selected = next;
            return selected;
        }

        private static bool HasPhase(MEAttackPlan plan)
            => plan != null && !plan.cancelled && (plan.RoleAt(Now) != MERole.Normal
                || plan.kind == MEAttackKind.Simple && Now >= plan.ActiveStart && Now < plan.PostStart);

        internal static MEAttackPlan FindAttackPlan(Pawn pawn)
        {
            if (MEStrike.CurrentPlan?.pawn == pawn && MEStrike.CurrentPlan.weapon == null) return MEStrike.CurrentPlan;
            MENaturalSchedule slot = Slot(pawn);
            return slot?.executing ?? PhasePlan(slot) ?? slot?.next;
        }

        internal static MERole GetRole(Pawn pawn, Pawn incomingAttacker, out bool attacking)
        {
            attacking = false;
            MEAttackPlan plan = PhasePlan(Slot(pawn));
            if (plan == null) return MERole.Normal;
            MERole role = plan.RoleAt(Now);
            if (plan.kind == MEAttackKind.Simple && Now >= plan.ActiveStart && Now < plan.PostStart)
            {
                attacking = true;
                return MERole.Normal;
            }
            bool attack = role == MERole.Cut || role == MERole.Thrust || role == MERole.Blunt || role == MERole.Handle;
            if (!attack) return role;
            attacking = true;
            if (role != MERole.Cut) return role;
            IntVec3 cell = plan.CellAt(Now);
            attacking = incomingAttacker?.Spawned == true && incomingAttacker.Map == pawn.Map && incomingAttacker.Position == cell;
            return attacking ? role : MERole.NoResponse;
        }

        internal static string ReservationLabel(Pawn pawn)
        {
            MEAttackPlan plan = Slot(pawn)?.next;
            return plan == null || plan.cancelled ? null : MEAttackTypes.Label(plan.kind);
        }

        internal static Verb GetReservedVerb(Pawn pawn)
        {
            MEAttackPlan plan = Slot(pawn)?.next;
            Restore(plan);
            return plan?.cancelled == false ? plan.verb : null;
        }

        private static void Lock(MEAttackPlan plan)
        {
            if (plan.locked || plan.pawn?.Spawned != true) return;
            plan.locked = true;
            plan.origin = plan.pawn.Position;
            IntVec3 difference = Live(plan.target, plan.pawn) ? plan.target.Position - plan.origin : plan.pawn.Rotation.FacingCell;
            int direction = Mathf.RoundToInt(Mathf.Repeat(difference.AngleFlat, 360f) / 45f) & 7;
            plan.mainCell = plan.origin + Directions[direction];
            plan.preCell = plan.origin + Directions[(direction - plan.turnSign + 8) & 7];
            plan.postCell = plan.origin + Directions[(direction + plan.turnSign + 8) & 7];
        }

        private static void Restore(MEAttackPlan plan)
        {
            if (plan?.pawn?.meleeVerbs == null || plan.verb != null) return;
            foreach (VerbEntry entry in plan.pawn.meleeVerbs.GetUpdatedAvailableVerbsList(false))
                if (entry.verb.EquipmentSource == null && entry.verb.GetUniqueLoadID() == plan.verbId)
                { plan.verb = entry.verb; return; }
        }

        private static void Advance(MEAttackPlan plan)
        {
            Restore(plan);
            if (plan.cancelled || plan.verb == null) return;
            if (Now >= plan.PreStart) Lock(plan);
            if (plan.kind == MEAttackKind.Cut && !plan.firstAttack && !plan.preHitDone && Now >= plan.PreStart)
            {
                plan.preHitDone = true;
                if (Now < plan.ActiveStart) MEStrike.Execute(plan, plan.preCell);
            }
            if (plan.mainConsumed && plan.kind == MEAttackKind.Blunt && !plan.postHitDone && Now >= plan.PostStart)
            {
                plan.postHitDone = true;
                if (Now < plan.EndTick || plan.segmentTicks == 0)
                    MEStrike.Execute(plan, Live(plan.target, plan.pawn) ? plan.target.Position : plan.mainCell);
            }
        }

        internal static void Tick(MENaturalCombatComponent data)
        {
            for (int i = data.schedules.Count - 1; i >= 0; i--)
            {
                MENaturalSchedule slot = data.schedules[i];
                if (slot?.pawn == null || slot.pawn.Dead || slot.pawn.Spawned && !Unarmed(slot.pawn))
                {
                    if (slot?.pawn != null) data.byPawn.Remove(slot.pawn);
                    data.schedules.RemoveAt(i);
                    continue;
                }
                if (!slot.pawn.Spawned) continue;
                Restore(slot.next);
                if (slot.next != null && !slot.next.locked
                    && Live(slot.pawn.CurJob?.targetA.Thing, slot.pawn))
                    slot.next.target = slot.pawn.CurJob.targetA.Thing;
                if (slot.executing == null && slot.next?.verb?.WarmingUp == true)
                    slot.executing = slot.next;
                bool engaged = Engaged(slot);
                for (int j = slot.tails.Count - 1; j >= 0; j--)
                {
                    if (j >= slot.tails.Count) continue;
                    MEAttackPlan tail = slot.tails[j];
                    if (engaged) Advance(tail);
                    // A hit can synchronously resolve a parry which clears this slot's tails.
                    if ((!engaged || tail.cancelled || Now >= tail.EndTick)
                        && j < slot.tails.Count && slot.tails[j] == tail) slot.tails.RemoveAt(j);
                }
                if (engaged && slot.next != null && !slot.next.cancelled) Advance(slot.next);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryMeleeAttack))]
    internal static class Patch_MENaturalAttempt
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Pawn ___pawn, Thing target, ref Verb verbToUse, ref bool __result,
            out MENaturalSchedule __state)
        {
            if (MENaturalCombat.PrepareAttempt(___pawn, target, ref verbToUse, out __state)) return true;
            __result = false;
            return false;
        }
        private static void Finalizer(MENaturalSchedule __state) => MENaturalCombat.FinishAttempt(__state);
    }

    [HarmonyPatch]
    internal static class Patch_MENaturalShot
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in GenTypes.AllTypes)
            {
                if (!typeof(Verb_MeleeAttack).IsAssignableFrom(type) || type.ContainsGenericParameters) continue;
                MethodInfo method = AccessTools.DeclaredMethod(type, "TryCastShot", Type.EmptyTypes);
                if (method != null && !method.IsAbstract && method.ReturnType == typeof(bool)) yield return method;
            }
        }
        [HarmonyPriority(Priority.First + 20)]
        private static bool Prefix(Verb_MeleeAttack __instance, ref bool __result, out MENaturalCombat.ShotScope __state)
        {
            if (!MENaturalCombat.BeginShot(__instance, out __state)) return true;
            __result = true;
            return false;
        }
        private static void Postfix(MENaturalCombat.ShotScope __state) => MENaturalCombat.FinishShot(__state?.slot);
        private static Exception Finalizer(Exception __exception, MENaturalCombat.ShotScope __state)
        {
            if (__exception != null) MENaturalCombat.AbortShot(__state?.slot);
            MENaturalCombat.EndShot(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.SetStance))]
    internal static class Patch_MENaturalCooldown
    {
        private static void Prefix(Pawn ___pawn, ref Stance newStance) => MENaturalCombat.RewriteCooldown(___pawn, ref newStance);
    }
}
