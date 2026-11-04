using CMS21Together.Shared.World;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Audit A12: the fallback reads the whole number at the end of the name, not one character.</summary>
[TestFixture]
public class LoaderNamesTests
{
	[TestCase("CarLoader_1", 0)]
	[TestCase("CarLoader_5", 4)]
	[TestCase("CarLoader_12", 11)]
	[TestCase("CarLoader_", -1)]
	[TestCase("CarLoader", -1)]
	[TestCase("", -1)]
	[TestCase(null, -1)]
	public void IdFromName(string name, int expected) => Assert.That(LoaderNames.IdFromName(name), Is.EqualTo(expected));
}