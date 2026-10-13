using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KRWF.RimKata;
using Verse;

namespace KRWF.RimKataME
{
    [StaticConstructorOnStartup]
    public static class RimKataMEBootstrap
    {
        static RimKataMEBootstrap()
        {
            // The add-on patches the existing menu after the base mod has initialized.
            RuntimeHelpers.RunClassConstructor(typeof(RimKataBootstrap).TypeHandle);
            var harmony = new Harmony("krwf.rimkatameleeex");
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Error("[RimKata ME] Initialization failed.\n" + exception);
            }
        }
    }
}
