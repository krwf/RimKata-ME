using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

public static class MEWrapperValidation
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int failures;
    private sealed class Job
    {
        internal MethodBase original;
        internal readonly List<MethodInfo> prefix = new List<MethodInfo>(), postfix = new List<MethodInfo>(),
            transpiler = new List<MethodInfo>(), finalizer = new List<MethodInfo>();
        internal readonly List<string> owners = new List<string>();
        internal readonly Dictionary<MethodInfo, HarmonyMethod> metadata = new Dictionary<MethodInfo, HarmonyMethod>();
        internal readonly Dictionary<MethodInfo, string> patchOwners = new Dictionary<MethodInfo, string>();
    }

    public static int Run(string repo, string harmonyDirectory)
    {
        string managed = @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed";
        string[] directories = { managed, Path.Combine(repo, "RimKata", "Assemblies"), harmonyDirectory, Path.Combine(repo, "1.6", "Assemblies") };
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
        {
            string file = new AssemblyName(args.Name).Name + ".dll";
            foreach (string directory in directories)
            {
                string path = Path.Combine(directory, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try
        {
            Assembly.LoadFrom(Path.Combine(managed, "UnityEngine.CoreModule.dll"));
            Assembly.LoadFrom(Path.Combine(managed, "UnityEngine.IMGUIModule.dll"));
            Assembly gameAssembly = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
            Assembly baseAssembly = Assembly.LoadFrom(Path.Combine(repo, "RimKata", "Assemblies", "RimKata.dll"));
            Assembly addon = Assembly.LoadFrom(Path.Combine(repo, "1.6", "Assemblies", "RimKataME.dll"));
            // No game mod manager exists in this process. Supply the same type-discovery input explicitly.
            gameAssembly.GetType("Verse.GenTypes").GetField("allTypesCached", All).SetValue(null,
                LoadTypes(gameAssembly).Concat(LoadTypes(baseAssembly)).Concat(addon.GetTypes()).ToList());
            Console.WriteLine("DLL " + addon.Location);
            foreach (MethodInfo method in gameAssembly.GetType("RimWorld.Verb_MeleeAttackDamage").GetMethods(All).Where(m => m.Name == "TryCastShot"))
                Console.WriteLine("SHOT DECLARING TYPE " + method.DeclaringType.FullName + " " + method);
            Activate(addon);
            PrepareHotPaths(addon);
            Wrappers(addon, baseAssembly);
            Timing(addon);
        }
        catch (Exception exception) { Fail("HARNESS", exception); }
        Console.WriteLine("VALIDATION failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void Activate(Assembly addon)
    {
        string[] names = { "MEAttackTypes", "MECombat", "MEDefense", "MEStrike", "MEMovement", "MENaturalCombat", "RimKataMEDebug", "RimKataMEDebugRender" };
        foreach (string name in names)
        {
            try
            {
                Type type = addon.GetType("KRWF.RimKataME." + name, true);
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                foreach (FieldInfo field in type.GetFields(All).Where(f => f.IsStatic && typeof(MemberInfo).IsAssignableFrom(f.FieldType)))
                    if (field.GetValue(null) == null) throw new MissingMemberException(name + "." + field.Name);
                Console.WriteLine("CCTOR PASS " + name);
            }
            catch (Exception exception) { Fail("CCTOR " + name, exception); }
        }
    }

    private static void PrepareHotPaths(Assembly addon)
    {
        string[] names = { "MEAttackTypes", "MECombat", "MEDefense", "MEStrike", "MEMovement", "MENaturalCombat", "MEAttackPlan" };
        foreach (string name in names)
        {
            try
            {
                Type type = addon.GetType("KRWF.RimKataME." + name, true);
                int count = 0;
                foreach (MethodInfo method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters) continue;
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    count++;
                }
                Console.WriteLine("JIT PASS " + name + " methods=" + count);
            }
            catch (Exception exception) { Fail("JIT " + name, exception); }
        }
    }

    private static void Wrappers(Assembly addon, Assembly baseAssembly)
    {
        Assembly harmonyAssembly = typeof(Harmony).Assembly;
        Type processorType = typeof(PatchClassProcessor);
        MethodInfo bulk = processorType.GetMethod("GetBulkMethods", All);
        FieldInfo container = processorType.GetField("containerAttributes", All);
        MethodInfo original = harmonyAssembly.GetType("HarmonyLib.PatchTools").GetMethod("GetOriginalMethod", All);
        Dictionary<MethodBase, Job> jobs = new Dictionary<MethodBase, Job>();
        var harmony = new Harmony("krwf.rimkatame.validation.wrapperonly");
        int classes = 0;
        foreach (Type type in addon.GetTypes().Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0))
        {
            classes++;
            try
            {
                object processor = Activator.CreateInstance(processorType, new object[] { harmony, type });
                var targets = (List<MethodBase>)bulk.Invoke(processor, null);
                if (targets == null || targets.Count == 0)
                {
                    MethodBase target = (MethodBase)original.Invoke(null, new[] { container.GetValue(processor) });
                    if (target == null) throw new MissingMethodException("No original target");
                    targets = new List<MethodBase> { target };
                }
                foreach (MethodBase target in targets)
                {
                    Job job;
                    if (!jobs.TryGetValue(target, out job)) jobs[target] = job = new Job { original = target };
                    AddPatches(job, type, (HarmonyMethod)container.GetValue(processor), "krwf.rimkatameleeex");
                }
            }
            catch (Exception exception) { Fail("TARGET " + type.FullName, exception); }
        }
        // Resolve only attribute-addressed base patches on existing addon targets.
        // Do not run the base bootstrap, Prepare hooks or dynamic TargetMethods enumeration.
        int sharedBaseClasses = 0;
        var declaringTypes = new HashSet<Type>(jobs.Keys.Select(target => target.DeclaringType));
        foreach (Type type in LoadTypes(baseAssembly).Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0))
        {
            try
            {
                object processor = Activator.CreateInstance(processorType, new object[] { harmony, type });
                var metadata = (HarmonyMethod)container.GetValue(processor);
                if (metadata.declaringType == null || metadata.methodName == null
                    || !declaringTypes.Contains(metadata.declaringType)) continue;
                MethodBase target = (MethodBase)original.Invoke(null, new object[] { metadata });
                Job job;
                if (target == null || !jobs.TryGetValue(target, out job)) continue;
                AddPatches(job, type, metadata, "krwf.rimkata");
                sharedBaseClasses++;
            }
            catch (Exception exception) { Fail("BASE ATTRIBUTE TARGET " + type.FullName, exception); }
        }
        Type configType = harmonyAssembly.GetType("HarmonyLib.MethodCreatorConfig");
        Type creatorType = harmonyAssembly.GetType("HarmonyLib.MethodCreator");
        ConstructorInfo configConstructor = configType.GetConstructors(All).Single();
        ConstructorInfo creatorConstructor = creatorType.GetConstructors(All).Single();
        Type infixList = typeof(List<>).MakeGenericType(harmonyAssembly.GetType("HarmonyLib.Infix"));
        int passed = 0;
        foreach (Job job in jobs.Values)
        {
            try
            {
                object config = configConstructor.Invoke(new object[] { job.original, null, Sorted(job, job.prefix), Sorted(job, job.postfix),
                    Sorted(job, job.transpiler), Sorted(job, job.finalizer), Activator.CreateInstance(infixList), Activator.CreateInstance(infixList), false });
                object creator = creatorConstructor.Invoke(new[] { config });
                object replacement = creatorType.GetMethod("CreateReplacement", All).Invoke(creator, null);
                if (replacement == null) throw new InvalidOperationException("Missing replacement");
                passed++;
                Console.WriteLine("WRAPPER PASS " + job.original.DeclaringType.FullName + "." + job.original.Name + " <= " + string.Join(",", job.owners));
            }
            catch (Exception exception) { Fail("WRAPPER " + string.Join(",", job.owners) + " => " + job.original, exception); }
        }
        Console.WriteLine("WRAPPERS addonClasses=" + classes + " sharedBaseClasses=" + sharedBaseClasses
            + " targets=" + jobs.Count + " passed=" + passed);
    }

    private static void AddPatches(Job job, Type type, HarmonyMethod container, string owner)
    {
        job.owners.Add((owner == "krwf.rimkata" ? "BASE:" : "") + type.Name);
        foreach (MethodInfo method in type.GetMethods(All | BindingFlags.DeclaredOnly))
        {
            bool patch = false;
            if (method.Name == "Prefix" || method.IsDefined(typeof(HarmonyPrefix), false)) { job.prefix.Add(method); patch = true; }
            if (method.Name == "Postfix" || method.IsDefined(typeof(HarmonyPostfix), false)) { job.postfix.Add(method); patch = true; }
            if (method.Name == "Transpiler" || method.IsDefined(typeof(HarmonyTranspiler), false)) { job.transpiler.Add(method); patch = true; }
            if (method.Name == "Finalizer" || method.IsDefined(typeof(HarmonyFinalizer), false)) { job.finalizer.Add(method); patch = true; }
            if (!patch) continue;
            var metadata = HarmonyMethod.Merge(new List<HarmonyMethod> { container, new HarmonyMethod(method) });
            metadata.method = method;
            job.metadata[method] = metadata;
            job.patchOwners[method] = owner;
        }
    }

    private static List<MethodInfo> Sorted(Job job, List<MethodInfo> methods)
    {
        // RimKata registers before the dependent addon; Harmony then applies priorities and ordering attributes.
        var patches = methods.OrderBy(method => job.patchOwners[method] == "krwf.rimkata" ? 0 : 1)
            .Select((method, index) => new Patch(job.metadata[method], index, job.patchOwners[method])).ToArray();
        return PatchProcessor.GetSortedPatchMethods(job.original, patches);
    }

    private static void Timing(Assembly addon)
    {
        Type planType = addon.GetType("KRWF.RimKataME.MEAttackPlan", true);
        Type roleType = addon.GetType("KRWF.RimKataME.MERole", true);
        MethodInfo roleAt = planType.GetMethod("RoleAt", All);
        int cases = 0;
        foreach (int cooldown in new[] { 0, 2, 60, 84, 92, 96, 120, 132, 156, 168, 180 })
        {
            try
            {
                object plan = Activator.CreateInstance(planType, true);
                Set(planType, plan, "cooldown", cooldown);
                Set(planType, plan, "segmentTicks", cooldown / 8);
                Set(planType, plan, "strikeTick", 1000);
                Set(planType, plan, "preRole", Enum.Parse(roleType, "Dodge"));
                Set(planType, plan, "activeRole", Enum.Parse(roleType, "Thrust"));
                Set(planType, plan, "postRole", Enum.Parse(roleType, "Block"));
                int pre = Property(planType, plan, "PreStart"), active = Property(planType, plan, "ActiveStart"),
                    post = Property(planType, plan, "PostStart"), end = Property(planType, plan, "EndTick");
                if (!(pre <= active && active <= 1000 && 1000 <= post && post <= end)) throw new Exception("Reversed boundaries");
                var counts = new Dictionary<string, int> { { "Dodge", 0 }, { "Thrust", 0 }, { "Block", 0 } };
                for (int tick = 1000 - cooldown - 10; tick <= 1000 + cooldown + 10; tick++)
                {
                    string role = roleAt.Invoke(plan, new object[] { tick }).ToString();
                    if (counts.ContainsKey(role)) counts[role]++;
                    else if (role != "Normal") throw new Exception("Unknown phase role");
                    if ((tick < pre || tick >= end) && role != "Normal") throw new Exception("Role outside attack interval");
                }
                if (counts.Values.Any(count => count != cooldown / 8)) throw new Exception("Phase length differs from Q");
                Set(planType, plan, "firstAttack", true);
                for (int tick = pre - 1; tick < active; tick++)
                    if (roleAt.Invoke(plan, new object[] { tick }).ToString() != "Normal") throw new Exception("First attack has wind-up role");
                Set(planType, plan, "cancelled", true);
                for (int tick = pre - 1; tick <= end; tick++)
                    if (roleAt.Invoke(plan, new object[] { tick }).ToString() != "Normal") throw new Exception("Cancelled attack retains role");
                cases++;
                Console.WriteLine("TIMING PASS C=" + cooldown + " Q=" + cooldown / 8 + " boundaries=" + pre + "/" + active + "/1000/" + post + "/" + end);
            }
            catch (Exception exception) { Fail("TIMING C=" + cooldown, exception); }
        }
        Console.WriteLine("TIMING cases=" + cases);
    }

    private static void Set(Type type, object value, string field, object data) { type.GetField(field, All).SetValue(value, data); }
    private static IEnumerable<Type> LoadTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception)
        {
            foreach (string failure in exception.LoaderExceptions.Select(e => e.Message + (e is TypeLoadException ? " Type=" + ((TypeLoadException)e).TypeName : "")).Distinct()) Console.WriteLine("TYPELOAD " + assembly.GetName().Name + " " + failure);
            return exception.Types.Where(t => t != null);
        }
    }
    private static int Property(Type type, object value, string name) { return (int)type.GetProperty(name, All).GetValue(value, null); }
    private static void Fail(string label, Exception exception)
    {
        failures++;
        while (exception.InnerException != null) exception = exception.InnerException;
        Console.WriteLine("FAIL " + label + ": " + exception.GetType().Name + ": " + exception.Message);
        Console.WriteLine(exception.StackTrace);
    }
}
