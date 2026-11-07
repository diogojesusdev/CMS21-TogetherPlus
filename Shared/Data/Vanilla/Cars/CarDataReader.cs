using System;
using System.Runtime.InteropServices;
using System.Text;
using UnhollowerBaseLib;

namespace CMS21Together.Shared.Data.Vanilla.Cars;

/// <summary>
/// Reads <c>ProfileData.carsInGarage[index]</c> from raw memory, with the real element stride and only after checking
/// every pointer it is about to hand to Unhollower.
/// <para>
/// Two hazards live here. (1) <c>Il2CppReferenceArray&lt;NewCarData&gt;</c> caches its element stride in its static
/// constructor: it starts at <c>IntPtr.Size</c> and only uses <c>il2cpp_class_value_size</c> when
/// <c>Il2CppClassPointerStore&lt;NewCarData&gt;.NativeClassPtr</c> is already set (it is set by NewCarData's own static
/// constructor). When an array is the first thing touched in a session the stride stays 8, so every slot after the
/// first is read from the wrong address - silently, without an exception. (2) A car that is still being spawned can
/// hold stale pointers, and wrapping one crashes the game inside <c>il2cpp_object_get_class</c>.
/// </para>
/// <para>
/// The field offsets IL2CPP reports for a value type are relative to its *boxed* form, so they include the object
/// header; the elements of the array are unboxed. <see cref="CarFieldLayout"/> works out the bias to subtract, and
/// the wrapper is built from a real box (as Unhollower does) so that reading or storing it stays in step with the
/// game.
/// </para>
/// </summary>
public static class CarDataReader
{
	private static readonly int ArrayHeader = 4 * IntPtr.Size; // Il2CppArray: klass, monitor, bounds, max_length
	private static readonly int ObjectHeader = 2 * IntPtr.Size; // Il2CppObject: klass, monitor
	private static readonly int StringHeader = 2 * IntPtr.Size; // Il2CppString: klass, monitor, then length
	private static readonly int StringLengthOffset = 2 * IntPtr.Size;
	private static readonly int StringCharsOffset = 2 * IntPtr.Size + 4; // ... and the chars follow the length
	private const int MaxStringLength = 512;

	private static readonly object Gate = new object();

	private static bool prepared;
	private static int elementSize;
	private static int offsetBias;
	private static IntPtr carClass;

	private static uint offCarToLoad;
	private static uint offUId;
	private static uint offEngineSwap;
	private static uint offBodyPartsData;
	private static uint offPartData;
	private static uint offBonusPartsData;
	private static uint offColor;
	private static uint offFactoryColor;
	private static uint offGearRatio;
	private static uint offTiresET;
	private static uint offWheelsWidth;
	private static uint offRimsSize;
	private static uint offTiresSize;

	private static uint offBonusIds;
	private static uint offBonusIsPainted;
	private static uint offBonusColor;
	private static uint offBonusPaintType;
	private static uint offBonusPaintData;
	private static uint offBonusIdFromConfig;

	/// <summary>
	/// The car on <paramref name="index"/>. An empty or unreadable slot gives a default car (a warning only when the
	/// slot held something unreadable), so callers that poke a single field keep working.
	/// <para>
	/// Never use this to copy a car: the default it falls back to has null strings, arrays and lists, and handing
	/// such a car back to the game crashes it in native code (see the menu-return crash). Use
	/// <see cref="CopySlot"/>, which reproduces the slot byte for byte.
	/// </para>
	/// </summary>
	public static NewCarData At(Il2CppReferenceArray<NewCarData> cars, int index)
	{
		if (TryRead(cars, index, out var data, out var problem)) return data;
		if (problem != null) Log.Warn($"[CarDataReader] Garage slot {index} could not be read: {problem}");
		return new NewCarData();
	}

