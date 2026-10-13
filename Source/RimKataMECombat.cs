using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KRWF.RimKata;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKataME
{
    // A completed action and the next reservation have independent lifetimes.
    internal sealed class MEWeaponSchedule : IExposable
    {
        internal Pawn pawn;
        internal ThingWithComps weapon;
        internal MEAttackPlan next;
        internal List<MEAttackPlan> tails = new List<MEAttackPlan>();
        internal bool hasAttacked;
        internal bool resultPending, reduced;
        internal MEAttackPlan executing;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref weapon, "weapon");
            Scribe_Deep.Look(ref next, "next");
            Scribe_Collections.Look(ref tails, "tails", LookMode.Deep);
            Scribe_Values.Look(ref hasAttacked, "hasAttacked");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                tails ??= new List<MEAttackPlan>();
                resultPending = false;
                executing = null;
            }
        }
    }

    public sealed class MECombatComponent : GameComponent
    {
        internal List<MEWeaponSchedule> schedules = new List<MEWeaponSchedule>();
        internal readonly Dictionary<ThingWithComps, MEWeaponSchedule> byWeapon =
            new Dictionary<ThingWithComps, MEWeaponSchedule>();
        public MECombatComponent(Game game) => MECombat.Attach(game, this);
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref schedules, "RimKataME_weaponSchedules", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                schedules ??= new List<MEWeaponSchedule>();
                byWeapon.Clear();
                foreach (MEWeaponSchedule slot in schedules)
                    if (slot?.pawn != null && slot.weapon != null)
                        byWeapon[slot.weapon] = slot;
            }
        }
        public override void GameComponentTick() => MECombat.Tick(this);
    }

    internal static class MECombat
    {
        internal sealed class Completion
        {
            internal MEWeaponSchedule slot;
            internal RimKataWeaponCycleState cycle;
            internal MEAttackPlan plan;
            internal Thing target;
            internal bool acted;
        }
        internal sealed class MainShotScope
        {
            internal Verb verb;
            internal Pawn pawn;
            internal LocalTargetInfo previousTarget;
            internal Rot4 previousFacing;
            internal bool redirected;
        }

        private static readonly AccessTools.FieldRef<Verb, LocalTargetInfo> CurrentTarget =
            AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentTarget");

        private static readonly Func<Pawn, bool> Qualified = AccessTools.MethodDelegate<Func<Pawn, bool>>(
            AccessTools.Method(typeof(RimKataEligibilityCache), "IsCachedQualifiedPawn"));
        private static readonly Func<Pawn, RimKataPawnCombatState, RimKataWeaponCycleState, Verb, bool, Verb>
            ResolveAction = AccessTools.MethodDelegate<Func<Pawn, RimKataPawnCombatState, RimKataWeaponCycleState, Verb, bool, Verb>>(
                AccessTools.Method(typeof(RimKataDualWeaponController), "ResolveCycleActionVerb"));
        private static readonly Func<Pawn, IReadOnlyList<Verb>, Thing, bool, bool, Verb> SelectMelee =
            AccessTools.MethodDelegate<Func<Pawn, IReadOnlyList<Verb>, Thing, bool, bool, Verb>>(
                AccessTools.Method(typeof(RimKataDualWeaponController), "SelectMeleeVerb"));
        private static readonly FieldInfo BoundVerb = AccessTools.Field(typeof(RimKataWeaponCycleState), "boundVerb");
        private static readonly FieldInfo CloseFireAction = AccessTools.Field(typeof(RimKataWeaponCycleState), "plannedCloseFireAction");
        private static readonly FieldInfo CloseFireMeleeVerbs = AccessTools.Field(typeof(RimKataWeaponCycleState), "closeFireMeleeVerbs");
        private static readonly FieldInfo RangedCooldown = AccessTools.Field(typeof(RimKataWeaponCycleState), "rangedCooldown");
        private static readonly FieldInfo NativeAttack = AccessTools.Field(typeof(RimKataWeaponCycleState), "nativeAttack");
        private static readonly MethodInfo ApplyResponse = AccessTools.Method(typeof(RimKataWeaponCycleState), "ApplyResponseCooldown");
        private static readonly Type NativeType = AccessTools.TypeByName("KRWF.RimKata.RimKataNativeAttack");
        private static readonly FieldInfo NativePawn = AccessTools.Field(NativeType, "pawn");
        private static readonly FieldInfo NativeCycle = AccessTools.Field(NativeType, "cycle");
        private static readonly FieldInfo NativeWeapon = AccessTools.Field(NativeType, "weapon");
        private static readonly FieldInfo NativeVerb = AccessTools.Field(NativeType, "verb");
        private static readonly FieldInfo NativeTarget = AccessTools.Field(NativeType, "firedTarget");
        private static readonly FieldInfo NativeFired = AccessTools.Field(NativeType, "<HasFired>k__BackingField");
        private static readonly PropertyInfo NativePending = AccessTools.Property(NativeType, "Pending");
        private static readonly PropertyInfo NativeExecuting = AccessTools.Property(NativeType, "Executing");
        private static readonly MethodInfo CompleteRequest = AccessTools.Method(NativeType, "CompleteRequest");
        private static readonly FieldInfo NativeKick = AccessTools.Field(NativeType, "kickAttack");
        private static readonly FieldInfo NativeReactive = AccessTools.Field(NativeType, "reactiveMotion");
        private static readonly FieldInfo NativeSubdue = AccessTools.Field(NativeType, "subdueState");
        private static readonly FieldInfo NativeHunt = AccessTools.Field(NativeType, "huntingSession");
        [ThreadStatic] private static bool selecting;
        private static Game ownerGame;
        private static MECombatComponent component;
        private static int Now => Find.TickManager?.TicksGame ?? 0;
        private static MECombatComponent Data => Current.Game == ownerGame ? component : null;
        internal static void Attach(Game game, MECombatComponent data) { ownerGame = game; component = data; }
        internal static bool IsAuxiliary => MEStrike.Active;

        internal static bool IsQualified(Pawn pawn)
            => pawn?.Spawned == true && !pawn.Dead && !pawn.Downed
                && pawn.RaceProps.Humanlike && Qualified(pawn);

        private static RimKataPawnCombatState State(Pawn pawn)
            => pawn?.Map?.GetComponent<RimKataMapComponent>()?.GetState(pawn, false);
        private static RimKataWeaponCycleState Cycle(Pawn pawn, ThingWithComps weapon)
        {
            RimKataPawnCombatState state = State(pawn);
            if (weapon == null || state == null) return null;
            if (state.primaryWeaponCycle?.weapon == weapon) return state.primaryWeaponCycle;
            return state.secondaryWeaponCycle?.weapon == weapon ? state.secondaryWeaponCycle : null;
        }
        private static MEWeaponSchedule Slot(Pawn pawn, ThingWithComps weapon, bool create = false)
        {
            MECombatComponent data = Data;
            if (pawn == null || weapon == null || data == null) return null;
            if (data.byWeapon.TryGetValue(weapon, out MEWeaponSchedule slot))
                return slot.pawn == pawn ? slot : null;
            if (!create) return null;
            slot = new MEWeaponSchedule { pawn = pawn, weapon = weapon };
            data.byWeapon.Add(weapon, slot);
            data.schedules.Add(slot);
            return slot;
        }
        private static bool Live(Thing target, Pawn pawn)
            => target != null && !target.Destroyed && target.Spawned && target.Map == pawn?.Map
                && (!(target is Pawn victim) || !victim.Dead);
        private static MEWeaponSchedule AttackSlot(Pawn pawn, ThingWithComps weapon)
        {
            if (weapon != null) return Slot(pawn, weapon);
            RimKataPawnCombatState state = State(pawn);
            MEWeaponSchedule primary = Slot(pawn, state?.primaryWeaponCycle?.weapon);
            MEWeaponSchedule secondary = Slot(pawn, state?.secondaryWeaponCycle?.weapon);
            if (HasBodyAction(primary)) return primary;
            return HasBodyAction(secondary) ? secondary : null;
        }
        private static bool HasBodyAction(MEWeaponSchedule slot)
        {
            if (slot == null) return false;
            if (slot.executing?.verb?.EquipmentSource == null && slot.executing?.verb != null)
            {
                RimKataWeaponCycleState cycle = Cycle(slot.pawn, slot.weapon);
                object request = cycle != null ? NativeAttack.GetValue(cycle) : null;
                if (request != null && (bool)NativeExecuting.GetValue(request)
                    && ReferenceEquals(NativeVerb.GetValue(request), slot.executing.verb)) return true;
            }
            foreach (MEAttackPlan tail in slot.tails)
                if (tail.verb != null && tail.verb.EquipmentSource == null && InPhase(tail, Now)) return true;
            return false;
        }
        internal static ThingWithComps AttackWeapon(Verb verb)
        {
            if (verb?.EquipmentSource != null) return verb.EquipmentSource;
            if (verb == null) return null;
            if (MEStrike.Active && ReferenceEquals(MEStrike.CurrentPlan?.verb, verb)) return MEStrike.CurrentPlan.weapon;
            RimKataPawnCombatState state = State(verb.CasterPawn);
            ThingWithComps weapon = ExecutingWeapon(state?.primaryWeaponCycle, verb);
            return weapon ?? ExecutingWeapon(state?.secondaryWeaponCycle, verb);
        }
        private static ThingWithComps ExecutingWeapon(RimKataWeaponCycleState cycle, Verb verb)
        {
            if (cycle == null) return null;
            object request = NativeAttack.GetValue(cycle);
            return request != null && (bool)NativeExecuting.GetValue(request)
                && ReferenceEquals(NativeVerb.GetValue(request), verb) ? cycle.weapon : null;
        }
        private static bool Engaged(MEWeaponSchedule slot)
        {
            if (!IsQualified(slot?.pawn) || MEMovement.Blocks(slot.pawn)) return false;
            RimKataPawnCombatState state = State(slot.pawn);
            if (state?.dualCloseCombatActive == true) return true;
            Thing target = slot.executing?.target ?? slot.next?.target;
            return Live(target, slot.pawn) && slot.next?.verb?.IsMeleeAttack == true
                && slot.pawn.Position.AdjacentTo8WayOrInside(target.Position);
        }

        internal static bool HasCombatPlan(Pawn pawn)
        {
            if (MENaturalCombat.HasCombatPlan(pawn)) return true;
            RimKataPawnCombatState state = State(pawn);
            if (state == null || !IsQualified(pawn)) return false;
            return HasLivePlan(Slot(pawn, state.primaryWeaponCycle?.weapon))
                || HasLivePlan(Slot(pawn, state.secondaryWeaponCycle?.weapon));
        }
        private static bool HasLivePlan(MEWeaponSchedule slot)
            => slot != null && Engaged(slot) && (slot.next?.verb?.IsMeleeAttack == true || slot.tails.Count > 0);

        private static Verb Choose(Pawn pawn, RimKataWeaponCycleState cycle, Thing target)
        {
            Verb bound = BoundVerb.GetValue(cycle) as Verb;
            if (bound == null) return null;
            Thing previous = cycle.plannedTarget;
            bool previousClose = cycle.plannedCloseAttack;
            bool previousContext = cycle.plannedCloseContext;
            bool wasSelecting = selecting;
            try
            {
                selecting = true;
                cycle.plannedTarget = target;
                bool close = State(pawn)?.dualCloseCombatActive == true;
                cycle.plannedCloseAttack = close && Live(target, pawn)
                    && pawn.Position.AdjacentTo8WayOrInside(target.Position);
                cycle.plannedCloseContext = close;
                return ResolveAction(pawn, State(pawn), cycle, bound, close);
            }
            finally
            {
                selecting = wasSelecting;
                cycle.plannedTarget = previous;
                cycle.plannedCloseAttack = previousClose;
                cycle.plannedCloseContext = previousContext;
            }
        }
        private static Verb WithoutFirstBlunt(Pawn pawn, ThingWithComps weapon, Thing target, Verb selected)
        {
            if (selected == null || MEAttackTypes.Kind(selected) != MEAttackKind.Blunt) return selected;
            List<Verb> all = weapon.TryGetComp<CompEquippable>()?.AllVerbs;
            var allowed = new List<Verb>();
            if (all != null)
                for (int i = 0; i < all.Count; i++)
                    if (MEAttackTypes.Supported(all[i]) && MEAttackTypes.Kind(all[i]) != MEAttackKind.Blunt) allowed.Add(all[i]);
            Verb opening = SelectMelee(pawn, allowed, target, false, false);
            if (opening != null) return opening;
            // A blunt-only weapon can open with the pawn's ordinary natural attack.
            allowed.Clear();
            if (pawn.meleeVerbs != null)
                foreach (VerbEntry entry in pawn.meleeVerbs.GetUpdatedAvailableVerbsList(false))
                    if (entry.verb.EquipmentSource == null && entry.verb.HediffSource == null
                        && MEAttackTypes.Supported(entry.verb)
                        && MEAttackTypes.Kind(entry.verb) == MEAttackKind.Simple) allowed.Add(entry.verb);
            return SelectMelee(pawn, allowed, target, false, false);
        }
        private static MEAttackPlan MakePlan(MEWeaponSchedule slot, Verb verb, Thing target, bool first, bool reduced)
        {
            Verb cooldownVerb = verb;
            RimKataWeaponCycleState cycle = Cycle(slot.pawn, slot.weapon);
            if (cycle != null && (int)CloseFireAction.GetValue(cycle) >= 0
                && CloseFireMeleeVerbs.GetValue(cycle) is Verb[] closeVerbs)
            {
                int index = (int)CloseFireAction.GetValue(cycle);
                if (index < closeVerbs.Length && ReferenceEquals(closeVerbs[index], verb))
                    cooldownVerb = BoundVerb.GetValue(cycle) as Verb ?? verb;
            }
            int ticks = Math.Max(1, RimKataCombatMath.CooldownTicksForSingleShot(cooldownVerb, slot.pawn, reduced));
            var plan = new MEAttackPlan
            {
                pawn = slot.pawn, weapon = slot.weapon, target = target, verb = verb,
                kind = MEAttackTypes.Kind(verb), cooldown = ticks, segmentTicks = ticks / 8,
                strikeTick = first ? Now : Now + ticks, firstAttack = first,
                turnSign = Rand.Bool ? 1 : -1
            };
            plan.SetRoles();
            return plan;
        }

        internal static bool Reserved(Pawn pawn, RimKataWeaponCycleState cycle, out Verb verb)
        {
            verb = null;
            if (selecting || IsAuxiliary || !IsQualified(pawn) || cycle?.weapon == null) return false;
            if (!(cycle.plannedTarget is Pawn))
            {
                if (cycle.plannedTarget != null) LeavePawnCombat(pawn, cycle);
                return false;
            }
            MEWeaponSchedule slot = Slot(pawn, cycle.weapon);
            MEAttackPlan plan = slot?.next;
            if (plan == null || plan.cancelled) return false;
            RestoreVerb(plan);
            if (plan.verb == null) return false;
            if (!plan.locked && cycle.plannedTarget != null) plan.target = cycle.plannedTarget;
            verb = plan.verb;
            return true;
        }
        internal static void Selected(Pawn pawn, RimKataWeaponCycleState cycle, ref Verb verb)
        {
            if (selecting || IsAuxiliary || !MEAttackTypes.Supported(verb) || !IsQualified(pawn) || cycle?.weapon == null) return;
            if (!(cycle.plannedTarget is Pawn))
            {
                if (cycle.plannedTarget != null) LeavePawnCombat(pawn, cycle);
                return;
            }
            MEWeaponSchedule slot = Slot(pawn, cycle.weapon, true);
            if (slot.next != null && !slot.next.cancelled) return;
            bool first = !slot.hasAttacked && cycle.cooldownTicksRemaining <= 0;
            if (first && MEAttackTypes.Kind(verb) == MEAttackKind.Blunt)
            {
                Verb opening = WithoutFirstBlunt(pawn, cycle.weapon, cycle.plannedTarget, verb);
                if (opening != null) verb = opening;
                else first = false;
            }
            slot.next = MakePlan(slot, verb, cycle.plannedTarget, first, false);
            if (!first)
            {
                // Existing time survives a new target/close-combat entry.
                int remaining = cycle.cooldownTicksRemaining > 0 ? cycle.cooldownTicksRemaining : slot.next.cooldown;
                slot.next.strikeTick = Now + remaining;
                cycle.cooldownTicksRemaining = remaining;
            }
            if (first) Lock(slot.next);
        }

        internal static void Queued(object request, bool success)
        {
            if (!success || IsAuxiliary || Special(request)) return;
            Pawn pawn = NativePawn.GetValue(request) as Pawn;
            ThingWithComps weapon = NativeWeapon.GetValue(request) as ThingWithComps;
            if (!IsQualified(pawn)) return;
            RimKataWeaponCycleState cycle = NativeCycle.GetValue(request) as RimKataWeaponCycleState;
            if (cycle?.weapon != weapon) return;
            if (!(NativeTarget.GetValue(request) is Pawn))
            {
                LeavePawnCombat(pawn, cycle);
                return;
            }
            Verb verb = NativeVerb.GetValue(request) as Verb;
            if (verb == null) return;
            if (MEAttackTypes.IsCombatExtended(verb)) return;
            MEWeaponSchedule slot = Slot(pawn, weapon, verb.IsMeleeAttack);
            if (slot == null) return;
            if (!verb.IsMeleeAttack)
            {
                slot.next = null;
                slot.executing = null;
                return;
            }
            if (slot.next == null || slot.next.cancelled)
                slot.next = MakePlan(slot, verb, NativeTarget.GetValue(request) as Thing, !slot.hasAttacked, false);
            slot.executing = slot.next;
        }
        internal static void Began(object request)
        {
            if (IsAuxiliary) return;
            Pawn pawn = NativePawn.GetValue(request) as Pawn;
            MEWeaponSchedule slot = Slot(pawn, NativeWeapon.GetValue(request) as ThingWithComps);
            if (slot?.executing == null) return;
            if (Special(request))
            {
                // A queued ordinary request can be adopted by shake-off/subdue before execution.
                // Those controllers own its completion and cooldown from this point onward.
                CancelPlans(slot);
                slot.executing = null;
                return;
            }
            slot.executing.strikeTick = Now;
            Lock(slot.executing);
        }
        private static bool Special(object request)
            => request == null || NativeKick.GetValue(request) != null || NativeReactive.GetValue(request) != null
                || NativeSubdue.GetValue(request) != null || NativeHunt.GetValue(request) != null;

        internal static Completion BeforeComplete(object request, ref bool acted)
        {
            if (IsAuxiliary || Special(request)) return null;
            Pawn pawn = NativePawn.GetValue(request) as Pawn;
            ThingWithComps weapon = NativeWeapon.GetValue(request) as ThingWithComps;
            if (!(NativeTarget.GetValue(request) is Pawn))
            {
                LeavePawnCombat(pawn, NativeCycle.GetValue(request) as RimKataWeaponCycleState);
                return null;
            }
            MEWeaponSchedule slot = Slot(pawn, weapon);
            if (slot == null || !IsQualified(pawn)) return null;
            if (slot.executing == null && slot.next == null && !slot.resultPending) return null;
            if (!MEAttackTypes.Supported(NativeVerb.GetValue(request) as Verb)) return null;
            if (slot.resultPending) acted = true;
            return new Completion
            {
                slot = slot, cycle = NativeCycle.GetValue(request) as RimKataWeaponCycleState,
                plan = slot.executing ?? slot.next,
                target = NativeTarget.GetValue(request) as Thing, acted = acted
            };
        }
        internal static void AfterComplete(Completion completion)
        {
            if (completion?.cycle == null) return;
            MEWeaponSchedule slot = completion.slot;
            slot.executing = null;
            if (completion.cycle.weapon != slot.weapon) { CancelPlans(slot); return; }
            if (!completion.acted)
            {
                if (slot.next != null) slot.next.cancelled = true;
                slot.next = null;
                return;
            }
            bool outcome = slot.resultPending;
            bool reduced = outcome && slot.reduced;
            slot.resultPending = false;
            slot.reduced = false;
            MEAttackPlan completed = completion.plan;
            if (outcome) CancelPlans(slot);
            else if (completed != null && !completed.cancelled)
            {
                completed.mainConsumed = true;
                completed.strikeTick = Now;
                Lock(completed);
                slot.tails.Add(completed);
            }
            slot.hasAttacked = true;
            slot.next = null;
            ReserveNext(slot, completion.cycle, completion.target, reduced, outcome);
            if (!outcome && completed != null && completed.segmentTicks == 0)
            {
                // Commit the normal transition before a zero-length tail can re-enter parry.
                // Its response is then the last writer, exactly as for a later scheduled tail.
                if (!completed.cancelled && IsQualified(slot.pawn) && !MEMovement.Blocks(slot.pawn))
                    AdvancePlan(completed, Now);
                slot.tails.Remove(completed);
            }
        }

        private static void ReserveNext(MEWeaponSchedule slot, RimKataWeaponCycleState cycle,
            Thing fallback, bool reduced, bool response)
        {
            if (cycle.plannedTarget != null && !(cycle.plannedTarget is Pawn))
            {
                LeavePawnCombat(slot.pawn, cycle);
                return;
            }
            Thing target = Live(cycle.plannedTarget, slot.pawn) ? cycle.plannedTarget
                : Live(cycle.cachedCandidateTarget, slot.pawn) ? cycle.cachedCandidateTarget
                : Live(cycle.focusedTarget, slot.pawn) ? cycle.focusedTarget : fallback;
            if (!(target is Pawn))
            {
                LeavePawnCombat(slot.pawn, cycle);
                return;
            }
            Verb verb = Choose(slot.pawn, cycle, target);
            if (verb == null) verb = BoundVerb.GetValue(cycle) as Verb;
            if (verb == null || MEAttackTypes.IsCombatExtended(verb)) return;
            slot.next = MakePlan(slot, verb, target, false, reduced);
            if (response) ApplyResponse.Invoke(cycle, new object[] { slot.next.cooldown });
            else cycle.cooldownTicksRemaining = slot.next.cooldown;
            RangedCooldown.SetValue(cycle, !verb.IsMeleeAttack);
            cycle.plannedActionVerb = verb;
            if (Live(target, slot.pawn))
            {
                cycle.plannedTarget = target;
                cycle.cachedCandidateTarget = target;
                cycle.visualTarget = target;
                cycle.visualAimTicksRemaining = slot.next.cooldown;
            }
        }

        internal static void ResolveParry(Pawn attacker, ThingWithComps attackingWeapon,
            Pawn defender, ThingWithComps responseWeapon, bool attackerReduced, bool defenderReduced)
        {
            Consume(attacker, attackingWeapon, defender, attackerReduced);
            Consume(defender, responseWeapon, attacker, defenderReduced);
        }
        internal static void ResolveOrdinaryParry(Pawn defender, ThingWithComps responseWeapon, Pawn attacker)
            => Consume(defender, responseWeapon, attacker, true);
        internal static void FlyingKickStarted(Pawn pawn, ThingWithComps weapon)
            => Consume(pawn, weapon, null, false);
        private static void Consume(Pawn pawn, ThingWithComps weapon, Thing fallback, bool reduced)
        {
            if (!IsQualified(pawn)) return;
            if (weapon == null) weapon = AttackSlot(pawn, null)?.weapon;
            if (weapon == null) { MENaturalCombat.Consume(pawn, reduced); return; }
            RimKataWeaponCycleState cycle = Cycle(pawn, weapon);
            if (cycle == null) return;
            MEWeaponSchedule slot = Slot(pawn, weapon, true);
            if (slot.resultPending) return;
            slot.resultPending = true;
            slot.reduced = reduced;
            foreach (MEAttackPlan tail in slot.tails) tail.cancelled = true;
            slot.tails.Clear();
            if (slot.next != null) slot.next.cancelled = true;
            object request = NativeAttack.GetValue(cycle);
            if (request != null && (bool)NativePending.GetValue(request))
            {
                // Complete only after the native call unwinds when damage re-enters us.
                if ((bool)NativeExecuting.GetValue(request)) return;
                NativeFired.SetValue(request, true);
                CompleteRequest.Invoke(request, new object[] { true });
                return;
            }
            slot.resultPending = false;
            slot.reduced = false;
            cycle.ClearPlan();
            slot.next = null;
            slot.executing = null;
            slot.hasAttacked = true;
            ReserveNext(slot, cycle, fallback ?? cycle.lastFiredTarget, reduced, true);
        }

        internal static MEAttackPlan FindAttackPlan(Pawn pawn, ThingWithComps weapon)
        {
            if (MEStrike.Active && MEStrike.CurrentPlan?.pawn == pawn
                && (MEStrike.CurrentPlan.weapon == weapon || weapon == null && MEStrike.CurrentPlan.verb?.EquipmentSource == null))
                return MEStrike.CurrentPlan;
            MEWeaponSchedule slot = AttackSlot(pawn, weapon);
            return slot?.executing ?? PhasePlan(slot) ?? slot?.next
                ?? (weapon == null ? MENaturalCombat.FindAttackPlan(pawn) : null);
        }
        // Called by the explicit preview click; rendering never restores verbs or selects actions.
        internal static Verb GetReservedVerb(Pawn pawn, ThingWithComps weapon)
        {
            if (weapon == null) return MENaturalCombat.GetReservedVerb(pawn);
            MEAttackPlan plan = Slot(pawn, weapon)?.next;
            RestoreVerb(plan);
            return plan?.verb;
        }
        internal static bool BeginMainShot(Verb verb, out MainShotScope scope)
        {
            scope = null;
            if (IsAuxiliary || !MEAttackTypes.Supported(verb)) return false;
            ThingWithComps weapon = AttackWeapon(verb);
            if (!(verb.CurrentTarget.Thing is Pawn))
            {
                LeavePawnCombat(verb.CasterPawn, Cycle(verb.CasterPawn, weapon));
                return false;
            }
            MEWeaponSchedule slot = Slot(verb.CasterPawn, weapon);
            MEAttackPlan plan = slot?.executing;
            // Every declaring override is patched, but only the outermost invocation owns the scope.
            if (plan == null || plan.verb != verb || plan.mainConsumed) return false;
            scope = new MainShotScope
            {
                verb = verb, pawn = plan.pawn, previousTarget = CurrentTarget(verb),
                previousFacing = plan.pawn.Rotation
            };
            plan.mainConsumed = true;
            if (slot.resultPending || plan.cancelled) return true;
            Lock(plan);
            if (plan.kind == MEAttackKind.Cut)
            {
                if (!MEStrike.TryTarget(plan, plan.mainCell, out Pawn victim)) return true;
                // Preserve the existing override chain: re-invoking TryCastShot here repeats
                // subclass effects such as venom even if its base damage is only applied once.
                CurrentTarget(verb) = victim;
                scope.redirected = true;
            }
            return plan.kind == MEAttackKind.Blunt;
        }
        internal static void EndMainShot(MainShotScope scope)
        {
            if (scope?.redirected != true) return;
            CurrentTarget(scope.verb) = scope.previousTarget;
            if (scope.pawn?.Spawned == true && !scope.pawn.Dead && !scope.pawn.Downed)
                scope.pawn.Rotation = scope.previousFacing;
        }

        private static MEAttackPlan PhasePlan(MEWeaponSchedule slot)
        {
            if (slot == null || !Engaged(slot)) return null;
            Thing target = Cycle(slot.pawn, slot.weapon)?.plannedTarget;
            if (target != null && !(target is Pawn)) return null;
            int now = Now;
            MEAttackPlan selected = null;
            foreach (MEAttackPlan tail in slot.tails)
                if (InPhase(tail, now) && (selected == null || tail.strikeTick > selected.strikeTick)) selected = tail;
            MEAttackPlan next = slot.executing ?? slot.next;
            if (InPhase(next, now)
                && (selected == null || next.strikeTick > selected.strikeTick)) selected = next;
            return selected;
        }
        private static bool InPhase(MEAttackPlan plan, int tick)
            => plan != null && !plan.cancelled && plan.verb?.IsMeleeAttack == true
                && tick >= (plan.firstAttack ? plan.ActiveStart : plan.PreStart) && tick < plan.EndTick;
        internal static MERole GetRole(Pawn pawn, ThingWithComps weapon, Pawn incomingAttacker, out bool attacking)
        {
            attacking = false;
            MEWeaponSchedule slot = AttackSlot(pawn, weapon);
            if (slot == null && weapon == null) return MENaturalCombat.GetRole(pawn, incomingAttacker, out attacking);
            MEAttackPlan plan = PhasePlan(slot);
            if (plan == null) return MERole.Normal;
            MERole role = plan.RoleAt(Now);
            bool attackRole = role == MERole.Cut || role == MERole.Thrust || role == MERole.Blunt || role == MERole.Handle
                || (plan.kind == MEAttackKind.Simple && Now >= plan.ActiveStart && Now < plan.PostStart);
            if (attackRole)
            {
                IntVec3 cell = plan.kind == MEAttackKind.Cut ? plan.CellAt(Now)
                    : Live(plan.target, pawn) ? plan.target.Position : plan.mainCell;
                bool inCell = incomingAttacker?.Spawned == true && incomingAttacker.Map == pawn.Map && incomingAttacker.Position == cell;
                attacking = role != MERole.Cut || inCell;
                if (role == MERole.Cut && !inCell) return MERole.NoResponse;
            }
            return role;
        }

        internal static string GetReservationLabel(Pawn pawn)
        {
            RimKataPawnCombatState state = State(pawn);
            if (state == null) return MENaturalCombat.ReservationLabel(pawn) ?? "대기";
            string first = SlotLabel(Slot(pawn, state.primaryWeaponCycle?.weapon));
            string second = SlotLabel(Slot(pawn, state.secondaryWeaponCycle?.weapon));
            if (first == null && second == null) return MENaturalCombat.ReservationLabel(pawn) ?? "대기";
            return first == null ? second : second == null ? first : first + " / " + second;
        }
        private static string SlotLabel(MEWeaponSchedule slot)
        {
            MEAttackPlan plan = slot?.next;
            if (plan == null || plan.cancelled) return null;
            return MEAttackTypes.Label(plan.kind);
        }
        internal static bool TryGetWeaponAim(Pawn pawn, ThingWithComps weapon, out float angle)
        {
            angle = 0f;
            MEAttackPlan plan = PhasePlan(Slot(pawn, weapon));
            if (plan == null || !plan.locked) return false;
            IntVec3 cell = plan.kind == MEAttackKind.Cut ? plan.CellAt(Now)
                : Live(plan.target, pawn) ? plan.target.Position : plan.mainCell;
            if (!cell.IsValid || cell == pawn.Position) return false;
            angle = (cell.ToVector3Shifted() - pawn.DrawPos).AngleFlat();
            return true;
        }

        private static void Lock(MEAttackPlan plan)
        {
            if (plan == null || plan.locked || plan.pawn?.Spawned != true) return;
            plan.locked = true;
            plan.origin = plan.pawn.Position;
            Vector3 offset = plan.target != null ? (plan.target.Position - plan.origin).ToVector3() : plan.pawn.Rotation.FacingCell.ToVector3();
            float angle = offset.AngleFlat();
            int direction = ((int)Math.Round(angle / 45f) % 8 + 8) % 8;
            // GenAdj ordering is not an angle ordering: calculate the compass cells explicitly.
            plan.mainCell = plan.origin + Compass(direction);
            plan.preCell = plan.origin + Compass(direction - plan.turnSign);
            plan.postCell = plan.origin + Compass(direction + plan.turnSign);
        }
        private static IntVec3 Compass(int direction)
        {
            switch ((direction % 8 + 8) % 8)
            {
                case 0: return new IntVec3(0, 0, 1);
                case 1: return new IntVec3(1, 0, 1);
                case 2: return new IntVec3(1, 0, 0);
                case 3: return new IntVec3(1, 0, -1);
                case 4: return new IntVec3(0, 0, -1);
                case 5: return new IntVec3(-1, 0, -1);
                case 6: return new IntVec3(-1, 0, 0);
                default: return new IntVec3(-1, 0, 1);
            }
        }
        private static void RestoreVerb(MEAttackPlan plan)
        {
            if (plan == null || plan.verb != null || plan.weapon == null) return;
            List<Verb> verbs = plan.weapon.TryGetComp<CompEquippable>()?.AllVerbs;
            if (verbs != null)
                foreach (Verb verb in verbs)
                    if (verb.GetUniqueLoadID() == plan.verbId) { plan.verb = verb; return; }
            if (plan.pawn?.meleeVerbs != null)
                foreach (VerbEntry entry in plan.pawn.meleeVerbs.GetUpdatedAvailableVerbsList(false))
                    if (entry.verb.GetUniqueLoadID() == plan.verbId) { plan.verb = entry.verb; return; }
        }
        private static void CancelPlans(MEWeaponSchedule slot)
        {
            if (slot.next != null) slot.next.cancelled = true;
            foreach (MEAttackPlan tail in slot.tails) tail.cancelled = true;
            slot.next = null;
            slot.tails.Clear();
        }
        private static void LeavePawnCombat(Pawn pawn, RimKataWeaponCycleState cycle)
        {
            MEWeaponSchedule slot = Slot(pawn, cycle?.weapon);
            if (slot == null) return;
            // Release only our selected verb. The base controller keeps its target, native request,
            // warmup and remaining cooldown when a player switches to a structure.
            if (cycle.plannedActionVerb != null
                && (ReferenceEquals(cycle.plannedActionVerb, slot.next?.verb)
                    || ReferenceEquals(cycle.plannedActionVerb, slot.executing?.verb)))
            {
                cycle.plannedActionVerb = null;
                CloseFireAction.SetValue(cycle, -2);
            }
            CancelPlans(slot);
            slot.executing = null;
            slot.resultPending = slot.reduced = false;
            slot.hasAttacked = false;
        }
        internal static MEWeaponSchedule BeforeCancel(object request)
        {
            if (IsAuxiliary || Special(request) || !(bool)NativePending.GetValue(request)) return null;
            return Slot(NativePawn.GetValue(request) as Pawn, NativeWeapon.GetValue(request) as ThingWithComps);
        }
        internal static void AfterCancel(object request, MEWeaponSchedule slot)
        {
            if (slot == null || (bool)NativePending.GetValue(request)) return;
            if (slot.executing != null) slot.executing.locked = false;
            slot.executing = null;
        }
        internal static void Tick(MECombatComponent data)
        {
            // Work only on registered slots; no map-wide pawn search.
            for (int i = data.schedules.Count - 1; i >= 0; i--)
            {
                MEWeaponSchedule slot = data.schedules[i];
                RimKataWeaponCycleState cycle = Cycle(slot?.pawn, slot?.weapon);
                if (slot?.pawn == null || slot.weapon == null || slot.weapon.Destroyed || slot.pawn.Dead
                    || (slot.pawn.Spawned && (cycle == null || !IsQualified(slot.pawn))))
                {
                    if (slot?.weapon != null) data.byWeapon.Remove(slot.weapon);
                    data.schedules.RemoveAt(i);
                    continue;
                }
                if (cycle != null) Advance(slot, cycle);
            }
        }
        internal static bool BeforeCycle(Pawn pawn, RimKataWeaponCycleState cycle, Thing assignedTarget)
        {
            if (!IsQualified(pawn)) return true;
            if (assignedTarget != null && !(assignedTarget is Pawn))
            {
                LeavePawnCombat(pawn, cycle);
                return true;
            }
            MEWeaponSchedule slot = Slot(pawn, cycle?.weapon);
            if (slot != null) Advance(slot, cycle);
            return !MEMovement.Blocks(pawn);
        }
        private static void Advance(MEWeaponSchedule slot, RimKataWeaponCycleState cycle)
        {
            if (cycle.plannedTarget != null && !(cycle.plannedTarget is Pawn))
            {
                LeavePawnCombat(slot.pawn, cycle);
                return;
            }
            int now = Now;
            if (slot.next != null)
            {
                RestoreVerb(slot.next);
                if (slot.executing == null && cycle.cooldownTicksRemaining > 0)
                    slot.next.strikeTick = now + cycle.cooldownTicksRemaining;
                if (!slot.next.locked && cycle.plannedTarget != null) slot.next.target = cycle.plannedTarget;
            }
            bool engaged = Engaged(slot);
            for (int i = slot.tails.Count - 1; i >= 0; i--)
            {
                if (i >= slot.tails.Count) continue;
                MEAttackPlan tail = slot.tails[i];
                RestoreVerb(tail);
                if (!engaged || tail.cancelled) { slot.tails.RemoveAt(i); continue; }
                AdvancePlan(tail, now);
                if (now >= tail.EndTick && i < slot.tails.Count && ReferenceEquals(slot.tails[i], tail))
                    slot.tails.RemoveAt(i);
            }
            if (engaged && slot.next != null && !slot.next.cancelled) AdvancePlan(slot.next, now);
        }
        private static void AdvancePlan(MEAttackPlan plan, int now)
        {
            if (now >= plan.PreStart) Lock(plan);
            if (plan.verb == null) return;
            if (plan.kind == MEAttackKind.Cut && !plan.firstAttack && !plan.preHitDone && now >= plan.PreStart)
            {
                plan.preHitDone = true;
                if (now < plan.ActiveStart) MEStrike.Execute(plan, plan.preCell);
            }
            if (plan.mainConsumed && plan.kind == MEAttackKind.Blunt && !plan.postHitDone && now >= plan.PostStart)
            {
                plan.postHitDone = true;
                if (now < plan.EndTick || plan.segmentTicks == 0)
                    MEStrike.Execute(plan, Live(plan.target, plan.pawn) ? plan.target.Position : plan.mainCell);
            }
            if (plan.mainConsumed && plan.kind == MEAttackKind.Handle && !plan.backstepDone && now >= plan.EndTick)
            {
                plan.backstepDone = true;
                MEMovement.BeginBackstep(plan);
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_ME_ResolveAction
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(RimKataDualWeaponController), "ResolveCycleActionVerb");
        private static bool Prefix(Pawn __0, RimKataWeaponCycleState __2, ref Verb __result)
            => !MECombat.Reserved(__0, __2, out __result);
        private static void Postfix(Pawn __0, RimKataWeaponCycleState __2, ref Verb __result)
            => MECombat.Selected(__0, __2, ref __result);
    }
    [HarmonyPatch]
    internal static class Patch_ME_Queue
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("KRWF.RimKata.RimKataNativeAttack"), "Queue");
        private static void Postfix(object __instance, bool __result) => MECombat.Queued(__instance, __result);
    }
    [HarmonyPatch]
    internal static class Patch_ME_Begin
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("KRWF.RimKata.RimKataNativeAttack"), "BeginExecution");
        private static void Postfix(object __instance) => MECombat.Began(__instance);
    }
    [HarmonyPatch]
    internal static class Patch_ME_Cancel
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("KRWF.RimKata.RimKataNativeAttack"), "Cancel");
        private static void Prefix(object __instance, out MEWeaponSchedule __state) => __state = MECombat.BeforeCancel(__instance);
        private static void Postfix(object __instance, MEWeaponSchedule __state) => MECombat.AfterCancel(__instance, __state);
    }
    [HarmonyPatch]
    internal static class Patch_ME_Complete
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(RimKataDualWeaponController), "CompleteNativeAttack");
        private static void Prefix(object __0, ref bool __1, out MECombat.Completion __state)
            => __state = MECombat.BeforeComplete(__0, ref __1);
        private static void Postfix(MECombat.Completion __state) => MECombat.AfterComplete(__state);
    }
    [HarmonyPatch]
    internal static class Patch_ME_DriveCycle
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(RimKataDualWeaponController), "TickWeaponCycle");
        private static bool Prefix(Pawn __0, RimKataWeaponCycleState __2, Thing __3, ref Thing __8)
        {
            if (MECombat.BeforeCycle(__0, __2, __3)) return true;
            __8 = null;
            return false;
        }
    }
    [HarmonyPatch]
    internal static class Patch_ME_MainStrike
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
        [HarmonyPriority(Priority.First + 30)]
        private static bool Prefix(Verb_MeleeAttack __instance, ref bool __result, out MECombat.MainShotScope __state)
        {
            if (!MECombat.BeginMainShot(__instance, out __state)) return true;
            __result = false;
            return false;
        }
        private static Exception Finalizer(Exception __exception, MECombat.MainShotScope __state)
        {
            MECombat.EndMainShot(__state);
            return __exception;
        }
    }
}
