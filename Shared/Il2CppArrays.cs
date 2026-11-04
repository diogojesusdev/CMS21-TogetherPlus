using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnhollowerBaseLib;

namespace CMS21Together.Shared;

/// <summary>
/// Stores into IL2CPP arrays through the GC write barrier. Unhollower's indexers (and the implicit T[] conversions)
/// write the slot directly, which the game's incremental GC can miss: an object held only by an already scanned
/// array is then freed while the game still uses it (crashes, cars whose lists come back corrupted).
/// </summary>
public static class Il2CppArrays
{
	private static readonly int ElementsOffset = 4 * IntPtr.Size;

	public static void Set<T>(Il2CppReferenceArray<T> array, int index, T value) where T : Il2CppObjectBase
	{
		if (!Element<T>.IsValueType)
		{
			Store(array, index, value == null ? IntPtr.Zero : value.Pointer);
			GC.KeepAlive(value);
			return;
		}

		// Game structs (NewCarData, CustomColor, SaveData) sit inline in the array: Unhollower copies the value in,
		// then every pointer-sized word of the element goes through the barrier so the references inside are seen.
		if (value == null) return;
		array[index] = value;
		var start = array.Pointer + ElementsOffset + index * Element<T>.Size;
		for (var offset = 0; offset + IntPtr.Size <= Element<T>.Size; offset += IntPtr.Size)
		{
			var slot = start + offset;
			IL2CPP.il2cpp_gc_wbarrier_set_field(array.Pointer, slot, Marshal.ReadIntPtr(slot));
		}
		GC.KeepAlive(value);
	}

	private static class Element<T>
	{
		public static readonly bool IsValueType = typeof(Il2CppSystem.ValueType).IsAssignableFrom(typeof(T));
		public static readonly int Size = IsValueType ? ValueSize() : IntPtr.Size;

		private static int ValueSize()
		{
			uint align = 0;
			return IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<T>.NativeClassPtr, ref align);
		}
	}

	public static void Set(Il2CppStringArray array, int index, string value) =>
		Store(array, index, value == null ? IntPtr.Zero : IL2CPP.ManagedStringToIl2Cpp(value));

	public static Il2CppReferenceArray<T> Of<T>(IList<T> items) where T : Il2CppObjectBase
	{
		if (items == null) return null;
		var array = new Il2CppReferenceArray<T>(items.Count);
		for (var i = 0; i < items.Count; i++) Set(array, i, items[i]);
		return array;
	}

	public static Il2CppStringArray Of(IList<string> items)
	{
		if (items == null) return null;
		var array = new Il2CppStringArray(items.Count);
		for (var i = 0; i < items.Count; i++) Set(array, i, items[i]);
		return array;
	}

	private static void Store<T>(Il2CppArrayBase<T> array, int index, IntPtr value)
	{
		if (array == null) throw new ArgumentNullException(nameof(array));
		if ((uint)index >= (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(index));
		var slot = array.Pointer + ElementsOffset + index * IntPtr.Size;
		IL2CPP.il2cpp_gc_wbarrier_set_field(array.Pointer, slot, value);
	}
}