	/// <summary>
	/// Copies one car from <paramref name="source"/> into <paramref name="target"/> exactly as the game stores it,
	/// through the GC write barrier. Nothing is interpreted, so a car the validated read cannot make sense of still
	/// survives a copy unchanged instead of being replaced by a blank one.
	/// </summary>
	public static bool CopySlot(Il2CppReferenceArray<NewCarData> source, Il2CppReferenceArray<NewCarData> target, int index, out string problem)
	{
		if (!Prepare(out problem)) return false;
		if (!TrySlot(source, index, out var from, out problem)) return false;
		if (!TrySlot(target, index, out var to, out problem)) return false;

		Il2CppArrays.CopyInline(target, to, from, elementSize);
		GC.KeepAlive(source);
		GC.KeepAlive(target);
		return true;
	}

	/// <summary>
	/// Reads a car only when its memory is consistent. Never wraps an unchecked pointer. <paramref name="problem"/>
	/// is null when the slot is simply empty (no <c>carToLoad</c>), so callers can tell "not spawned yet" from "bad".
	/// </summary>
	public static bool TryRead(Il2CppReferenceArray<NewCarData> cars, int index, out NewCarData data, out string problem)
	{
		data = null;

		if (!Prepare(out problem)) return false;
		if (!TrySlot(cars, index, out var slot, out problem)) return false;

		var namePointer = ReadPointer(slot, offCarToLoad);
		if (namePointer == IntPtr.Zero)
		{
			problem = null; // an empty garage slot, not a broken one
			return false;
		}

		if (!TryReadString(namePointer, out var name))
		{
			problem = "carToLoad is not a readable string";
			return false;
		}

		if (string.IsNullOrEmpty(name))
		{
			problem = null; // a slot the game has reset to "", not a broken one
			return false;
		}

		if (!CheckString(ReadPointer(slot, offUId), "UId", ref problem)) return false;
		if (!CheckString(ReadPointer(slot, offEngineSwap), "engineSwap", ref problem)) return false;
		if (!CheckArray(ReadPointer(slot, offColor), "color", ref problem)) return false;
		if (!CheckArray(ReadPointer(slot, offFactoryColor), "factoryColor", ref problem)) return false;
		if (!CheckArray(ReadPointer(slot, offGearRatio), "gearRatio", ref problem)) return false;
		if (!CheckArray(ReadPointer(slot, offTiresET), "tiresET", ref problem)) return false;
		if (!CheckArray(ReadPointer(slot, offWheelsWidth), "wheelsWidth", ref problem)) return false;
		if (!CheckArray(ReadPointer(slot, offRimsSize), "rimsSize", ref problem)) return false;
		if (!CheckArray(ReadPointer(slot, offTiresSize), "tiresSize", ref problem)) return false;
		if (!CheckList(ReadPointer(slot, offBodyPartsData), "BodyPartsData", ref problem)) return false;
		if (!CheckList(ReadPointer(slot, offPartData), "PartData", ref problem)) return false;
		if (!CheckBonusParts(Field(slot, offBonusPartsData), ref problem)) return false;

		// Unhollower's own array indexer boxes the inline element before it wraps it; the generated field accessors
		// then add the header-inclusive offsets to that boxed pointer. Handing the wrapper the raw inline address
		// would make every field read (and every il2cpp_object_unbox on the way back out) 16 bytes wrong.
		var boxed = IL2CPP.il2cpp_value_box(carClass, slot);
		if (!SafeMemory.LooksLikeObject(boxed))
		{
			problem = "the car could not be boxed";
			return false;
		}

		data = new NewCarData(boxed);
		return true;
	}

	/// <summary>The <c>carToLoad</c> of a slot, read without wrapping anything that cannot be read.</summary>
	public static bool TryReadName(Il2CppReferenceArray<NewCarData> cars, int index, out string name)
	{
		name = null;
		if (!Prepare(out _)) return false;
		if (!TrySlot(cars, index, out var slot, out _)) return false;

		var pointer = ReadPointer(slot, offCarToLoad);
		if (pointer == IntPtr.Zero)
		{
			name = string.Empty; // a slot the game has not filled in, which is readable and simply has no car
			return true;
		}

		return TryReadString(pointer, out name);
	}

