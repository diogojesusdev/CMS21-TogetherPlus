using System;
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

	private static Type Element(Type type) => type != null && (type.IsByRef || type.IsPointer) ? type.GetElementType() : type;

	private static bool IsStruct(Type type, Func<Type, bool> isGameStruct)
	{
		var element = Element(type);
		return element != null && element != typeof(void) && isGameStruct(element);
	}
}
