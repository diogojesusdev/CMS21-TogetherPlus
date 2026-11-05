using System.IO;
using CMS21Together.Shared;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// Together+ keeps its data next to the vanilla saves, outside the game folder, so verifying or reinstalling the
/// game never loses a backup. A wrong root writes saves somewhere nobody looks, so the location is pinned.
/// </summary>
[TestFixture]
public class PlusPathsTests
{
	[Test]
	public void TheDataFolderSitsUnderTheVanillaSaveFolder()
	{
		StringAssert.Contains("Red Dot Games", PlusPaths.GameData);
		StringAssert.Contains("Car Mechanic Simulator 2021", PlusPaths.GameData);
		Assert.That(PlusPaths.Root, Is.EqualTo(Path.Combine(PlusPaths.GameData, "TogetherPlus")));
	}

	[Test]
	public void ThePathIsAlreadyExpanded()
	{
		// The game reads this before anything else; a literal "%USERPROFILE%" would create a folder named that.
		Assert.That(PlusPaths.GameData, Does.Not.Contain("%"));
		Assert.That(Path.IsPathRooted(PlusPaths.Root), Is.True);
	}
}
