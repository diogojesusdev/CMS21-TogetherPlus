using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using CMS21Together.Shared.Safety;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// No Harmony patch may target a game method that takes, returns or belongs to a game value type: MelonLoader 0.5.7
/// shifts such a struct by 16 bytes, which is how the job car was never sent and the menu-return GC crash happened.
/// The attribute patches of the built mod are checked here; the hand-made feature patches are refused at runtime by
/// <c>GameFeature.Target</c>. Skipped when the mod DLL has not been built yet (run build.ps1).
/// </summary>
[TestFixture]
public class HarmonyTargetTests
{
	private class FakeValueType
	{
	}

	private class FakeCar : FakeValueType
	{
		public void Instance()
		{
		}
	}

	private static bool IsFake(Type type) => type != typeof(FakeValueType) && typeof(FakeValueType).IsAssignableFrom(type);

	private static void TakesCar(FakeCar car, int index)
	{
	}

	private static void TakesCarByRef(ref FakeCar car)
	{
	}

	private static FakeCar ReturnsCar() => null;

	private static void Plain(int index, bool toParking, string name, FakeValueType boxed)
	{
	}

	private static MethodInfo Method(string name) =>
		typeof(HarmonyTargetTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

	[Test]
	public void RulesRefuseGameValueTypes()
	{
		StringAssert.Contains("parameter 'car'", PatchRules.UnsafeReason(Method(nameof(TakesCar)), IsFake));
		StringAssert.Contains("parameter 'car'", PatchRules.UnsafeReason(Method(nameof(TakesCarByRef)), IsFake));
		StringAssert.Contains("returns", PatchRules.UnsafeReason(Method(nameof(ReturnsCar)), IsFake));
		StringAssert.Contains("method of the game value type",
			PatchRules.UnsafeReason(typeof(FakeCar).GetMethod(nameof(FakeCar.Instance)), IsFake));
	}

	[Test]
	public void RulesAllowPlainParameters()
	{
		Assert.IsNull(PatchRules.UnsafeReason(Method(nameof(Plain)), IsFake));
	}

	[Test]
	public void NoAttributePatchTargetsAGameValueType()
	{
		using var context = LoadMod(out var mod, out var valueType);
		bool IsGameStruct(Type t) => !t.Equals(valueType) && t.FullName != "Il2CppSystem.Enum" && valueType.IsAssignableFrom(t);

		var resolved = 0;
		var unresolved = new List<string>();
		var unsafeTargets = new List<string>();
		foreach (var type in Types(mod))
		{
			List<Target> classLevel;
			try
			{
				classLevel = Patches(type.GetCustomAttributesData());
			}
			catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException)
			{
				unresolved.Add($"{type.Name}: {ex.Message}");
				continue;
			}

			foreach (var patch in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
			{
				try
				{
					var own = Patches(patch.GetCustomAttributesData());
					if (own.Count == 0 && (classLevel.Count == 0 || !IsPatchMethod(patch))) continue;

					var target = Merge(classLevel.Concat(own));
					foreach (var original in Resolve(target))
					{
						resolved++;
						var reason = PatchRules.UnsafeReason(original, IsGameStruct);
						if (reason != null) unsafeTargets.Add($"{type.Name}.{patch.Name} patches {PatchRules.Describe(original)}: {reason}");
					}
				}
				catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException)
				{
					unresolved.Add($"{type.Name}.{patch.Name}: {ex.Message}");
				}
			}
		}

		if (unresolved.Count > 0) TestContext.WriteLine("Not checked:" + Environment.NewLine + string.Join(Environment.NewLine, unresolved));
		Assert.Greater(resolved, 30, "The scan found too few patch targets; the attribute parsing is broken.");
		Assert.IsEmpty(unsafeTargets, string.Join(Environment.NewLine, unsafeTargets));
	}

	private sealed class Target
	{
		public Type DeclaringType;
		public string Method;
		public Type[] Arguments;
		public int MethodType;
	}

