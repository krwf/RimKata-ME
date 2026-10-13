using System;
using KRWF.RimKata;
using UnityEngine;
using Verse;

namespace KRWF.RimKataME
{
    internal enum MEAttackKind { Simple, Cut, Thrust, Blunt, Handle }
    internal enum MERole { Normal, NoResponse, Dodge, Block, Cut, Thrust, Blunt, Handle }

    // One reserved action owns its entire wind-up, strike and follow-through.
    internal sealed class MEAttackPlan : IExposable
    {
        internal Pawn pawn;
        internal ThingWithComps weapon;
        internal Thing target;
        internal Verb verb;
        internal MEAttackKind kind;
        internal MERole preRole, activeRole, postRole;
        internal int cooldown, strikeTick, segmentTicks, turnSign;
        internal bool firstAttack, locked, preHitDone, mainConsumed, postHitDone, cancelled, backstepDone;
        internal IntVec3 origin = IntVec3.Invalid, mainCell = IntVec3.Invalid;
        internal IntVec3 preCell = IntVec3.Invalid, postCell = IntVec3.Invalid;
        internal string verbId;
        internal int PreStart => strikeTick - segmentTicks / 2 - segmentTicks;
        internal int ActiveStart => strikeTick - segmentTicks / 2;
        internal int PostStart => strikeTick + segmentTicks - segmentTicks / 2;
        internal int EndTick => PostStart + segmentTicks;

        internal MERole RoleAt(int tick)
        {
            if (cancelled || tick >= EndTick || tick < PreStart) return MERole.Normal;
            if (tick < ActiveStart) return firstAttack ? MERole.Normal : preRole;
            if (tick < PostStart) return activeRole;
            return postRole;
        }

        internal IntVec3 CellAt(int tick)
            => tick < ActiveStart ? preCell : tick < PostStart ? mainCell : postCell;

        internal void SetRoles()
        {
            preRole = postRole = MERole.Normal;
            activeRole = MERole.Normal;
            switch (kind)
            {
                case MEAttackKind.Cut: preRole = activeRole = MERole.Cut; postRole = MERole.Block; break;
                case MEAttackKind.Thrust: preRole = MERole.Dodge; activeRole = MERole.Thrust; postRole = MERole.NoResponse; break;
                case MEAttackKind.Blunt: preRole = MERole.Dodge; activeRole = MERole.NoResponse; postRole = MERole.Blunt; break;
                case MEAttackKind.Handle: preRole = MERole.Block; activeRole = MERole.Handle; break;
            }
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref weapon, "weapon");
            Scribe_References.Look(ref target, "target");
            if (Scribe.mode == LoadSaveMode.Saving && verb != null) verbId = verb.GetUniqueLoadID();
            Scribe_Values.Look(ref verbId, "verbId");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref preRole, "preRole");
            Scribe_Values.Look(ref activeRole, "activeRole");
            Scribe_Values.Look(ref postRole, "postRole");
            Scribe_Values.Look(ref cooldown, "cooldown");
            Scribe_Values.Look(ref strikeTick, "strikeTick");
            Scribe_Values.Look(ref segmentTicks, "segmentTicks");
            Scribe_Values.Look(ref turnSign, "turnSign");
            Scribe_Values.Look(ref firstAttack, "firstAttack");
            Scribe_Values.Look(ref locked, "locked");
            Scribe_Values.Look(ref preHitDone, "preHitDone");
            Scribe_Values.Look(ref mainConsumed, "mainConsumed");
            Scribe_Values.Look(ref postHitDone, "postHitDone");
            Scribe_Values.Look(ref cancelled, "cancelled");
            Scribe_Values.Look(ref backstepDone, "backstepDone");
            Scribe_Values.Look(ref origin, "origin", IntVec3.Invalid);
            Scribe_Values.Look(ref mainCell, "mainCell", IntVec3.Invalid);
            Scribe_Values.Look(ref preCell, "preCell", IntVec3.Invalid);
            Scribe_Values.Look(ref postCell, "postCell", IntVec3.Invalid);
        }
    }
}
