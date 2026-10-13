using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace KRWF.RimKataME
{
    internal static class MEAttackTypes
    {
        internal static bool Supported(Verb verb) => verb?.IsMeleeAttack == true && !IsCombatExtended(verb);

        internal static bool IsCombatExtended(Verb verb)
        {
            for (Type type = verb?.GetType(); type != null; type = type.BaseType)
                if (type.Namespace == "CombatExtended" || type.Namespace?.StartsWith("CombatExtended.", StringComparison.Ordinal) == true)
                    return true;
            return false;
        }

        private static readonly HashSet<string> Handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "handle", "handles", "shaft", "shafts", "base", "bases", "butt", "butts",
            "pommel", "pommels", "hilt", "hilts", "grip", "grips"
        };
        private static readonly HashSet<string> OrdinaryRangedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "handle", "handles", "grip", "grips", "stock", "stocks", "barrel", "barrels"
        };
        private static readonly HashSet<string> OrdinaryBodyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "left fist", "right fist", "fist", "fists", "teeth", "head"
        };

        internal static MEAttackKind Kind(Verb verb)
        {
            if (!Supported(verb)) return MEAttackKind.Simple;
            ThingWithComps weapon = verb.EquipmentSource;
            string part = (verb.tool?.untranslatedLabel ?? verb.tool?.label ?? string.Empty).Trim();
            // Native fists/teeth stay ordinary. Implants and additional race tools retain their damage classification.
            if (weapon == null && verb.HediffSource == null
                && (verb.tool == null || OrdinaryBodyTools.Contains(part))) return MEAttackKind.Simple;
            if (weapon?.def.IsMeleeWeapon == true && Handles.Contains(part)) return MEAttackKind.Handle;
            if (weapon?.def.IsRangedWeapon == true && IsOrdinaryRangedTool(weapon.def, part)) return MEAttackKind.Simple;

            DamageDef damage = verb.verbProps?.meleeDamageDef ?? verb.maneuver?.verb?.meleeDamageDef;
            if (damage == DamageDefOf.Cut) return MEAttackKind.Cut;
            if (damage == DamageDefOf.Stab) return MEAttackKind.Thrust;
            if (damage == DamageDefOf.Blunt) return MEAttackKind.Blunt;
            return MEAttackKind.Simple;
        }

        private static bool IsOrdinaryRangedTool(ThingDef definition, string part)
        {
            if (OrdinaryRangedTools.Contains(part)) return true;
            // Keep vanilla bow/pilum attacks ordinary without masking another weapon's bayonet.
            switch (definition.defName)
            {
                case "Pila":
                    return string.Equals(part, "shaft", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(part, "point", StringComparison.OrdinalIgnoreCase);
                case "Bow_Short":
                case "Bow_Recurve":
                case "Bow_Great":
                case "Flamebow":
                case "NerveSpiker":
                    return string.Equals(part, "limb", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        internal static string Label(Verb verb) => Label(Kind(verb));

        internal static string Label(MEAttackKind kind)
        {
            switch (kind)
            {
                case MEAttackKind.Cut: return "베기";
                case MEAttackKind.Thrust: return "찌르기";
                case MEAttackKind.Blunt: return "때리기";
                case MEAttackKind.Handle: return "손잡이";
                default: return "공격";
            }
        }
    }
}