	/// <summary>
	/// The slot that holds <paramref name="carName"/>, preferring <paramref name="preferred"/>. -1 when no slot holds
	/// it: a wrong slot must never be converted, that is how the crash report's garbage car was read.
	/// </summary>
	public static int FindSlot(Il2CppReferenceArray<NewCarData> cars, string carName, int preferred)
	{
		if (!Prepare(out var preparedProblem))
		{
			Log.Error($"[CarDataReader] The garage array cannot be read: {preparedProblem}");
			return -1;
		}

		if (cars == null) return -1;

		if (TryReadName(cars, preferred, out var atPreferred) && string.Equals(atPreferred, carName, StringComparison.Ordinal))
			return preferred;

		var length = Length(cars);
		for (var i = 0; i < length; i++)
		{
			if (i == preferred) continue;
			if (TryReadName(cars, i, out var found) && string.Equals(found, carName, StringComparison.Ordinal)) return i;
		}

		return -1;
	}

	/// <summary>What every slot holds, for the error log when a car cannot be found.</summary>
	public static string Describe(Il2CppReferenceArray<NewCarData> cars)
	{
		if (cars == null) return "(no car array)";

		var length = Length(cars);
		if (length < 0) return "(the car array is not readable)";

		var text = new StringBuilder();
		for (var i = 0; i < length; i++)
		{
			if (i > 0) text.Append(", ");
			text.Append('[').Append(i).Append("]=");
			text.Append(TryReadName(cars, i, out var name) ? (string.IsNullOrEmpty(name) ? "<empty>" : name) : "<unreadable>");
		}

		return text.ToString();
	}

	/// <summary>Number of slots, or -1 when the array cannot be read.</summary>
	public static int Count(Il2CppReferenceArray<NewCarData> cars) => Length(cars);

	/// <summary>
	/// Where slot <paramref name="index"/>'s inline car lives, with the NewCarData class and the field bias, for a
	/// check of every field (<see cref="CarSlotAudit"/>). Nothing is wrapped.
	/// </summary>
	public static bool TryLocate(Il2CppReferenceArray<NewCarData> cars, int index, out IntPtr slot, out IntPtr klass, out int size, out int bias, out string problem)
	{
		slot = IntPtr.Zero;
		klass = IntPtr.Zero;
		size = 0;
		bias = 0;
		if (!Prepare(out problem)) return false;
		if (!TrySlot(cars, index, out slot, out problem)) return false;
		klass = carClass;
		size = elementSize;
		bias = offsetBias;
		return true;
	}

	private static int Length(Il2CppReferenceArray<NewCarData> cars)
	{
		if (cars == null) return -1;
		if (!SafeMemory.LooksLikeObject(cars.Pointer) || !SafeMemory.IsReadable(cars.Pointer, ArrayHeader)) return -1;
		var length = IL2CPP.il2cpp_array_length(cars.Pointer);
		return length > int.MaxValue ? -1 : (int)length;
	}

