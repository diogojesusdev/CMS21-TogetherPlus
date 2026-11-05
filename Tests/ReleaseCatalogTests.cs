using System;
using System.IO;
using CMS21Together.Shared.Updates;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>The in-game version picker only lists releases the updater can actually install.</summary>
[TestFixture]
public class ReleaseCatalogTests
{
	private const string Json = @"[
		{""tag_name"":""v0.5.0-rc18"",""prerelease"":true,""draft"":false,""assets"":[{""name"":""TogetherPlus-0.5.0-rc18.zip""},{""name"":""TogetherPlus-0.5.0-rc18.zip.sha256""}]},
		{""tag_name"":""v0.5.1"",""prerelease"":false,""draft"":false,""assets"":[{""name"":""TogetherPlus-0.5.1.zip""},{""name"":""TogetherPlus-0.5.1.zip.sha256""}]},
		{""tag_name"":""v0.5.0"",""prerelease"":false,""draft"":false,""assets"":[{""name"":""TogetherPlus-0.5.0.zip""}]},
		{""tag_name"":""v0.4.9"",""prerelease"":false,""draft"":true,""assets"":[{""name"":""TogetherPlus-0.4.9.zip""},{""name"":""TogetherPlus-0.4.9.zip.sha256""}]},
		{""tag_name"":""latest"",""assets"":[]}
	]";

	[Test]
	public void ListsInstallableReleasesNewestFirst()
	{
		var releases = ReleaseCatalog.Parse(Json);
		Assert.That(releases.Count, Is.EqualTo(2), "drafts, missing sha256 and non-version tags are skipped");
		Assert.That(releases[0].Version, Is.EqualTo("0.5.1"));
		Assert.That(releases[0].PreRelease, Is.False);
		Assert.That(releases[1].Version, Is.EqualTo("0.5.0-rc18"));
		Assert.That(releases[1].PreRelease, Is.True);
		Assert.That(releases[1].HasPackage, Is.True);
	}

	[Test]
	public void EmptyOrMalformedJsonIsHandled()
	{
		Assert.That(ReleaseCatalog.Parse(null), Is.Empty);
		Assert.That(ReleaseCatalog.Parse(""), Is.Empty);
		Assert.Throws<FormatException>(() => ReleaseCatalog.Parse("{not json"));
	}
}