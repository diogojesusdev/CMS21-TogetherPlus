using System;
using System.Collections;

namespace CMS21Together.Shared.Safety;

/// <summary>
/// Runs one mod action and turns a managed failure into a cancelled action: logged, reported, and optionally rolled
/// back (e.g. ask the host for the car again). A native access violation cannot be caught; the call sites use this
/// after validating their references, so the risky native call is skipped instead.
/// </summary>
public static class Guard
{
	public static void Run(string action, Action body, Action onError = null)
	{
		try
		{
			body?.Invoke();
		}
		catch (Exception ex)
		{
			Fail(action, ex, onError);
		}
	}

	public static T Try<T>(string action, Func<T> body, T fallback = default, Action onError = null)
	{
		try
		{
			return body != null ? body() : fallback;
		}
		catch (Exception ex)
		{
			Fail(action, ex, onError);
			return fallback;
		}
	}

	/// <summary>Wraps an apply coroutine: a throw cancels it (and runs the rollback) instead of killing it silently.</summary>
	public static IEnumerator Coroutine(string action, IEnumerator inner, Action onError = null)
	{
		if (inner == null) yield break;
		while (true)
		{
			object current;
			try
			{
				if (!inner.MoveNext()) yield break;
				current = inner.Current;
			}
			catch (Exception ex)
			{
				Fail(action, ex, onError);
				yield break;
			}
			yield return current;
		}
	}

	private static void Fail(string action, Exception exception, Action onError)
	{
		CrashReport.Note(action);
		CrashReport.Capture(action, exception);
		try
		{
			onError?.Invoke();
		}
		catch (Exception nested)
		{
			Log.Warn($"[Guard] The recovery for {action} failed: {nested.Message}");
		}
	}
}