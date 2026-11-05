using System;
using CMS21Together.Shared.Data.Vanilla.Cars;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// Every garage slot read back as "unreadable" on a car order (rc25) because the IL2CPP field offsets of a value type
/// include the object header, while the array elements are stored unboxed. These pin the bias down.
/// </summary>
[TestFixture]
public class CarFieldLayoutTests
{
	private const int Header = 16;
	private const int ElementSize = 440;

	[Test]
	public void BoxedOffsetsSubtractTheHeader()
	{
		var bias = CarFieldLayout.ChooseBias(new uint[] { 16, 24, 40, 432 }, Header, ElementSize, out var headerInclusive, out var problem);

		Assert.That(problem, Is.Null);
		Assert.That(headerInclusive, Is.True);
		Assert.That(bias, Is.EqualTo(Header), "the first field of a boxed value type sits right after the header");
	}

	[Test]
	public void InlineOffsetsKeepTheirAddress()
	{
		var bias = CarFieldLayout.ChooseBias(new uint[] { 0x4, 24, 400 }, Header, ElementSize, out var headerInclusive, out var problem);

		Assert.That(problem, Is.Null);
		Assert.That(headerInclusive, Is.False);
		Assert.That(bias, Is.Zero);
	}

	[Test]
	public void UnresolvedFieldsAreIgnored()
	{
		var bias = CarFieldLayout.ChooseBias(new uint[] { 0, 16, 0, 200 }, Header, ElementSize, out _, out var problem);

		Assert.That(problem, Is.Null);
		Assert.That(bias, Is.EqualTo(Header));
	}

	[Test]
	public void OffsetsThatFitNothingAreRefused()
	{
		var bias = CarFieldLayout.ChooseBias(new uint[] { 16, 100000 }, Header, ElementSize, out _, out var problem);

		Assert.That(problem, Is.Not.Null, "reading from a layout we do not understand is how the crash happened");
		Assert.That(bias, Is.Zero);
	}

	[Test]
	public void NothingResolvedIsAProblem()
	{
		Assert.That(CarFieldLayout.ChooseBias(Array.Empty<uint>(), Header, ElementSize, out _, out var empty), Is.Zero);
		Assert.That(empty, Is.Not.Null);

		Assert.That(CarFieldLayout.ChooseBias(new uint[] { 0, 0 }, Header, ElementSize, out _, out var allZero), Is.Zero);
		Assert.That(allZero, Is.Not.Null);

		Assert.That(CarFieldLayout.ChooseBias(null, Header, ElementSize, out _, out var none), Is.Zero);
		Assert.That(none, Is.Not.Null);
	}

	[Test]
	public void AnImplausibleLayoutIsRefused()
	{
		Assert.That(CarFieldLayout.ChooseBias(new uint[] { 16 }, Header, 0, out _, out var noSize), Is.Zero);
		Assert.That(noSize, Is.Not.Null);
	}
}