	private static bool Prepare(out string problem)
	{
		problem = null;
		if (prepared) return true;

		lock (Gate)
		{
			if (prepared) return true;

			try
			{
				// NewCarData is an il2cpp value type: creating one runs the generated static constructor that registers
				// Il2CppClassPointerStore<NewCarData>.NativeClassPtr, which the array classes read for their stride.
				GC.KeepAlive(new NewCarData());
				GC.KeepAlive(new BonusPartsData());

				var klass = Il2CppClassPointerStore<NewCarData>.NativeClassPtr;
				if (klass == IntPtr.Zero)
				{
					problem = "the NewCarData class is not registered";
					return false;
				}

				uint align = 0;
				var size = IL2CPP.il2cpp_class_value_size(klass, ref align);
				if (size < 3 * IntPtr.Size || size > 8192)
				{
					problem = $"NewCarData reports an implausible size ({size})";
					return false;
				}

				elementSize = size;

				offCarToLoad = Offset(klass, "carToLoad");
				offUId = Offset(klass, "UId");
				offEngineSwap = Offset(klass, "engineSwap");
				offBodyPartsData = Offset(klass, "BodyPartsData");
				offPartData = Offset(klass, "PartData");
				offBonusPartsData = Offset(klass, "BonusPartsData");
				offColor = Offset(klass, "color");
				offFactoryColor = Offset(klass, "factoryColor");
				offGearRatio = Offset(klass, "gearRatio");
				offTiresET = Offset(klass, "tiresET");
				offWheelsWidth = Offset(klass, "wheelsWidth");
				offRimsSize = Offset(klass, "rimsSize");
				offTiresSize = Offset(klass, "tiresSize");

				var bonusClass = Il2CppClassPointerStore<BonusPartsData>.NativeClassPtr;
				if (bonusClass != IntPtr.Zero)
				{
					offBonusIds = Offset(bonusClass, "IDs");
					offBonusIsPainted = Offset(bonusClass, "IsPainted");
					offBonusColor = Offset(bonusClass, "Color");
					offBonusPaintType = Offset(bonusClass, "PaintType");
					offBonusPaintData = Offset(bonusClass, "PaintData");
					offBonusIdFromConfig = Offset(bonusClass, "IdFromConfig");
				}

				if (offCarToLoad == 0 || offBodyPartsData == 0 || offPartData == 0)
				{
					problem = "the NewCarData field layout could not be resolved";
					return false;
				}

				// The offsets IL2CPP reports are relative to a boxed NewCarData; the array holds unboxed elements.
				offsetBias = CarFieldLayout.ChooseBias(
					new[]
					{
						offCarToLoad, offUId, offEngineSwap, offBodyPartsData, offPartData, offBonusPartsData,
						offColor, offFactoryColor, offGearRatio, offTiresET, offWheelsWidth, offRimsSize, offTiresSize
					},
					ObjectHeader, elementSize, out var headerInclusive, out var layoutProblem);
				if (layoutProblem != null)
				{
					problem = layoutProblem;
					return false;
				}
				if (!headerInclusive)
					Log.Warn("[CarDataReader] NewCarData reports inline field offsets; reading them without the object header.");

				carClass = klass;
				prepared = true;
				Log.Info($"[CarDataReader] Reading cars with the {elementSize}-byte NewCarData stride (field bias {offsetBias}).");
				return true;
			}
			catch (Exception ex)
			{
				problem = $"the NewCarData layout could not be prepared: {ex.Message}";
				return false;
			}
		}
	}

	private static uint Offset(IntPtr klass, string name)
	{
		var field = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
		return field == IntPtr.Zero ? 0u : IL2CPP.il2cpp_field_get_offset(field);
	}

	private static bool TrySlot(Il2CppReferenceArray<NewCarData> cars, int index, out IntPtr slot, out string problem)
	{
		slot = IntPtr.Zero;
		problem = null;

		if (cars == null)
		{
			problem = "the profile has no car array";
			return false;
		}

		var length = Length(cars);
		if (length < 0)
		{
			problem = "the car array is not readable";
			return false;
		}

		if ((uint)index >= (uint)length)
		{
			problem = $"slot {index} is outside the car array (0..{length - 1})";
			return false;
		}

		slot = cars.Pointer + ArrayHeader + (index * elementSize);
		if (!SafeMemory.IsReadable(slot, elementSize))
		{
			problem = $"slot {index} is not readable memory";
			slot = IntPtr.Zero;
			return false;
		}

		return true;
	}

	/// <summary>
	/// The address of a field inside an inline (unboxed) value type. The reported offset includes the object header
	/// that the inline bytes do not have, so the bias is taken off again.
	/// </summary>
	private static IntPtr Field(IntPtr inlineBase, uint offset) => inlineBase + (int)offset - offsetBias;

	private static IntPtr ReadPointer(IntPtr at, uint offset)
	{
		if (offset == 0) return IntPtr.Zero;
		var address = Field(at, offset);
		if (!SafeMemory.IsReadable(address, IntPtr.Size)) return IntPtr.Zero;
		return Marshal.ReadIntPtr(address);
	}

