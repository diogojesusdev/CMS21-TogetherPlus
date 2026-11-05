using System;
using System.Collections;
using System.IO;
using CMS21Together.Shared.Safety;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Crash containment: a failed action is reported, not thrown further; the report is written and copyable.</summary>
[TestFixture]
public class CrashReportTests
{
	private string directory;

	[SetUp]
	public void SetUp()
	{
		directory = Path.Combine(Path.GetTempPath(), "together-crash-" + Guid.NewGuid().ToString("N"));
		CrashReport.DirectoryOverride = directory;
		CrashReport.Enabled = true;
	}

	[TearDown]
	public void TearDown()
	{
		CrashReport.Enabled = false;
		CrashReport.DirectoryOverride = null;
		try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
		catch { }
	}

	[Test]
	public void CaptureWritesTheReportAndRaisesIt()
	{
		string seen = null;
		Action<string> handler = text => seen = text;
		CrashReport.Captured += handler;
		try
		{
			var text = CrashReport.Capture("apply part", new InvalidOperationException("boom"), "car 3");
			Assert.That(text, Does.Contain("apply part"));
			Assert.That(text, Does.Contain("boom"));
			Assert.That(text, Does.Contain("car 3"));
			Assert.That(seen, Is.EqualTo(text));
			Assert.That(CrashReport.LastReport, Is.EqualTo(text));
			Assert.That(Directory.GetFiles(directory, "crash-*.txt"), Has.Length.EqualTo(1));
			Assert.That(CrashReport.LatestReport, Does.Contain("apply part"));
		}
		finally
		{
			CrashReport.Captured -= handler;
		}
	}

	[Test]
	public void GuardRunsTheRollbackInsteadOfThrowing()
	{
		var rolledBack = false;
		Assert.DoesNotThrow(() => Guard.Run("part mount", () => throw new InvalidOperationException("nope"), () => rolledBack = true));
		Assert.That(rolledBack, Is.True);
		Assert.That(CrashReport.LastReport, Does.Contain("nope"));
	}

	[Test]
	public void GuardTryReturnsTheFallback()
	{
		var value = Guard.Try("lookup", () => throw new InvalidOperationException("missing"), 42);
		Assert.That(value, Is.EqualTo(42));
	}

	[Test]
	public void GuardCoroutineStopsAtTheThrowAndRollsBack()
	{
		var rolledBack = false;
		var routine = Guard.Coroutine("apply", ThrowingCoroutine(), () => rolledBack = true);
		Assert.DoesNotThrow(() => { while (routine.MoveNext()) { } });
		Assert.That(rolledBack, Is.True);
		Assert.That(CrashReport.LastReport, Does.Contain("iter"));
	}

	[Test]
	public void DisabledReportsStillBuildTheTextButWriteNothing()
	{
		CrashReport.Enabled = false;
		var text = CrashReport.Capture("headless", new InvalidOperationException("quiet"));
		Assert.That(text, Does.Contain("quiet"));
		Assert.That(Directory.Exists(directory), Is.False);
	}

	private static IEnumerator ThrowingCoroutine()
	{
		yield return null;
		throw new InvalidOperationException("iter");
	}
}