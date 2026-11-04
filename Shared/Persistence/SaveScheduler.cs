using System;

namespace CMS21Together.Shared.Persistence;

public enum SaveAction
{
	None,

	/// <summary>Save everything through the game (profile and sidecar, a new generation).</summary>
	Full,

	/// <summary>Write only the mod sidecar (players' progress), keeping the current generation.</summary>
	SidecarOnly
}

/// <summary>
/// When the host saves (plan §3.5, audit A18): every <see cref="IntervalMs"/>, and shortly after a player leaves.
/// Progress changes such as skill unlocks only mark the save dirty; they never write on their own.
/// Full saves wait until the game can save (host in the garage, nothing loading). Main thread only.
/// </summary>
public sealed class SaveScheduler
{
	public const long DefaultIntervalMs = 5 * 60_000;

	/// <summary>Players leaving within this window are saved together.</summary>
	public const long LeaveDebounceMs = 2_000;

	/// <summary>A leave this soon after a full save only writes the sidecar.</summary>
	public const long MinGapMs = 30_000;

	/// <summary>A full save that did not happen (the game was busy) is asked for again after this long.</summary>
	public const long RetryMs = 10_000;

	private readonly Func<long> clock;
	private long lastFullMs;
	private long leaveDueMs = -1;
	private long retryAtMs;
	private bool fullPending;

	public SaveScheduler(Func<long> clockMs, long intervalMs = DefaultIntervalMs)
	{
		clock = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
		IntervalMs = intervalMs;
		lastFullMs = clock();
	}

	/// <summary>0 or less disables autosave (leaves still save).</summary>
	public long IntervalMs { get; set; }

	/// <summary>Progress changed since the last write (A18).</summary>
	public bool Dirty { get; private set; }

	/// <summary>A new session starts: the next autosave is one interval away.</summary>
	public void Reset()
	{
		lastFullMs = clock();
		leaveDueMs = -1;
		retryAtMs = 0;
		fullPending = false;
		Dirty = false;
	}

	public void MarkDirty() => Dirty = true;

	public void PlayerLeft()
	{
		if (leaveDueMs < 0) leaveDueMs = clock() + LeaveDebounceMs;
	}

	/// <summary>A full save happened (ours or the game's own).</summary>
	public void FullSaveDone()
	{
		lastFullMs = clock();
		retryAtMs = 0;
		fullPending = false;
		Dirty = false;
	}

	public void SidecarSaved() => Dirty = false;

	/// <param name="canFullSave">The game can save now (host in the garage, game ready, no scene loading).</param>
	public SaveAction Poll(bool canFullSave)
	{
		var now = clock();
		if (leaveDueMs >= 0 && now >= leaveDueMs)
		{
			leaveDueMs = -1;
			if (canFullSave && now - lastFullMs >= MinGapMs && now >= retryAtMs) return AskFull(now);
			// Keep the leaver's progress now; the rest of the game is saved as soon as the game can save.
			if (!canFullSave) fullPending = true;
			return SaveAction.SidecarOnly;
		}

		if (!canFullSave || now < retryAtMs) return SaveAction.None;
		if (fullPending || IntervalMs > 0 && now - lastFullMs >= IntervalMs) return AskFull(now);
		return SaveAction.None;
	}

	private SaveAction AskFull(long now)
	{
		retryAtMs = now + RetryMs;
		return SaveAction.Full;
	}
}