	private static bool TryReadString(IntPtr pointer, out string value)
	{
		value = null;
		if (!SafeMemory.LooksLikeObject(pointer)) return false;
		if (!SafeMemory.IsReadable(pointer, StringHeader)) return false;

		var length = Marshal.ReadInt32(pointer + StringLengthOffset);
		if (length < 0 || length > MaxStringLength) return false;
		if (!SafeMemory.IsReadable(pointer + StringCharsOffset, (length + 1) * 2)) return false;

		value = IL2CPP.Il2CppStringToManaged(pointer);
		return value != null;
	}

	private static bool CheckString(IntPtr pointer, string name, ref string problem)
	{
		if (pointer == IntPtr.Zero) return true;
		if (TryReadString(pointer, out _)) return true;
		problem = $"{name} is not a readable string";
		return false;
	}

	private static bool CheckArray(IntPtr pointer, string name, ref string problem)
	{
		if (pointer == IntPtr.Zero) return true;
		if (!SafeMemory.LooksLikeObject(pointer) || !SafeMemory.IsReadable(pointer, ArrayHeader))
		{
			problem = $"{name} is not a readable array";
			return false;
		}

		if (IL2CPP.il2cpp_array_length(pointer) > 100000)
		{
			problem = $"{name} reports an implausible length";
			return false;
		}

		return true;
	}

	private static bool CheckList(IntPtr pointer, string name, ref string problem)
	{
		if (pointer == IntPtr.Zero) return true;
		if (!SafeMemory.LooksLikeObject(pointer))
		{
			problem = $"{name} is not a readable list";
			return false;
		}

		// The list's own class gives the layout, so this works for every List<T> without a generic class lookup.
		var klass = Marshal.ReadIntPtr(pointer);
		var itemsOffset = Offset(klass, "_items");
		var sizeOffset = Offset(klass, "_size");
		if (itemsOffset == 0 || sizeOffset == 0 || !SafeMemory.IsReadable(pointer + (int)sizeOffset, 4))
		{
			problem = $"{name} has no List layout";
			return false;
		}

		var count = Marshal.ReadInt32(pointer + (int)sizeOffset);
		if (count < 0)
		{
			problem = $"{name} reports {count} item(s)";
			return false;
		}

		if (count == 0) return true;

		var items = ReadPointer(pointer, itemsOffset);
		if (items == IntPtr.Zero)
		{
			problem = $"{name} reports {count} item(s) but has no storage";
			return false;
		}

		if (!SafeMemory.LooksLikeObject(items) || !SafeMemory.IsReadable(items, ArrayHeader))
		{
			problem = $"{name} reports {count} item(s) but its storage is not readable";
			return false;
		}

		if (IL2CPP.il2cpp_array_length(items) < (uint)count)
		{
			problem = $"{name} reports {count} item(s) but its storage holds fewer";
			return false;
		}

		return true;
	}

	private static bool CheckBonusParts(IntPtr at, ref string problem)
	{
		// A stale-inline bonus block is only a problem when we cannot read it at all: every field is optional.
		if (offBonusIds == 0 || offBonusPartsData == 0) return true;
		if (!SafeMemory.IsReadable(at, IntPtr.Size))
		{
			problem = "BonusPartsData is not readable";
			return false;
		}

		if (!CheckArray(ReadPointer(at, offBonusIds), "BonusPartsData.IDs", ref problem)) return false;
		if (!CheckArray(ReadPointer(at, offBonusIsPainted), "BonusPartsData.IsPainted", ref problem)) return false;
		if (!CheckArray(ReadPointer(at, offBonusColor), "BonusPartsData.Color", ref problem)) return false;
		if (!CheckArray(ReadPointer(at, offBonusPaintType), "BonusPartsData.PaintType", ref problem)) return false;
		if (!CheckArray(ReadPointer(at, offBonusPaintData), "BonusPartsData.PaintData", ref problem)) return false;
		if (!CheckArray(ReadPointer(at, offBonusIdFromConfig), "BonusPartsData.IdFromConfig", ref problem)) return false;

		return true;
	}
}
