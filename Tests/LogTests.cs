using System;
using System.Collections.Generic;
using CMS21Together.Shared;
using NUnit.Framework;

namespace CMS21Together.Tests;

[NonParallelizable]
public class LogTests
{
	private sealed class RecordingSink : ILogSink
	{
		public readonly List<(LogLevel Level, string Message)> Entries = new();

		public void Write(LogLevel level, string message) => Entries.Add((level, message));
	}

	private sealed class ThrowingSink : ILogSink
	{
		public void Write(LogLevel level, string message) => throw new InvalidOperationException("sink failure");
	}

	private ILogSink previousSink;
	private LogLevel previousLevel;
	private RecordingSink sink;

	[SetUp]
	public void SetUp()
	{
		previousSink = Log.Sink;
		previousLevel = Log.MinLevel;
		sink = new RecordingSink();
		Log.Sink = sink;
	}

	[TearDown]
	public void TearDown()
	{
		Log.Sink = previousSink;
		Log.MinLevel = previousLevel;
	}

	[Test]
	public void MessagesBelowMinLevel_AreDropped()
	{
		Log.MinLevel = LogLevel.Info;

		Log.Trace("t");
		Log.Debug("d");
		Log.Info("i");
		Log.Warn("w");
		Log.Error("e");

		Assert.That(sink.Entries, Is.EqualTo(new[]
		{
			(LogLevel.Info, "i"),
			(LogLevel.Warn, "w"),
			(LogLevel.Error, "e")
		}));
	}

	[Test]
	public void TraceLevel_LetsEverythingThrough()
	{
		Log.MinLevel = LogLevel.Trace;

		Log.Trace("t");
		Log.Debug("d");

		Assert.That(sink.Entries, Has.Count.EqualTo(2));
		Assert.That(sink.Entries[0].Level, Is.EqualTo(LogLevel.Trace));
		Assert.That(sink.Entries[1].Level, Is.EqualTo(LogLevel.Debug));
	}

	[Test]
	public void OffLevel_DropsEverything()
	{
		Log.MinLevel = LogLevel.Off;

		Log.Error("e");
		Log.Write(LogLevel.Off, "off");

		Assert.That(sink.Entries, Is.Empty);
	}

	[Test]
	public void WritingAtOffLevel_IsNeverEmitted()
	{
		Log.MinLevel = LogLevel.Trace;

		Log.Write(LogLevel.Off, "off");

		Assert.That(sink.Entries, Is.Empty);
		Assert.That(Log.IsEnabled(LogLevel.Off), Is.False);
	}

	[Test]
	public void IsEnabled_FollowsMinLevelAndSink()
	{
		Log.MinLevel = LogLevel.Warn;

		Assert.That(Log.IsEnabled(LogLevel.Info), Is.False);
		Assert.That(Log.IsEnabled(LogLevel.Warn), Is.True);

		Log.Sink = null;
		Assert.That(Log.IsEnabled(LogLevel.Error), Is.False);
	}

	[Test]
	public void NullSink_DoesNotThrow()
	{
		Log.Sink = null;

		Assert.DoesNotThrow(() => Log.Error("e"));
	}

	[Test]
	public void ThrowingSink_IsSwallowed()
	{
		Log.Sink = new ThrowingSink();

		Assert.DoesNotThrow(() => Log.Error("e"));
	}

	[Test]
	public void ErrorWithException_IncludesTheException()
	{
		Log.Error("boom", new InvalidOperationException("details"));

		Assert.That(sink.Entries, Has.Count.EqualTo(1));
		Assert.That(sink.Entries[0].Level, Is.EqualTo(LogLevel.Error));
		Assert.That(sink.Entries[0].Message, Does.StartWith("boom: System.InvalidOperationException: details"));
	}

	[TestCase("Debug", LogLevel.Debug)]
	[TestCase("debug", LogLevel.Debug)]
	[TestCase("  WARN ", LogLevel.Warn)]
	[TestCase("off", LogLevel.Off)]
	[TestCase("Trace", LogLevel.Trace)]
	public void TryParseLevel_AcceptsKnownNames(string value, LogLevel expected)
	{
		Assert.That(Log.TryParseLevel(value, out var level), Is.True);
		Assert.That(level, Is.EqualTo(expected));
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase("   ")]
	[TestCase("Verbose")]
	[TestCase("42")]
	public void TryParseLevel_RejectsUnknownValues_AndFallsBackToInfo(string value)
	{
		Assert.That(Log.TryParseLevel(value, out var level), Is.False);
		Assert.That(level, Is.EqualTo(LogLevel.Info));
	}
}
