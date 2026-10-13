using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KRWF.RimKata;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKataME
{
    internal static class RimKataMEDebug
    {
        private sealed class PawnState
        {
            internal int direction;
            internal int nextAttackTick;
        }

        private static ConditionalWeakTable<Pawn, PawnState> states = new ConditionalWeakTable<Pawn, PawnState>();
        private static readonly Func<Pawn, bool> Qualified = AccessTools.MethodDelegate<Func<Pawn, bool>>(
            AccessTools.Method(typeof(RimKataEligibilityCache), "IsCachedQualifiedPawn"));
        private static readonly Func<Pawn, ThingWithComps, Thing, bool, Verb> SelectWeaponMelee =
            AccessTools.MethodDelegate<Func<Pawn, ThingWithComps, Thing, bool, Verb>>(
                AccessTools.Method(typeof(RimKataDualWeaponController), "ResolveWeaponMeleeVerb"));

        internal static bool Enabled { get; private set; }
        internal static bool Active => Prefs.DevMode && Enabled;

        internal static bool IsEligible(Pawn pawn)
            => pawn?.Spawned == true && !pawn.Dead && pawn.RaceProps.Humanlike && Qualified(pawn);

        internal static void SetEnabled(bool enabled)
        {
            Enabled = enabled && Prefs.DevMode && Current.Game != null;
            states = new ConditionalWeakTable<Pawn, PawnState>();
        }

        internal static int GetDirection(Pawn pawn)
        {
            if (!Active || !IsEligible(pawn)) return 0;
            PawnState state = states.GetOrCreateValue(pawn);
            UpdateFacing(pawn);
            return state.direction;
        }

        internal static void SetDirection(Pawn pawn, int direction)
        {
            if (!Active || !IsEligible(pawn) || direction < 0 || direction >= 8) return;
            states.GetOrCreateValue(pawn).direction = direction;
            // Selection is a main-thread event and must also take effect while paused.
            UpdateFacing(pawn);
        }

        internal static bool TryGetFacing(Pawn pawn, out Rot4 facing)
        {
            facing = Rot4.Invalid;
            if (!TryGetAim(pawn, out float angle)) return false;
            facing = Pawn_RotationTracker.RotFromAngleBiased(angle);
            return true;
        }

        // Only UI events and the rotation tracker call this; render hooks remain read-only.
        internal static bool UpdateFacing(Pawn pawn)
        {
            if (MECombat.HasCombatPlan(pawn) || MEMovement.Blocks(pawn)) return false;
            if (!TryGetFacing(pawn, out Rot4 facing)) return false;
            if (pawn.Rotation != facing) pawn.Rotation = facing;
            return true;
        }

        // Render paths only read published state; they never register or remove pawns.
        internal static bool TryGetAim(Pawn pawn, out float angle)
        {
            angle = 0f;
            if (!Active || pawn == null || !states.TryGetValue(pawn, out PawnState state)
                || MECombat.HasCombatPlan(pawn) || MEMovement.Blocks(pawn)
                || !IsEligible(pawn) || pawn.Downed || pawn.IsBurning()
                || pawn.GetPosture() != PawnPosture.Standing || pawn.carryTracker?.CarriedThing != null)
                return false;
            angle = state.direction * 45f;
            return true;
        }

        internal static bool CanAttack(Pawn pawn, out string reason)
        {
            reason = null;
            if (!Active || !IsEligible(pawn)) reason = "ME 디버그가 활성화된 림카타 폰이 필요합니다.";
            else if (pawn.Downed || pawn.IsBurning() || pawn.GetPosture() != PawnPosture.Standing
                || pawn.stances?.stunner?.Stunned == true) reason = "지금은 근접 공격을 할 수 없습니다.";
            else if (!pawn.kindDef.canMeleeAttack || pawn.WorkTagIsDisabled(WorkTags.Violent))
                reason = "근접 공격을 할 수 없는 폰입니다.";
            else if (pawn.carryTracker?.CarriedThing != null) reason = "운반 중에는 사용할 수 없습니다.";
            else if (pawn.stances?.FullBodyBusy == true) reason = "현재 동작이 끝난 뒤 사용할 수 있습니다.";
            else if (states.TryGetValue(pawn, out PawnState state) && state.nextAttackTick > Find.TickManager.TicksGame)
                reason = "ME 재사용 대기시간: " + (state.nextAttackTick - Find.TickManager.TicksGame) + "틱";
            return reason == null;
        }

        internal static void Attack(Pawn pawn)
        {
            if (!CanAttack(pawn, out string reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }

            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            // A pawn is supplied only to the selection-weight calculation; no verb is cast.
            Verb verb = MECombat.GetReservedVerb(pawn, primary);
            if (verb == null && primary != null) verb = SelectWeaponMelee(pawn, primary, pawn, true);
            if (verb == null) verb = pawn.meleeVerbs?.TryGetMeleeVerb(null);
            if (verb?.IsMeleeAttack != true)
            {
                Messages.Message("사용 가능한 근접 공격이 없습니다.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            PawnState state = states.GetOrCreateValue(pawn);
            int cooldown = Mathf.Max(1, RimKataCombatMath.CooldownTicksForSingleShot(verb, pawn, false));
            RimKataMEDebugRender.PlayAttack(pawn, state.direction * 45f);
            state.nextAttackTick = Find.TickManager.TicksGame + cooldown;
        }
    }

    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.UpdateRotation))]
    internal static class Patch_PawnRotationTracker_RimKataMEFacing
    {
        private static bool Prefix(Pawn ___pawn) => !RimKataMEDebug.UpdateFacing(___pawn);
    }

    // Preview controls deliberately do not survive a new game or load.
    public sealed class RimKataMEDebugComponent : GameComponent
    {
        public RimKataMEDebugComponent(Game game) => RimKataMEDebug.SetEnabled(false);
        public override void StartedNewGame() => RimKataMEDebug.SetEnabled(false);
        public override void LoadedGame() => RimKataMEDebug.SetEnabled(false);

        public override void GameComponentUpdate()
        {
            // Update also runs while paused, so turning off developer mode clears preview state immediately.
            if (RimKataMEDebug.Enabled && !Prefs.DevMode) RimKataMEDebug.SetEnabled(false);
        }
    }
}
