using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CMS21Together.Shared.Safety;

/// <summary>
/// Which game methods MelonLoader 0.5.7 can patch without corrupting the game's memory.
/// <para>
/// Its IL2CPP Harmony shim converts every argument of a patched method, and it has no case for the game's value types
/// (<c>NewCarData</c>, <c>FluidsData</c>, <c>CustomColor</c>, ... - classes deriving from <c>Il2CppSystem.ValueType</c>).
/// It wraps the raw pointer to the struct as if it were a boxed object, and the original then receives
/// <c>il2cpp_object_unbox</c> of it: the struct shifted by 16 bytes. <c>SaveCarInGarage</c> stored such shifted cars:
/// the name read as empty (the job car was never sent) and floats landed in reference fields (the GC crash on the way to
/// the menu). It happens for any patch on such a method, even an empty prefix, so the method must not be patched at all.
/// </para>
/// </summary>
public static class PatchRules
{
	public const string Why =
		"MelonLoader 0.5.7 hands such a method a struct shifted by 16 bytes, which corrupts the game's memory; patch a caller with plain parameters instead.";

	/// <summary>Null when <paramref name="method"/> can be patched; otherwise why it cannot.</summary>
	public static string UnsafeReason(MethodBase method, Func<Type, bool> isGameStruct)
	{
		if (method == null || isGameStruct == null) return null;

		foreach (var parameter in method.GetParameters())
			if (IsStruct(parameter.ParameterType, isGameStruct))
				return $"its parameter '{parameter.Name}' is the game value type {Element(parameter.ParameterType).Name}";

		if (method is MethodInfo info && IsStruct(info.ReturnType, isGameStruct))
			return $"it returns the game value type {Element(info.ReturnType).Name}";

		if (!method.IsStatic && method.DeclaringType != null && IsStruct(method.DeclaringType, isGameStruct))
			return $"it is a method of the game value type {method.DeclaringType.Name}";

		return null;
	}

	public static string Describe(MethodBase method) =>
		method == null
			? "(no method)"
			: $"{method.DeclaringType?.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

	public const string SharedWhy =
		"IL2CPP compiled identical methods to one native function and Harmony detours that function, so the patch would run for every one of them with an object of the wrong type; patch a method of its own instead.";

	/// <summary>
	/// Null when the method's native code is its own; otherwise why it cannot be patched. <paramref name="users"/> is how
	/// many game methods run that code (0 when unknown), <paramref name="others"/> a few of the other ones.
	/// </summary>
	public static string SharedCodeReason(int users, IEnumerable<string> others)
	{
		if (users <= 1) return null;
		var names = others?.Where(n => !string.IsNullOrEmpty(n)).ToList() ?? new List<string>();
		var more = users - 1 - names.Count;
		var list = names.Count == 0 ? "" : " (" + string.Join(", ", names) + (more > 0 ? $", +{more} more" : "") + ")";
		return $"its native code is shared with {users - 1} other game method{(users == 2 ? "" : "s")}{list}";
	}

	private static Type Element(Type type) => type != null && (type.IsByRef || type.IsPointer) ? type.GetElementType() : type;

	private static bool IsStruct(Type type, Func<Type, bool> isGameStruct)
	{
		var element = Element(type);
		return element != null && element != typeof(void) && isGameStruct(element);
	}
}
