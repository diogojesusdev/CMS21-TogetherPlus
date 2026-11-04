using System;

namespace CMS21Together.Shared;

public enum LogLevel
{
	Trace,
	Debug,
	Info,
	Warn,
	Error,
	Off
}

public interface ILogSink
{
	void Write(LogLevel level, string message);
}

/// <summary>
/// Single logging gateway with levels. Messages below <see cref="MinLevel"/> are dropped before
/// reaching the sink. Guard expensive messages with <see cref="IsEnabled"/>.
/// Pure .NET so shared code stays testable without MelonLoader.
/// </summary>
public static class Log
{
	private static volatile ILogSink sink;

	public static LogLevel MinLevel { get; set; } = LogLevel.Info;

	public static ILogSink Sink
	{
		get => sink;
		set => sink = value;
	}

	public static bool IsEnabled(LogLevel level) => level != LogLevel.Off && level >= MinLevel && sink != null;

	public static void Trace(string message) => Write(LogLevel.Trace, message);
	public static void Debug(string message) => Write(LogLevel.Debug, message);
	public static void Info(string message) => Write(LogLevel.Info, message);
	public static void Warn(string message) => Write(LogLevel.Warn, message);
	public static void Error(string message) => Write(LogLevel.Error, message);
	public static void Error(string message, Exception exception) => Write(LogLevel.Error, $"{message}: {exception}");

	public static void Write(LogLevel level, string message)
	{
		if (!IsEnabled(level)) return;
		try
		{
			sink?.Write(level, message);
		}
		catch
		{
			// Logging must never break the caller.
		}
	}

	public static bool TryParseLevel(string value, out LogLevel level)
	{
		if (!string.IsNullOrWhiteSpace(value)
		    && Enum.TryParse(value.Trim(), true, out level)
		    && Enum.IsDefined(typeof(LogLevel), level))
			return true;

		level = LogLevel.Info;
		return false;
	}
}
