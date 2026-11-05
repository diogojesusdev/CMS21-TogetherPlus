using System;
using System.Runtime.InteropServices;
using CMS21Together.Shared;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// A car order spawn crashed the game with 0xc0000005 inside <c>il2cpp_object_get_class</c>, because managed code
/// wrapped a pointer it read out of game memory (rcx = 0x411d3e4c, unmapped). SafeMemory is the gate that turns that
/// fatal native read into a skipped read, so its answers have to be exact: a false "readable" brings the crash back.
/// </summary>
[TestFixture]
public class SafeMemoryTests
{
	[Test]
	public void UnmappedAndTinyPointersAreRejected()
	{
		Assert.That(SafeMemory.IsReadable(IntPtr.Zero, 8), Is.False, "null is not readable memory");
		Assert.That(SafeMemory.IsReadable(new IntPtr(0x411d3e4c), 8), Is.False, "the pointer from the crash dump must be refused");
		Assert.That(SafeMemory.IsReadable(new IntPtr(1), 8), Is.False);
		Assert.That(SafeMemory.LooksLikeObject(IntPtr.Zero), Is.False);
		Assert.That(SafeMemory.LooksLikeObject(new IntPtr(0x411d3e4c)), Is.False);
	}

	[Test]
	public void ZeroSizeIsReadableByDefinition()
	{
		Assert.That(SafeMemory.IsReadable(IntPtr.Zero, 0), Is.True);
		Assert.That(SafeMemory.IsReadable(new IntPtr(0x411d3e4c), 0), Is.True);
	}

	[Test]
	public void PinnedMemoryIsReadable()
	{
		var buffer = new byte[4096];
		var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
		try
		{
			var address = handle.AddrOfPinnedObject();

			Assert.That(SafeMemory.IsReadable(address, 1), Is.True);
			Assert.That(SafeMemory.IsReadable(address, 4096), Is.True);
			Assert.That(SafeMemory.IsPlausiblePointer(address), Is.True);
		}
		finally
		{
			handle.Free();
		}
	}

	[Test]
	public void AnObjectNeedsAReadableClassWord()
	{
		// A fake IL2CPP object: its first word is the class pointer.
		var klass = new byte[64];
		var objectBuffer = new byte[64];
		var klassHandle = GCHandle.Alloc(klass, GCHandleType.Pinned);
		var objectHandle = GCHandle.Alloc(objectBuffer, GCHandleType.Pinned);
		try
		{
			var klassPointer = klassHandle.AddrOfPinnedObject();
			var objectPointer = objectHandle.AddrOfPinnedObject();

			Marshal.WriteIntPtr(objectPointer, klassPointer);
			Assert.That(SafeMemory.LooksLikeObject(objectPointer), Is.True);

			Marshal.WriteIntPtr(objectPointer, new IntPtr(0x411d3e4c));
			Assert.That(SafeMemory.LooksLikeObject(objectPointer), Is.False, "a garbage class pointer is exactly the crash");

			Marshal.WriteIntPtr(objectPointer, IntPtr.Zero);
			Assert.That(SafeMemory.LooksLikeObject(objectPointer), Is.False);
		}
		finally
		{
			objectHandle.Free();
			klassHandle.Free();
		}
	}

	[Test]
	public void NullIsAcceptedOnlyWhereTheFieldIsOptional()
	{
		var buffer = new byte[32];
		var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
		try
		{
			var pointer = handle.AddrOfPinnedObject();
			Marshal.WriteIntPtr(pointer, pointer);

			Assert.That(SafeMemory.LooksLikeObjectOrNull(IntPtr.Zero), Is.True);
			Assert.That(SafeMemory.LooksLikeObjectOrNull(pointer), Is.True);
			Assert.That(SafeMemory.LooksLikeObjectOrNull(new IntPtr(0x411d3e4c)), Is.False);
		}
		finally
		{
			handle.Free();
		}
	}

	[Test]
	public void NoAddressInTheDumpEverReadsAsAPlausibleObject()
	{
		// The dump's garbage register values: none of these may be treated as an object.
		Assert.That(SafeMemory.IsReadable(new IntPtr(0x74694694), 8), Is.False);
		Assert.That(SafeMemory.IsReadable(new IntPtr(0x7FFFFFFFFFFFFFFC), 8), Is.False);
		Assert.That(SafeMemory.IsPlausiblePointer(new IntPtr(0x10)), Is.False);
	}
}
