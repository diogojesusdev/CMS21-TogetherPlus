using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnhollowerBaseLib;

namespace CMS21Together.Shared.Safety;

/// <summary>
/// Checks what the game's garbage collector will dereference inside a struct stored inline (a car slot of
/// <c>carsInGarage</c>, for instance): every reference field - including those of nested structs - must be null or
/// point at an object. A float or a small integer in such a field is not a crash yet, but the next collection that
/// marks it is, and that is usually much later (the menu-return crash). Checking right after a write turns that into
/// an error naming the writer and the field.
/// <para>
/// The field list comes from il2cpp itself (<c>il2cpp_class_get_fields</c>), so every field the collector walks is
/// covered, not just the ones the mod reads. Value-type field offsets are relative to the boxed object, so the object
/// header is subtracted for each level of inline struct.
/// </para>
/// </summary>
public static class HeapCheck
{
	private const int TypeString = 0x0e;
	private const int TypeValueType = 0x11;
	private const int TypeClass = 0x12;
	private const int TypeArray = 0x14;
	private const int TypeGenericInst = 0x15;
	private const int TypeObject = 0x1c;
	private const int TypeSzArray = 0x1d;
	private const int FieldStatic = 0x10;
	private const int MaxDepth = 6;

	private static readonly Dictionary<IntPtr, Reference[]> Layouts = new();
	private static readonly object Gate = new();

	private readonly struct Reference
	{
		public readonly string Path;
		public readonly int Offset;

		public Reference(string path, int offset)
		{
			Path = path;
			Offset = offset;
		}
	}

	/// <summary>The outcome of one check.</summary>
	public sealed class Result
	{
		/// <summary>Reference fields looked at.</summary>
		public int Checked;

		/// <summary>The bad fields, as "path = 0x..." (empty when the struct is sound).</summary>
		public readonly List<string> Bad = new();

		/// <summary>Bad fields set to null so the collector cannot fault on them.</summary>
		public int Cleared;

		public bool Sound => Bad.Count == 0;
	}

	/// <summary>
	/// Checks the inline value of <paramref name="valueClass"/> (<paramref name="size"/> bytes) at
	/// <paramref name="data"/>. <paramref name="bias"/> is what il2cpp adds to a value type's field offsets (the object
	/// header; <see cref="Data.Vanilla.Cars.CarFieldLayout"/> measures it). With <paramref name="clear"/>, a bad field is
	/// set to null: a missing value is something the game copes with, a non-object is a native crash in the collector.
	/// Nothing is checked, let alone cleared, when the field layout does not fit the struct.
	/// </summary>
	public static Result CheckInline(IntPtr data, IntPtr valueClass, int size, int bias, bool clear)
	{
		var result = new Result();
		if (data == IntPtr.Zero || valueClass == IntPtr.Zero) return result;

		var layout = LayoutOf(valueClass, bias);
		foreach (var reference in layout)
		{
			if (reference.Offset >= 0 && reference.Offset + IntPtr.Size <= size && reference.Offset % IntPtr.Size == 0) continue;
			result.Bad.Add($"{reference.Path} has an impossible offset {reference.Offset} (the layout was misread; nothing was changed)");
			return result;
		}

		foreach (var reference in layout)
		{
			var at = data + reference.Offset;
			if (!SafeMemory.IsReadable(at, IntPtr.Size))
			{
				result.Bad.Add($"{reference.Path} (not readable memory)");
				continue;
			}

			result.Checked++;
			var value = Marshal.ReadIntPtr(at);
			if (value == IntPtr.Zero || LooksLikeObject(value)) continue;

			result.Bad.Add($"{reference.Path} = 0x{value.ToInt64():X}");
			if (!clear) continue;
			Marshal.WriteIntPtr(at, IntPtr.Zero);
			result.Cleared++;
		}

		return result;
	}

	/// <summary>Heap objects are pointer-aligned, readable, and start with a readable class pointer.</summary>
	private static bool LooksLikeObject(IntPtr value) =>
		(value.ToInt64() & (IntPtr.Size - 1)) == 0 && SafeMemory.LooksLikeObject(value);

	private static Reference[] LayoutOf(IntPtr valueClass, int bias)
	{
		lock (Gate)
		{
			if (Layouts.TryGetValue(valueClass, out var known)) return known;
			var list = new List<Reference>();
			Collect(valueClass, 0, bias, string.Empty, list, 0);
			var layout = list.ToArray();
			Layouts[valueClass] = layout;
			return layout;
		}
	}

	/// <summary>The reference fields of an inline <paramref name="klass"/> starting at <paramref name="start"/>.</summary>
	private static void Collect(IntPtr klass, int start, int bias, string prefix, List<Reference> into, int depth)
	{
		if (depth > MaxDepth) return;

		var iterator = IntPtr.Zero;
		IntPtr field;
		while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator)) != IntPtr.Zero)
		{
			if ((IL2CPP.il2cpp_field_get_flags(field) & FieldStatic) != 0) continue;

			var type = IL2CPP.il2cpp_field_get_type(field);
			if (type == IntPtr.Zero) continue;

			var name = prefix + (Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field)) ?? "?");
			var offset = start + (int)IL2CPP.il2cpp_field_get_offset(field) - bias;

			switch (IL2CPP.il2cpp_type_get_type(type))
			{
				case TypeString:
				case TypeClass:
				case TypeArray:
				case TypeObject:
				case TypeSzArray:
					into.Add(new Reference(name, offset));
					break;

				case TypeValueType:
				case TypeGenericInst:
					var fieldClass = IL2CPP.il2cpp_class_from_il2cpp_type(type);
					if (fieldClass == IntPtr.Zero) break;
					if (!IL2CPP.il2cpp_class_is_valuetype(fieldClass))
						into.Add(new Reference(name, offset)); // a generic class such as List<T>
					else if (!IL2CPP.il2cpp_class_is_enum(fieldClass))
						Collect(fieldClass, offset, bias, name + ".", into, depth + 1);
					break;
			}
		}
	}
}
