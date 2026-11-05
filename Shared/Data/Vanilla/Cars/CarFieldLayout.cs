using System.Collections.Generic;

namespace CMS21Together.Shared.Data.Vanilla.Cars;

/// <summary>
/// Decides how to turn an IL2CPP field offset into an address inside an *inline* (unboxed) value-type element.
/// <para>
/// IL2CPP stores the field offsets of a value type relative to its boxed form, so they include the
/// <c>Il2CppObject</c> header (two pointers). Unhollower relies on that: for a value-type array element it boxes the
/// inline data (<c>il2cpp_value_box</c>) and the generated accessors add the raw offset to the boxed pointer. Code
/// that reads the inline bytes directly must subtract the header again, or every field is read 16 bytes too far -
/// which is exactly why every garage slot came back as "unreadable".
/// </para>
/// </summary>
public static class CarFieldLayout
{
	/// <summary>
	/// The number of bytes to subtract from each offset. <paramref name="problem"/> is non-null when the offsets fit
	/// neither convention, in which case nothing may be read.
	/// </summary>
	/// <param name="offsets">The resolved field offsets; zero means "field not found" and is ignored.</param>
	/// <param name="header">The size of the IL2CPP object header (two pointers).</param>
	/// <param name="elementSize">The unboxed size of the value type.</param>
	/// <param name="headerInclusive">True when the offsets include the object header (the normal case).</param>
	public static int ChooseBias(IEnumerable<uint> offsets, int header, int elementSize, out bool headerInclusive, out string problem)
	{
		headerInclusive = true;
		problem = null;

		if (header <= 0 || elementSize <= 0)
		{
			problem = $"an implausible layout (header {header}, element {elementSize})";
			return 0;
		}

		var smallest = uint.MaxValue;
		var largest = 0u;
		var any = false;

		if (offsets != null)
		{
			foreach (var offset in offsets)
			{
				if (offset == 0) continue; // the field was not found; the caller already handles that
				any = true;
				if (offset < smallest) smallest = offset;
				if (offset > largest) largest = offset;
			}
		}

		if (!any)
		{
			problem = "no field offset could be resolved";
			return 0;
		}

		// The normal case: offsets are relative to the boxed object, so the first field sits at the header.
		if (smallest >= header && largest < header + elementSize) return header;

		// A build whose offsets are already relative to the inline data.
		if (largest < elementSize)
		{
			headerInclusive = false;
			return 0;
		}

		problem = $"field offsets {smallest}..{largest} fit neither an inline nor a boxed {elementSize}-byte value";
		return 0;
	}
}
