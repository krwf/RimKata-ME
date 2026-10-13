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
    internal static class MEDefense
    {
        internal sealed class Frame
        {
            internal Pawn attacker, defender;
            internal Verb verb;
            internal ThingWithComps weapon;
            internal MEAttackKind incoming;
            internal MERole role;
            internal bool selected, attacking, managed, parryResolved, parried, allResolved, avoided;
        }

        internal struct Scope
        {
            internal Frame previous;
        }

        [ThreadStatic] private static Frame current;
        private static readonly Func<Pawn, RimKataSettings> Settings = AccessTools.MethodDelegate<Func<Pawn, RimKataSettings>>(
            AccessTools.Method(typeof(RimKataEligibility).Assembly.GetType("KRWF.RimKata.RimKataTargetAccess"), "SettingsFor"));
        private static readonly Func<Pawn, float> DodgeChance = AccessTools.MethodDelegate<Func<Pawn, float>>(
            AccessTools.Method(typeof(RimKataCombatMath), "CloseMeleeDodgeChanceVerified"));
        private static readonly Action<Pawn, Thing, bool> NotifyDodge = AccessTools.MethodDelegate<Action<Pawn, Thing, bool>>(
            AccessTools.Method(typeof(RimKataEligibility).Assembly.GetType("KRWF.RimKata.RimKataGroundPoseUtility"), "NotifyAvoidance"));
        private static readonly Action<Pawn, ThingWithComps, LocalTargetInfo> WeaponEffect =
            AccessTools.MethodDelegate<Action<Pawn, ThingWithComps, LocalTargetInfo>>(
                AccessTools.Method(typeof(RimKataDefenseUtility), "SpawnWeaponResponseEffect"));
        private delegate bool ReactiveDefense(Pawn defender, out bool parried);
        private static readonly Type ReactiveType = typeof(RimKataEligibility).Assembly.GetType("KRWF.RimKata.RimKataReactiveDefense");
        private static readonly ReactiveDefense Reactive = AccessTools.MethodDelegate<ReactiveDefense>(AccessTools.Method(ReactiveType, "TryDefense"));
        private static readonly Action<Pawn, bool> NotifyReactive = AccessTools.MethodDelegate<Action<Pawn, bool>>(
            AccessTools.Method(ReactiveType, "NotifyDefense"));

        internal static Scope Enter(Pawn attacker, Pawn defender, Verb verb)
        {
            var state = new Scope { previous = current };
            if (MEAttackTypes.IsCombatExtended(verb)
                || !MECombat.IsQualified(attacker) && !MECombat.IsQualified(defender))
            {
                current = null;
                return state;
            }
            // Overrides which call their base and defense helpers must share one selection.
            if (current != null && current.attacker == attacker && current.verb == verb)
            {
                if (current.defender == defender) return state;
                if (!current.selected && verb?.CurrentTarget.Pawn == defender)
                {
                    current.defender = defender;
                    return state;
                }
            }
            current = new Frame
            {
                attacker = attacker, defender = defender, verb = verb,
                incoming = MEStrike.Active && MEStrike.CurrentPlan?.verb == verb
                    ? MEStrike.CurrentPlan.kind : MEAttackTypes.Kind(verb)
            };
            return state;
        }

        internal static void Exit(Scope state) => current = state.previous;

        private static Frame Select(Pawn defender)
        {
            Frame frame = current;
            if (frame == null) return null;
            if (frame.defender != defender)
            {
                // A natural cut can replace CurrentTarget after the outer shot scope entered.
                // Bind the first real response query to that victim, before choosing a hand.
                if (frame.verb?.CurrentTarget.Pawn != defender) return null;
                if (!frame.selected) frame.defender = defender;
                else current = frame = new Frame
                {
                    attacker = frame.attacker, defender = defender, verb = frame.verb, incoming = frame.incoming
                };
            }
            if (frame.selected) return frame;
            frame.selected = true;
            frame.managed = MECombat.IsQualified(defender);
            if (!frame.managed) return frame;

            Pawn_EquipmentTracker equipment = defender.equipment;
            ThingWithComps primary = equipment?.Primary;
            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeapon(defender);
            if (primary?.Destroyed == true) primary = null;
            if (secondary == primary || secondary?.Destroyed == true
                || secondary != null && equipment?.AllEquipmentListForReading.Contains(secondary) != true)
                secondary = null;
            // Choose existing slots, not slots prefiltered by their role, verb or response chance.
            frame.weapon = primary != null && secondary != null ? (Rand.Bool ? primary : secondary) : primary ?? secondary;
            frame.role = MECombat.GetRole(defender, frame.weapon, frame.attacker, out frame.attacking);
            return frame;
        }

        internal static float FilterDodge(Pawn defender, float chance)
        {
            Frame frame = Select(defender);
            if (frame == null) return chance;
            if (frame.incoming == MEAttackKind.Thrust) return 0f;
            if (!frame.managed) return chance;
            return frame.role == MERole.Normal || frame.role == MERole.Dodge ? chance : 0f;
        }

        internal static void FilterReactive(Pawn defender, bool parried, ref bool avoided)
        {
            if (!avoided) return;
            Frame frame = Select(defender);
            if (frame == null) return;
            // Leave the base sliding/shake-off response intact in the normal phase.
            if (frame.managed && frame.role != MERole.Normal
                || frame.incoming == MEAttackKind.Thrust && !parried
                || frame.incoming == MEAttackKind.Blunt && parried)
                avoided = false;
        }

        internal static void ConsumeReactive(Pawn defender, bool parried)
        {
            Frame frame = Select(defender);
            if (frame == null || !frame.managed || !parried || frame.parryResolved
                || frame.role != MERole.Normal || frame.incoming == MEAttackKind.Blunt) return;
            frame.parryResolved = frame.parried = true;
            MECombat.ResolveOrdinaryParry(defender, frame.weapon, frame.attacker);
        }

        internal static bool Handles(Pawn defender)
            => MECombat.IsQualified(defender) || current?.incoming == MEAttackKind.Blunt;

        internal static bool ResolveParry(Pawn defender, Pawn attacker, Verb verb)
        {
            Frame frame = Select(defender);
            if (frame == null) return false;
            if (frame.parryResolved) return frame.parried;
            frame.parryResolved = true;
            if (frame.incoming == MEAttackKind.Blunt || !frame.managed
                || defender?.Map == null || attacker?.Map != defender.Map
                || !RimKataEligibility.CanUseMeleeResponse(defender)
                || Settings(defender)?.responseEnabled == false || ExcludedFriendly(defender, attacker))
                return false;
            if (frame.weapon != null && (frame.weapon.Destroyed
                || defender.equipment?.AllEquipmentListForReading.Contains(frame.weapon) != true))
                return false; // No retry with the other hand after selection.

            bool passiveBlock = frame.role == MERole.Block;
            if (!passiveBlock && frame.role != MERole.Normal && !frame.attacking) return false;
            if (frame.attacking && MECombat.IsQualified(attacker))
            {
                int attackerSkill = attacker.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0;
                int defenderSkill = defender.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0;
                int difference = attackerSkill - defenderSkill;
                bool attackerReduced = true, defenderReduced = true;
                if (difference != 0 && Rand.Chance(Mathf.Min(Mathf.Abs(difference) * 0.1f, 1f)))
                {
                    attackerReduced = difference > 0;
                    defenderReduced = difference < 0;
                }
                frame.parried = true; // Publish before engine callbacks; nested effects cannot resolve it twice.
                MECombat.ResolveParry(attacker, MECombat.AttackWeapon(verb), defender, frame.weapon, attackerReduced, defenderReduced);
            }
            else
            {
                if (!Rand.Chance(RimKataCombatMath.MeleeParryChance(defender, attacker))) return false;
                frame.parried = true;
                if (!passiveBlock)
                    MECombat.ResolveOrdinaryParry(defender, frame.weapon, attacker);
            }
            ShowParry(frame, passiveBlock);
            return true;
        }

        private static bool ExcludedFriendly(Pawn defender, Pawn attacker)
            => attacker != null && attacker.Faction == defender.Faction && attacker.Faction?.IsPlayer == true
                && attacker.IsFreeNonSlaveColonist && !attacker.InMentalState && !attacker.IsQuestLodger();

        private static void ShowParry(Frame frame, bool block)
        {
            if (frame.defender?.Map == null) return;
            if (frame.weapon != null) WeaponEffect(frame.defender, frame.weapon, new LocalTargetInfo(frame.attacker));
            else EffecterDefOf.Deflect_General.Spawn(frame.defender.Position, frame.defender.Map)?.Cleanup();
            if (!block && RimKataDefOf.RimKata_ParryBattleLog != null)
                Find.BattleLog?.Add(new BattleLogEntry_Event(frame.defender, RimKataDefOf.RimKata_ParryBattleLog, frame.attacker));
        }

        internal static bool ResolveAll(Pawn defender, Pawn attacker, Verb verb, bool reactive, out bool parried)
        {
            Frame frame = Select(defender);
            parried = false;
            if (frame == null) return false;
            if (frame.allResolved) { parried = frame.parried; return frame.avoided; }
            frame.allResolved = true;
            if (defender == null || attacker == null || !RimKataEligibility.CanUseDefense(defender)) return false;
            if (reactive && Reactive(defender, out parried))
            {
                ConsumeReactive(defender, parried);
                frame.parried = parried;
                frame.parryResolved = parried;
                frame.avoided = true;
                NotifyReactive(defender, parried);
                return true;
            }
            if (RimKataEligibility.CanRollMeleeDodge(defender)
                && Rand.Chance(FilterDodge(defender, DodgeChance(defender))))
            {
                frame.avoided = true;
                NotifyDodge(defender, attacker, true);
                return true;
            }
            parried = ResolveParry(defender, attacker, verb);
            frame.avoided = parried;
            return parried;
        }
    }

    [HarmonyPatch]
    internal static class Patch_ME_MeleeDefenseScope
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in GenTypes.AllTypes)
            {
                if (!typeof(Verb_MeleeAttack).IsAssignableFrom(type)) continue;
                MethodInfo method = AccessTools.DeclaredMethod(type, "TryCastShot", Type.EmptyTypes);
                if (method != null && !method.IsAbstract) yield return method;
            }
        }
        [HarmonyPriority(Priority.First + 600)]
        private static void Prefix(Verb_MeleeAttack __instance, out MEDefense.Scope __state)
            => __state = MEDefense.Enter(__instance.CasterPawn, __instance.CurrentTarget.Pawn, __instance);
        [HarmonyPriority(Priority.Last - 600)]
        private static Exception Finalizer(Exception __exception, MEDefense.Scope __state)
        {
            MEDefense.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Verb_MeleeAttack), "GetDodgeChance")]
    internal static class Patch_ME_MeleeDodge
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(LocalTargetInfo __0, ref float __result)
            => __result = MEDefense.FilterDodge(__0.Pawn, __result);
    }

    [HarmonyPatch]
    internal static class Patch_ME_MeleeParry
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(RimKataDefenseUtility), "TryResolveMeleeParry",
            new[] { typeof(Pawn), typeof(Pawn), typeof(Verb), typeof(bool) });
        private static bool Prefix(Pawn defender, Pawn attacker, Verb attackingVerb, ref bool __result, out MEDefense.Scope __state)
        {
            __state = MEDefense.Enter(attacker, defender, attackingVerb);
            if (MEAttackTypes.IsCombatExtended(attackingVerb) || !MEDefense.Handles(defender)) return true;
            __result = MEDefense.ResolveParry(defender, attacker, attackingVerb);
            return false;
        }
        private static Exception Finalizer(Exception __exception, MEDefense.Scope __state)
        {
            MEDefense.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class Patch_ME_ReactiveDefense
    {
        private static MethodBase TargetMethod() => AccessTools.Method(
            typeof(RimKataEligibility).Assembly.GetType("KRWF.RimKata.RimKataReactiveDefense"), "TryDefense");
        private static void Postfix(Pawn defender, bool parried, ref bool __result)
            => MEDefense.FilterReactive(defender, parried, ref __result);
    }

    [HarmonyPatch]
    internal static class Patch_ME_ReactiveDefenseSuccess
    {
        private static MethodBase TargetMethod() => AccessTools.Method(
            typeof(RimKataEligibility).Assembly.GetType("KRWF.RimKata.RimKataReactiveDefense"), "NotifyDefense");
        private static void Prefix(Pawn defender, bool parried) => MEDefense.ConsumeReactive(defender, parried);
    }

    [HarmonyPatch]
    internal static class Patch_ME_DirectDefense
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(RimKataDefenseUtility), "TryResolveDirectMeleeDefense");
        private static bool Prefix(Pawn defender, Pawn attacker, Verb attackingVerb, ref bool parried,
            ref bool __result, out MEDefense.Scope __state)
        {
            __state = MEDefense.Enter(attacker, defender, attackingVerb);
            if (MEAttackTypes.IsCombatExtended(attackingVerb) || !MECombat.IsQualified(defender)) return true;
            __result = MEDefense.ResolveAll(defender, attacker, attackingVerb, true, out parried);
            return false;
        }
        private static Exception Finalizer(Exception __exception, MEDefense.Scope __state)
        {
            MEDefense.Exit(__state);
            return __exception;
        }
    }

}
