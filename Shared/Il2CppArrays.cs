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

		// Game structs (NewCarData, CustomColor, SaveData) sit inline in the array. Unhollower's indexer is not used
		// for them: it copies with a stride cached in its own static constructor, which stays at IntPtr.Size when the
		// array type is touched before the element class is registered, so the element would be scattered.
		if (value == null) return;
		CopyInline(array, index, IL2CPP.il2cpp_object_unbox(value.Pointer), Element<T>.Size);
		GC.KeepAlive(value);
	}

	/// <summary>
	/// Copies <paramref name="size"/> bytes of an inline value type into element <paramref name="index"/>.
	/// </summary>
	public static void CopyInline<T>(Il2CppReferenceArray<T> array, int index, IntPtr source, int size) where T : Il2CppObjectBase
	{
		if (array == null) throw new ArgumentNullException(nameof(array));
		if ((uint)index >= (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(index));
		if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
		CopyInline(array, array.Pointer + ElementsOffset + index * size, source, size);
	}

	/// <summary>
	/// The same copy, for a caller that has already worked out (and checked) the element's address.
	/// <para>
	/// The bytes are copied plainly, and deliberately not fed through <c>il2cpp_gc_wbarrier_set_field</c>. An inline
	/// struct is mostly not references: <c>NewCarData</c> and <c>SaveData</c> hold bools, ints and floats between
	/// their pointers. Announcing every pointer-sized word as a reference told the collector that a <c>bool</c> of
	/// true was an object at address 1, and the next collection - the one Unity runs while unloading a scene -
	/// dereferenced it and killed the game (0xC0000005 reading 0x1, in GameAssembly's object header code). That is
	/// the crash on returning to the menu.
	/// </para>
	/// <para>
	/// A plain copy is right here: the mod runs the collector non-incrementally (TogetherPlus.GcFix), and Boehm scans
	/// this memory itself and checks whether a word really points into the heap, instead of being told.
	/// </para>
	/// </summary>
	public static void CopyInline<T>(Il2CppReferenceArray<T> array, IntPtr target, IntPtr source, int size) where T : Il2CppObjectBase
	{
		if (array == null) throw new ArgumentNullException(nameof(array));
		if (target == IntPtr.Zero) throw new ArgumentNullException(nameof(target));
		if (source == IntPtr.Zero) throw new ArgumentNullException(nameof(source));
		if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

		var words = size / IntPtr.Size;

		for (var word = 0; word < words; word++)
		{
			var offset = word * IntPtr.Size;
			Marshal.WriteIntPtr(target, offset, Marshal.ReadIntPtr(source, offset));
		}

		for (var rest = words * IntPtr.Size; rest < size; rest++)
			Marshal.WriteByte(target, rest, Marshal.ReadByte(source, rest));

		GC.KeepAlive(array);
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
