using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KRWF.RimKata;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKataME
{
    [HarmonyPatch]
    internal static class Patch_RimKataDebugMenu_ME
    {
        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(Patch_Pawn_RimKataDebugHUDGizmo), "OpenDebugMenu");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo menuConstructor = AccessTools.Constructor(
                typeof(FloatMenu), new[] { typeof(List<FloatMenuOption>) });
            MethodInfo appendOption = AccessTools.Method(typeof(Patch_RimKataDebugMenu_ME), nameof(AppendOption));
            bool inserted = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, menuConstructor))
                {
                    var call = new CodeInstruction(OpCodes.Call, appendOption);
                    call.labels.AddRange(instruction.labels);
                    instruction.labels.Clear();
                    call.blocks.AddRange(instruction.blocks);
                    instruction.blocks.Clear();
                    yield return call;
                    inserted = true;
                }
                yield return instruction;
            }
            if (!inserted)
                Log.Error("[RimKata ME] Could not add the ME option to the RimKata debugging menu.");
        }

        private static List<FloatMenuOption> AppendOption(List<FloatMenuOption> options)
        {
            options.Add(new FloatMenuOption(
                (RimKataMEDebug.Enabled ? "[x] " : "[ ] ") + "ME",
                () => RimKataMEDebug.SetEnabled(!RimKataMEDebug.Enabled)));
            return options;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_Pawn_ME_DebugGizmos
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            return RimKataMEDebug.Active ? AddGizmos(__result, __instance) : __result;
        }

        private static IEnumerable<Gizmo> AddGizmos(IEnumerable<Gizmo> original, Pawn pawn)
        {
            foreach (Gizmo gizmo in original)
                yield return gizmo;

            if (!RimKataMEDebug.Active || !RimKataMEDebug.IsEligible(pawn))
                yield break;

            yield return new Command_MEDirection(pawn);
            yield return new Command_MEAttack(pawn);
        }
    }

    internal sealed class Command_MEDirection : Command_Action
    {
        private static readonly string[] Directions = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        private static GUIStyle directionStyle;
        private readonly Pawn pawn;

        internal Command_MEDirection(Pawn pawn)
        {
            this.pawn = pawn;
            RimKataMEDebug.GetDirection(pawn);
            defaultLabel = pawn.LabelShortCap;
            defaultDesc = "ME 공격 방향을 선택합니다.";
            icon = BaseContent.ClearTex;
            groupable = false;
            action = OpenDirectionMenu;
        }

        public override bool GroupsWith(Gizmo other) => false;

        public override void DrawIcon(Rect rect, Material material, GizmoRenderParms parms)
        {
            if (directionStyle == null)
            {
                directionStyle = new GUIStyle(Text.CurFontStyle)
                {
                    fontSize = 28,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false
                };
                directionStyle.normal.textColor = Color.white;
            }

            var textRect = new Rect(rect.x, rect.y, rect.width, Mathf.Max(0f, rect.height - 18f));
            GUI.Label(textRect, Directions[RimKataMEDebug.GetDirection(pawn)], directionStyle);
        }

        private void OpenDirectionMenu()
        {
            if (!RimKataMEDebug.Active || !RimKataMEDebug.IsEligible(pawn))
                return;

            var options = new List<FloatMenuOption>(Directions.Length);
            for (int i = 0; i < Directions.Length; i++)
            {
                int direction = i;
                options.Add(new FloatMenuOption(Directions[i], () =>
                {
                    if (RimKataMEDebug.Active && RimKataMEDebug.IsEligible(pawn))
                        RimKataMEDebug.SetDirection(pawn, direction);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    internal sealed class Command_MEAttack : Command_Action
    {
        internal Command_MEAttack(Pawn pawn)
        {
            defaultLabel = MECombat.GetReservationLabel(pawn) + "\n" + pawn.LabelShortCap + "\nME";
            defaultDesc = "선택한 방향으로 허공 근접 공격을 실행합니다. 실제 대상에 피해를 주지 않습니다.";
            groupable = false;
            icon = TexCommand.Attack;
            if (PawnAttackGizmoUtility.GetMeleeAttackGizmo(pawn) is Command original)
            {
                icon = original.icon ?? TexCommand.Attack;
                iconAngle = original.iconAngle;
                iconProportions = original.iconProportions;
                iconTexCoords = original.iconTexCoords;
                iconDrawScale = original.iconDrawScale;
                iconOffset = original.iconOffset;
                defaultIconColor = original.defaultIconColor;
            }
            action = () => RimKataMEDebug.Attack(pawn);
            if (!RimKataMEDebug.CanAttack(pawn, out string reason))
                Disable(reason);
        }

        public override bool GroupsWith(Gizmo other) => false;
    }
}