	private static bool IsPatchMethod(MethodInfo method) =>
		method.Name is "Prefix" or "Postfix" or "Finalizer" or "Transpiler" ||
		method.GetCustomAttributesData().Any(a => a.AttributeType.FullName is "HarmonyLib.HarmonyPrefix" or "HarmonyLib.HarmonyPostfix" or "HarmonyLib.HarmonyFinalizer" or "HarmonyLib.HarmonyTranspiler");

	private static List<Target> Patches(IEnumerable<CustomAttributeData> attributes)
	{
		var list = new List<Target>();
		foreach (var attribute in attributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
		{
			var target = new Target();
			foreach (var argument in attribute.ConstructorArguments)
			{
				if (argument.Value is Type declaringType) target.DeclaringType = declaringType;
				else if (argument.Value is string method) target.Method = method;
				else if (argument.Value is ReadOnlyCollection<CustomAttributeTypedArgument> types && argument.ArgumentType.IsArray)
					target.Arguments = types.Select(t => (Type)t.Value).ToArray();
				else if (argument.ArgumentType.FullName == "HarmonyLib.MethodType") target.MethodType = Convert.ToInt32(argument.Value);
			}
			list.Add(target);
		}
		return list;
	}

	private static Target Merge(IEnumerable<Target> parts)
	{
		var merged = new Target();
		foreach (var part in parts)
		{
			merged.DeclaringType = part.DeclaringType ?? merged.DeclaringType;
			merged.Method = part.Method ?? merged.Method;
			merged.Arguments = part.Arguments ?? merged.Arguments;
			if (part.MethodType != 0) merged.MethodType = part.MethodType;
		}
		return merged;
	}

	private static IEnumerable<MethodBase> Resolve(Target target)
	{
		if (target.DeclaringType == null) return Enumerable.Empty<MethodBase>();
		const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

		IEnumerable<MethodBase> candidates;
		switch (target.MethodType)
		{
			case 1: // getter
				candidates = target.DeclaringType.GetMethods(all).Where(m => m.Name == "get_" + target.Method);
				break;
			case 2: // setter
				candidates = target.DeclaringType.GetMethods(all).Where(m => m.Name == "set_" + target.Method);
				break;
			case 3: // constructor
				candidates = target.DeclaringType.GetConstructors(all);
				break;
			default:
				if (target.Method == null) return Enumerable.Empty<MethodBase>();
				candidates = target.DeclaringType.GetMethods(all).Where(m => m.Name == target.Method);
				break;
		}

		if (target.Arguments == null) return candidates.ToList();
		return candidates.Where(m => m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(target.Arguments.Select(a => a.FullName))).ToList();
	}

	private static IEnumerable<Type> Types(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			return ex.Types.Where(t => t != null);
		}
	}

	/// <summary>
	/// Metadata only: the unhollowed game types cannot be loaded into the test's runtime (.NET Framework rejects the
	/// layout of <c>UnityEngine.Object</c>), and nothing of the game may run here anyway.
	/// </summary>
	private static MetadataLoadContext LoadMod(out Assembly mod, out Type valueType)
	{
		var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CMS21-Together.csproj"))) dir = dir.Parent;
		if (dir == null) Assert.Ignore("Repository root not found.");

		var dll = new[] { "Release", "Debug" }
			.Select(c => Path.Combine(dir.FullName, "bin", c, "CMS21-Together.dll"))
			.Where(File.Exists)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
		if (dll == null) Assert.Ignore("CMS21-Together.dll not built; run build.ps1 first.");

		var libs = Path.Combine(dir.FullName, "Libs");
		var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
		var paths = new[] { dll }
			.Concat(Directory.GetFiles(libs, "*.dll"))
			.Concat(Directory.GetFiles(runtime, "*.dll"))
			.Where(p => !string.Equals(Path.GetFileName(p), "steam_api64.dll", StringComparison.OrdinalIgnoreCase))
			.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First());

		var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "mscorlib");
		mod = context.LoadFromAssemblyPath(dll);
		valueType = context.LoadFromAssemblyPath(Path.Combine(libs, "Il2Cppmscorlib.dll")).GetType("Il2CppSystem.ValueType", true);
		return context;
	}
}
