using System;
using System.Runtime.InteropServices;

namespace CMS21Together.Shared;

/// <summary>
/// A pointer read out of game memory cannot be trusted. Handing a stale one to Unhollower's wrapper constructor is
/// fatal: <c>Il2CppObjectBase(IntPtr)</c> starts with <c>il2cpp_object_get_class(pointer)</c> and the game dies in that
/// getter with an uncatchable native access violation (0xc0000005), as reported for a car order spawn
/// ("GameAssembly.dll+0x872E0, mov rax,[rcx], rcx = 0x00000000411d3e4c"). Ask Windows whether the pages are mapped
/// before a pointer is wrapped or read.
/// </summary>
public static class SafeMemory
{
	private const uint MemCommit = 0x1000;
	private const uint PageNoAccess = 0x01;
	private const uint PageGuard = 0x100;
	private const long LowestUserAddress = 0x10000;

	/// <summary>No sane pointer below 64 KiB: the first page is reserved and small values are misreads.</summary>
	public static bool IsPlausiblePointer(IntPtr pointer) => pointer.ToInt64() >= LowestUserAddress;

	/// <summary>True when every byte of <c>[address, address + size)</c> is committed and readable.</summary>
	public static bool IsReadable(IntPtr address, int size)
	{
		if (size <= 0) return true;
		if (!IsPlausiblePointer(address)) return false;

		var start = (ulong)address.ToInt64();
		var end = start + (ulong)size;
		var current = start;
		var length = (UIntPtr)(uint)Marshal.SizeOf(typeof(MemoryBasicInformation));

		while (current < end)
		{
			if (VirtualQuery((IntPtr)(long)current, out var region, length) == UIntPtr.Zero) return false;
			if (region.State != MemCommit) return false;
			if ((region.Protect & (PageNoAccess | PageGuard)) != 0) return false;
			if ((region.Protect & 0xFF) == 0) return false;

			var regionEnd = (ulong)region.BaseAddress.ToInt64() + (ulong)region.RegionSize.ToInt64();
			if (regionEnd <= current) return false;
			current = regionEnd;
		}

		return true;
	}

	/// <summary>
	/// True when <paramref name="pointer"/> can be an IL2CPP object: it is readable and so is the class pointer it
	/// starts with (the word <see cref="Il2CppObjectBase"/> reads first).
	/// </summary>
	public static bool LooksLikeObject(IntPtr pointer)
	{
		if (!IsReadable(pointer, IntPtr.Size)) return false;
		var klass = Marshal.ReadIntPtr(pointer);
		return IsReadable(klass, IntPtr.Size);
	}

	/// <summary>Null is a valid value for an optional reference; anything else must look like an object.</summary>
	public static bool LooksLikeObjectOrNull(IntPtr pointer) => pointer == IntPtr.Zero || LooksLikeObject(pointer);

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryBasicInformation
	{
		public IntPtr BaseAddress;
		public IntPtr AllocationBase;
		public uint AllocationProtect;
		public IntPtr RegionSize;
		public uint State;
		public uint Protect;
		public uint Type;
	}

	[DllImport("kernel32.dll")]
	private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation information, UIntPtr length);
}
