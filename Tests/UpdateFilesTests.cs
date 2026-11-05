using System;
using System.IO;
using CMS21Together.Shared.Updates;
using NUnit.Framework;

namespace CMS21Together.Tests;

[TestFixture]
public class VersionTextTests
{
	[TestCase("v0.5.0-rc12", "0.5.0-rc12")]
	[TestCase("0.5.0-rc12", "0.5.0-rc12")]
	[TestCase(" V0.5.1 ", "0.5.1")]
	[TestCase("v0.5.0", "0.5.0")]
	public void ReadsVersionsOutOfReleaseTags(string tag, string expected) =>
		Assert.AreEqual(expected, VersionText.FromTag(tag));

	[TestCase(null)]
	[TestCase("")]
	[TestCase("latest")]
	[TestCase("v1.2")]
	[TestCase("TogetherPlus-0.5.0.zip")]
	public void RejectsWhatIsNotAVersion(string tag) => Assert.IsNull(VersionText.FromTag(tag));

	[Test]
	public void OrderIsNumeric() =>
		Assert.Multiple(() =>
		{
			Assert.Less(VersionText.Compare("0.5.0-rc9", "0.5.0-rc10"), 0);
			Assert.Less(VersionText.Compare("0.5.0-rc11", "0.5.0-rc12"), 0);
			Assert.AreEqual(0, VersionText.Compare("0.5.0-rc12", "v0.5.0-rc12"));
			Assert.Greater(VersionText.Compare("0.5.1", "0.5.0-rc12"), 0);
			Assert.Greater(VersionText.Compare("0.6.0", "0.5.9"), 0);
			Assert.Less(VersionText.Compare("0.5.0-rc12", "0.5.0"), 0);
			Assert.Less(VersionText.Compare("0.6.0-alpha1", "0.6.0-beta2"), 0);
		});

	[Test]
	public void NewerAndEqualHelpersMatchCompare() =>
		Assert.Multiple(() =>
		{
			Assert.IsTrue(VersionText.IsNewer("0.5.0-rc12", "0.5.0-rc11"));
			Assert.IsFalse(VersionText.IsNewer("0.5.0-rc11", "0.5.0-rc11"));
			Assert.IsTrue(VersionText.AreEqual("v0.5.0-rc11", "0.5.0-rc11"));
		});

	[TestCase("Mod version mismatch: host has TogetherPlus-0.6.0, you have TogetherPlus-0.5.0-rc16.", "0.6.0")]
	[TestCase("Mod version mismatch: host has TogetherPlus-0.5.0-rc12, you have TogetherPlus-0.5.0-rc11.", "0.5.0-rc12")]
	[TestCase("Mod version mismatch: host has TogetherPlus 0.6.1, you have TogetherPlus 0.6.0.", "0.6.1")]
	[TestCase("Mod version mismatch: host has Together+ 0.5.0-rc12 (Together 0.4.16hf3), you have Together+ 0.5.0-rc11 (Together 0.4.16hf3).", "0.5.0-rc12")]
	[TestCase("host has 0.7.1", "0.7.1")]
	public void FindsTheHostVersionInARejection(string reason, string expected) =>
		Assert.AreEqual(expected, VersionText.HostVersionIn(reason));

	[TestCase(null)]
	[TestCase("Server is full (8 players).")]
	[TestCase("Incompatible Together+ version (host protocol 1, yours 2). Install the same mod version as the host.")]
	public void IgnoresReasonsWithoutAVersion(string reason) => Assert.IsNull(VersionText.HostVersionIn(reason));
}

[TestFixture]
public class UpdateFilesTests
{
	private string root;

