using System;
using System.Linq;
using System.Reflection;

namespace CMS21Together.Shared.Safety;

/// <summary>
/// Applies <see cref="PatchRules"/> to the game's real types: a patch on a method that takes, returns or belongs to a
/// game value type corrupts memory, and one on a method whose native code IL2CPP shares with other methods runs for all
/// of them, so such a patch is refused before it is made (<see cref="Require"/>) and reported if one was made anyway
/// (<see cref="AuditApplied"/>).
/// </summary>
public static class PatchSafety
{
	/// <summary>A game struct: a class Unhollower derived from <c>Il2CppSystem.ValueType</c> (enums are real enums).</summary>
	public static bool IsGameStruct(Type type) =>
		type != null
		&& type != typeof(Il2CppSystem.ValueType)
		&& type.FullName != "Il2CppSystem.Enum"
		&& typeof(Il2CppSystem.ValueType).IsAssignableFrom(type);

	public static string UnsafeReason(MethodBase method) => Problem(method)?.Reason;

	private sealed class Finding
	{
		public string Reason;
		public string Why;
	}

	private static Finding Problem(MethodBase method)
	{
		var reason = PatchRules.UnsafeReason(method, IsGameStruct);
		if (reason != null) return new Finding { Reason = reason, Why = PatchRules.Why };

		var users = NativeCode.Users(method);
		if (users <= 1) return null;
		return new Finding { Reason = PatchRules.SharedCodeReason(users, NativeCode.Others(method, 3)), Why = PatchRules.SharedWhy };
	}

	/// <summary>Throws instead of patching <paramref name="target"/> when the patch would corrupt memory.</summary>
	public static void Require(MethodBase target)
	{
		var problem = Problem(target);
		if (problem != null)
			throw new InvalidOperationException($"{PatchRules.Describe(target)} cannot be patched: {problem.Reason}. {problem.Why}");
	}

	/// <summary>
	/// Reports every method in the game that is already patched unsafely, by this mod or by another one. Harmony cannot
	/// undo it (unpatching rebuilds the same shim), so this is the loudest thing that can be done after the fact.
	/// </summary>
	public static void AuditApplied()
	{
		var checkedCount = 0;
		var bad = 0;
		foreach (var method in HarmonyLib.Harmony.GetAllPatchedMethods().ToList())
		{
			checkedCount++;
			var problem = Problem(method);
			if (problem == null) continue;

			bad++;
			var owners = HarmonyLib.Harmony.GetPatchInfo(method)?.Owners;
			var by = owners == null || owners.Count == 0 ? "an unknown mod" : string.Join(", ", owners);
			Log.Error($"[Patches] {PatchRules.Describe(method)} is patched by {by}, but {problem.Reason}. {problem.Why}");
		}

		if (bad == 0) Log.Info($"[Patches] {checkedCount} patched game methods checked: none takes a game value type or shares its native code.");
	}
}
