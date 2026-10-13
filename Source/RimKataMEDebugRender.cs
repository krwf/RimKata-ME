using System;
using HarmonyLib;
using KRWF.RimKata;
using UnityEngine;
using Verse;

namespace KRWF.RimKataME
{
    public static class RimKataMEDebugRender
    {
        private static readonly AccessTools.FieldRef<Pawn_DrawTracker, JitterHandler> Jitterer =
            AccessTools.FieldRefAccess<Pawn_DrawTracker, JitterHandler>("jitterer");
        private static readonly AccessTools.FieldRef<float> SecondaryReflectionAxis =
            AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(
                typeof(RimKataDualWeaponRenderUtility), "nativeSecondaryReflectionAxis"));
        private static readonly AccessTools.FieldRef<float> SecondaryCombatTilt =
            AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(
                typeof(RimKataDualWeaponRenderUtility), "nativeSecondaryCombatTilt"));
        private static readonly AccessTools.FieldRef<bool> SecondaryRangedMirroring =
            AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(
                typeof(RimKataDualWeaponRenderUtility), "mirroringRangedCombatWeapon"));

        [ThreadStatic] private static DrawScope current;

        internal struct DrawScope
        {
            internal Pawn pawn;
            internal ThingWithComps primary;
            internal ThingWithComps secondary;
            internal Vector3 root;
            internal PawnRenderFlags flags;
            internal float angle;
            internal float primaryAngle, secondaryAngle;
            internal bool primaryAimed, secondaryAimed;
            internal float distanceFactor;
            internal bool active;
        }

        internal struct SecondaryScope
        {
            internal bool active;
            internal float reflectionAxis;
            internal float combatTilt;
            internal bool rangedMirroring;
        }

        internal static void Push(Pawn pawn, Vector3 root, PawnRenderFlags flags, out DrawScope previous)
        {
            // Publish the restore token before any call which could throw.
            previous = current;
            // A nested portrait or unrelated pawn must not inherit the outer preview.
            current = default;
            if ((flags & PawnRenderFlags.Portrait) != 0 || pawn == null) return;

            RimKataEligibilityCache.TryGetRegisteredSecondaryWeapon(pawn, out ThingWithComps secondary);
            ThingWithComps primary = pawn.equipment?.Primary;
            bool primaryAimed = MECombat.TryGetWeaponAim(pawn, primary, out float primaryAngle);
            bool secondaryAimed = MECombat.TryGetWeaponAim(pawn, secondary, out float secondaryAngle);
            bool preview = RimKataMEDebug.TryGetAim(pawn, out float angle);
            if (!primaryAimed && preview && primary != null) { primaryAimed = true; primaryAngle = angle; }
            if (!secondaryAimed && preview && secondary != null) { secondaryAimed = true; secondaryAngle = angle; }
            if (!primaryAimed && !secondaryAimed) return;
            current = new DrawScope
            {
                pawn = pawn,
                primary = primary,
                secondary = secondary,
                root = root,
                flags = flags,
                angle = angle,
                primaryAngle = primaryAngle,
                secondaryAngle = secondaryAngle,
                primaryAimed = primaryAimed,
                secondaryAimed = secondaryAimed,
                distanceFactor = pawn.ageTracker?.CurLifeStage?.equipmentDrawDistanceFactor ?? 1f,
                active = true
            };
        }

        internal static void Pop(DrawScope previous) => current = previous;

        internal static bool CarriesOpenly(Pawn pawn)
            => current.active && current.pawn == pawn;

        private static bool Owns(Thing weapon)
            => current.active && weapon != null
                && (weapon == current.primary && current.primaryAimed
                    || weapon == current.secondary && current.secondaryAimed);

        private static float AimAngle(Thing weapon)
            => weapon == current.secondary ? current.secondaryAngle : current.primaryAngle;

        private static Vector3 AimLocation(Thing weapon, Vector3 root, float distanceFactor)
            => root + new Vector3(0f, 0f, 0.4f + weapon.def.equippedDistanceOffset)
                .RotatedBy(AimAngle(weapon)) * distanceFactor;

        internal static bool DrawCarriedWeapon(ThingWithComps weapon, Vector3 root, float distanceFactor)
        {
            if (!Owns(weapon)) return false;

            // Enter the ordinary aiming call once, preserving RimKata's paired-weapon renderer.
            PawnRenderUtility.DrawEquipmentAiming(
                weapon, AimLocation(weapon, root, distanceFactor), AimAngle(weapon));
            return true;
        }

        internal static void AdjustAim(Thing weapon, ref Vector3 drawLoc, ref float aimAngle)
        {
            if (!Owns(weapon)) return;

            float altitude = drawLoc.y;
            drawLoc = AimLocation(weapon, current.root, current.distanceFactor);
            drawLoc.y = altitude;
            aimAngle = AimAngle(weapon);
        }

        internal static void BeginSecondary(Thing weapon, out SecondaryScope previous)
        {
            previous = default;
            if (!Owns(weapon) || weapon != current.secondary) return;

            previous = new SecondaryScope
            {
                active = true,
                reflectionAxis = SecondaryReflectionAxis(),
                combatTilt = SecondaryCombatTilt(),
                rangedMirroring = SecondaryRangedMirroring()
            };
            // RimKata reflects the secondary mesh after the aiming prefixes run.
            // Use its aimed-weapon path so idle facing cannot change our direction
            // or halve the secondary's north/south distance on an east/west pawn.
            SecondaryReflectionAxis() = AimAngle(weapon);
            SecondaryCombatTilt() = 0f;
            SecondaryRangedMirroring() = true;
        }

        internal static void EndSecondary(SecondaryScope previous)
        {
            if (!previous.active) return;
            SecondaryReflectionAxis() = previous.reflectionAxis;
            SecondaryCombatTilt() = previous.combatTilt;
            SecondaryRangedMirroring() = previous.rangedMirroring;
        }

        public static void PlayAttack(Pawn pawn, float angle)
        {
            if (!RimKataMEDebug.TryGetAim(pawn, out _)) return;
            Pawn_DrawTracker drawer = pawn.Drawer;
            if (drawer != null)
                Jitterer(drawer)?.AddOffset(0.5f, angle);
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras))]
    internal static class Patch_PawnRenderUtility_RimKataMEDebugScope
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Pawn pawn, Vector3 drawPos, PawnRenderFlags flags,
            out RimKataMEDebugRender.DrawScope __state)
        {
            RimKataMEDebugRender.Push(pawn, drawPos, flags, out __state);
        }

        private static Exception Finalizer(Exception __exception, RimKataMEDebugRender.DrawScope __state)
        {
            RimKataMEDebugRender.Pop(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.CarryWeaponOpenly))]
    internal static class Patch_PawnRenderUtility_RimKataMEDebugCarry
    {
        private static void Postfix(Pawn pawn, ref bool __result)
        {
            if (RimKataMEDebugRender.CarriesOpenly(pawn)) __result = true;
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawCarriedWeapon))]
    internal static class Patch_PawnRenderUtility_RimKataMEDebugCarriedWeapon
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(ThingWithComps weapon, Vector3 drawPos, float equipmentDrawDistanceFactor)
            => !RimKataMEDebugRender.DrawCarriedWeapon(weapon, drawPos, equipmentDrawDistanceFactor);
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
    internal static class Patch_PawnRenderUtility_RimKataMEDebugAim
    {
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("krwf.rimkata")]
        private static void Prefix(Thing eq, ref Vector3 drawLoc, ref float aimAngle,
            out RimKataMEDebugRender.SecondaryScope __state)
        {
            __state = default;
            RimKataMEDebugRender.AdjustAim(eq, ref drawLoc, ref aimAngle);
            RimKataMEDebugRender.BeginSecondary(eq, out __state);
        }

        private static void Finalizer(RimKataMEDebugRender.SecondaryScope __state)
            => RimKataMEDebugRender.EndSecondary(__state);
    }
}
