using System;
using System.Collections.Generic;

namespace CMS21Together.Shared;

/// <summary>
/// Per-key burst limiter for breadcrumb logs: at most <c>max</c> lines per key in each window, then a count of
/// the skipped ones on the next allowed line. Cheap (one dictionary lookup) and main-thread only.
/// </summary>
public sealed class LogThrottle
{
	private readonly int windowMs;
	private readonly int max;
	private readonly Dictionary<int, Window> windows = new();

	private struct Window
	{
		public int Start;
		public int Count;
	}

	public LogThrottle(int max = 5, int windowMs = 5000)
	{
		this.max = Math.Max(1, max);
		this.windowMs = Math.Max(1, windowMs);
	}

	/// <summary>True when a line for <paramref name="key"/> may be written; <paramref name="suppressed"/> counts the ones skipped before it.</summary>
	public bool Allow(int key, out int suppressed) => Allow(key, Environment.TickCount, out suppressed);

	public bool Allow(int key, int nowMs, out int suppressed)
	{
		suppressed = 0;
		if (!windows.TryGetValue(key, out var window) || unchecked(nowMs - window.Start) >= windowMs)
		{
			if (window.Count > max) suppressed = window.Count - max;
			windows[key] = new Window { Start = nowMs, Count = 1 };
			return true;
		}

		window.Count++;
		windows[key] = window;
		return window.Count <= max;
	}

	/// <summary>Suffix for an allowed line that follows skipped ones.</summary>
	public static string Skipped(int suppressed) => suppressed > 0 ? $" (+{suppressed} similar skipped)" : string.Empty;

	public void Clear() => windows.Clear();
}