	[SetUp]
	public void CreateTemporaryGame()
	{
		root = Path.Combine(Path.GetTempPath(), "TogetherPlus-update-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(root, "Mods"));
	}

	[TearDown]
	public void RemoveTemporaryGame()
	{
		try
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
		catch (IOException)
		{
		}
	}

	[Test]
	public void InstalledVersionIsNextToTheMod()
	{
		Assert.AreEqual(Path.Combine(root, "Mods", "CMS21-Together.version"), UpdateFiles.InstalledVersionPath(root));
		Assert.IsNull(UpdateFiles.ReadInstalledVersion(root));

		UpdateFiles.WriteInstalledVersion(root, "0.5.0-rc12");
		Assert.AreEqual("0.5.0-rc12", UpdateFiles.ReadInstalledVersion(root));
	}

	[Test]
	public void MarkersRoundTripUnderThePlusRoot()
	{
		var plus = Path.Combine(root, "Plus");
		Assert.IsNull(UpdateFiles.ReadPending(plus));
		Assert.IsNull(UpdateFiles.ReadRequest(plus));
		Assert.IsNull(UpdateFiles.ReadStarted(plus));

		UpdateFiles.WritePending(plus, "0.5.0-rc12");
		UpdateFiles.WriteRequest(plus, "0.5.0-rc11");
		UpdateFiles.WriteStarted(plus, "0.5.0-rc12");

		Assert.AreEqual("0.5.0-rc12", UpdateFiles.ReadPending(plus));
		Assert.AreEqual("0.5.0-rc11", UpdateFiles.ReadRequest(plus));
		Assert.IsTrue(UpdateFiles.WasStarted(plus, "0.5.0-rc12"));
		Assert.IsFalse(UpdateFiles.WasStarted(plus, "0.5.0-rc11"));

		UpdateFiles.ClearPending(plus);
		UpdateFiles.ClearRequest(plus);
		Assert.IsNull(UpdateFiles.ReadPending(plus));
		Assert.IsNull(UpdateFiles.ReadRequest(plus));
	}

	[Test]
	public void PinnedVersionRoundTripsUnderThePlusRoot()
	{
		var plus = Path.Combine(root, "Plus");
		Assert.IsNull(UpdateFiles.ReadPinned(plus), "no pin means latest");

		UpdateFiles.WritePinned(plus, "0.5.0-rc18");
		Assert.AreEqual("0.5.0-rc18", UpdateFiles.ReadPinned(plus));
		Assert.That(File.Exists(UpdateFiles.PinnedPath(plus)), Is.True);

		UpdateFiles.ClearPinned(plus);
		Assert.IsNull(UpdateFiles.ReadPinned(plus));
		Assert.That(File.Exists(UpdateFiles.PinnedPath(plus)), Is.False);
	}

	[Test]
	public void FailedVersionsAreKeptOnce()
	{
		var plus = Path.Combine(root, "Plus");
		UpdateFiles.MarkFailed(plus, "0.5.0-rc12");
		UpdateFiles.MarkFailed(plus, "0.5.0-rc12");
		UpdateFiles.MarkFailed(plus, "v0.5.0-rc13");

		Assert.IsTrue(UpdateFiles.HasFailed(plus, "0.5.0-rc12"));
		Assert.IsTrue(UpdateFiles.HasFailed(plus, "v0.5.0-rc13"));
		Assert.IsFalse(UpdateFiles.HasFailed(plus, "0.5.0-rc11"));
		Assert.AreEqual(2, UpdateFiles.ReadFailed(plus).Count);
	}

	[Test]
	public void AVersionThatStartsIsNoLongerFailed()
	{
		var plus = Path.Combine(root, "Plus");
		UpdateFiles.MarkFailed(plus, "0.5.0-rc12");
		UpdateFiles.MarkFailed(plus, "0.5.0-rc13");

		UpdateFiles.ClearFailed(plus, "v0.5.0-rc12");

		Assert.IsFalse(UpdateFiles.HasFailed(plus, "0.5.0-rc12"));
		Assert.IsTrue(UpdateFiles.HasFailed(plus, "0.5.0-rc13"));
	}

	[Test]
	public void AGarbageMarkerIsNotAVersion()
	{
		var plus = Path.Combine(root, "Plus");
		Directory.CreateDirectory(UpdateFiles.StateDirectory(plus));
		File.WriteAllText(UpdateFiles.PendingPath(plus), "half-written");
		Assert.IsNull(UpdateFiles.ReadPending(plus));
	}

	[Test]
	public void WritingAnEmptyVersionIsRejected() =>
		Assert.Throws<ArgumentNullException>(() => UpdateFiles.WriteInstalledVersion(root, null));
}
