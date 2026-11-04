using System.IO;
using CMS21Together.BotHarness;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Phase 4 exit test (short form; <c>BotHarness --save-kill 200</c> is the full one).</summary>
[TestFixture]
public class SaveKillTests
{
	[Test]
	public void KillingTheGameWhileItSavesAlwaysLeavesALoadableGeneration()
	{
		var exe = FindHarness();
		if (exe == null) Assert.Ignore("Build Tools\\BotHarness first (the test kills real processes).");
		var dir = TestSaves.NewDirectory();
		try
		{
			var result = SaveKill.Run(exe, dir, 12, 21);
			Assert.That(result.Passed, Is.True, result.ToString());
			Assert.That(result.Kills, Is.EqualTo(12));
			Assert.That(result.LastGeneration, Is.GreaterThan(1));
		}
		finally
		{
			TestSaves.DeleteDirectory(dir);
		}
	}

	private static string FindHarness()
	{
		var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CMS21-Together.sln"))) dir = dir.Parent;
		if (dir == null) return null;
		foreach (var configuration in new[] { "Release", "Debug" })
		{
			var exe = Path.Combine(dir.FullName, "Tools", "BotHarness", "bin", configuration, "net472", "BotHarness.exe");
			if (File.Exists(exe)) return exe;
		}
		return null;
	}
}