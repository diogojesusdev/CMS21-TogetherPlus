using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared;
using NUnit.Framework;

namespace CMS21Together.Tests;

public class SidecarRulesTests
{
	private sealed class User
	{
		public string Id;
		public int Exp;
	}

	private sealed class Info
	{
		public string Id;
		public int Exp;
	}

	private static int Apply(IEnumerable<User> users, List<Info> infos) =>
		SidecarRules.ApplyPlayerStats(users, infos, u => u.Id, i => i.Id, (u, i) => i.Exp = u.Exp);

	[Test]
	public void EveryConnectedPlayerIsSaved_WhenAnEarlierOneHasNoProfile()
	{
		// Audit A17: the old loop stopped at the first player without a profile.
		var infos = new List<Info> { new() { Id = "host" }, new() { Id = "p3" }, new() { Id = "p4" } };
		var connected = new[]
		{
			new User { Id = "host", Exp = 10 },
			new User { Id = "unknown", Exp = 99 },
			new User { Id = "p3", Exp = 30 },
			new User { Id = "p4", Exp = 40 }
		};

		Assert.That(Apply(connected, infos), Is.EqualTo(3));
		Assert.That(infos.Select(i => i.Exp), Is.EqualTo(new[] { 10, 30, 40 }));
	}

	[Test]
	public void APlayerWhoLeft_KeepsTheirSavedProgress()
	{
		var infos = new List<Info> { new() { Id = "host", Exp = 1 }, new() { Id = "gone", Exp = 500 } };

		Apply(new[] { new User { Id = "host", Exp = 2 } }, infos);

		Assert.That(infos[1].Exp, Is.EqualTo(500));
	}

	[Test]
	public void NullsAndEmptyIds_AreSkipped()
	{
		var infos = new List<Info> { null, new() { Id = "a" } };
		var connected = new[] { null, new User { Id = null, Exp = 5 }, new User { Id = "", Exp = 6 }, new User { Id = "a", Exp = 7 } };

		Assert.That(Apply(connected, infos), Is.EqualTo(1));
		Assert.That(infos[1].Exp, Is.EqualTo(7));
		Assert.That(Apply(null, infos), Is.Zero);
		Assert.That(Apply(connected, null), Is.Zero);
	}

	[TestCase(true, 2, 2, ExpectedResult = true)]
	[TestCase(true, 1, 2, ExpectedResult = false)]
	[TestCase(false, 2, 2, ExpectedResult = false)]
	public bool LiveStats_OnlyGoIntoTheSaveBeingPlayed(bool hosting, int saveIndex, int current) =>
		// Audit A21: saving slot X must never write the session into slot Y.
		SidecarRules.AppliesLiveStats(hosting, saveIndex, current);
}