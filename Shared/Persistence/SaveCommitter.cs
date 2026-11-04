using System;
using System.Threading.Tasks;

namespace CMS21Together.Shared.Persistence;

/// <summary>The files of the save being written, as the committer sees them (the game's slot, or a fake in tests).</summary>
public interface ISaveSlot
{
	string SaveId { get; }
	string Name { get; }

	/// <summary>Generation currently stamped in the sidecar.</summary>
	long Generation { get; }

	/// <summary>
	/// Stamps the sidecar with a generation and the profile's hash (null = not known yet), captures the current
	/// progress and writes it atomically. Returns the bytes written, or null when the save no longer exists.
	/// </summary>
	byte[] WriteSidecar(long generation, string profileSha256);

	/// <summary>Contents of the profile file, or null when it does not exist.</summary>
	byte[] ReadProfile(out byte format);

	/// <summary>Last write time and size of the profile file, or null when it does not exist.</summary>
	(DateTime writtenUtc, long length)? ProfileFileState { get; }
}

public sealed class CommittedSave
{
	public CommittedSave(BundleMeta meta, byte[] bundle, string path)
	{
		Meta = meta;
		Bundle = bundle;
		Path = path;
	}

	public BundleMeta Meta { get; }
	public byte[] Bundle { get; }

	/// <summary>Where the backup was stored.</summary>
	public string Path { get; }
}

/// <summary>
/// Two-step save commit (plan §3.5). The game writes its profile asynchronously after the mod's save hook, so:
/// <list type="number">
/// <item><see cref="Begin"/> (in the hook) writes the sidecar as generation N with the profile hash pending;</item>
/// <item><see cref="Pump"/> waits until the game's write has finished and the file is stable, hashes the profile,
/// stamps the sidecar with that hash, and writes the bundle of generation N to the backups (in the background).</item>
/// </list>
/// A crash between the steps leaves a sidecar without hash, which load validation handles. Main thread only.
/// </summary>
public sealed class SaveCommitter
{
	/// <summary>Wait this long for the game's write to start before using the file as it is.</summary>
	public const long SettleMs = 3_000;

	/// <summary>The profile must not change for this long before it is hashed.</summary>
	public const long StableMs = 500;

	/// <summary>Give up (the generation stays unverified) when the game is still saving after this long.</summary>
	public const long MaxWaitMs = 60_000;

	private readonly BundleStore backups;
	private readonly BundleStore kept;
	private readonly Func<long> clock;
	private readonly bool inline;
	private Pending pending;
	private Task<CommittedSave> writing;

	/// <param name="inline">Write bundles on the calling thread (tests and the save harness).</param>
	/// <param name="kept">Where a restore moves newer backups; its generations are never reused (review fix #4).</param>
	public SaveCommitter(BundleStore backups, Func<long> clockMs, bool inline = false, BundleStore kept = null)
	{
		this.backups = backups ?? throw new ArgumentNullException(nameof(backups));
		this.kept = kept;
		clock = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
		this.inline = inline;
	}

	/// <summary>A generation was fully written (sidecar stamped, backup stored). Raised from <see cref="Pump"/>.</summary>
	public event Action<CommittedSave> Committed;

	public bool Idle => pending == null && writing == null;

	/// <summary>Generation waiting for the game's write (0 when none).</summary>
	public long PendingGeneration => pending?.Generation ?? 0;

	public string PendingSaveId => pending?.SaveId;

	/// <summary>Step 1: writes the sidecar as the next generation, hash pending. Returns the generation, or 0 on failure.</summary>
	public long Begin(ISaveSlot slot)
	{
		if (slot == null || !SaveIds.IsValid(slot.SaveId))
		{
			Log.Warn("[Saves] Not saving: the save has no valid id.");
			return 0;
		}

		long generation;
		var sawSave = false;
		if (pending != null && pending.SaveId == slot.SaveId)
		{
			// The game's save calls more than one hook: they are the same generation.
			generation = pending.Generation;
			sawSave = pending.SawSave;
		}
		else
		{
			if (pending != null) Log.Warn($"[Saves] Generation {pending.Generation} of another save was not finished; it stays unverified.");
			generation = Math.Max(slot.Generation, NewestBackup(slot.SaveId)) + 1;
		}

		byte[] written;
		try
		{
			written = slot.WriteSidecar(generation, null);
		}
		catch (Exception ex)
		{
			Log.Error("[Saves] Writing the mod save failed", ex);
			written = null;
		}
		if (written == null)
		{
			pending = null;
			return 0;
		}

		var now = clock();
		pending = new Pending(slot, generation, now, slot.ProfileFileState) { SawSave = sawSave, LastChangeMs = now };
		Log.Debug($"[Saves] Generation {generation} started.");
		return generation;
	}

	/// <summary>Step 2, every frame. <paramref name="gameSaveInProgress"/>: the game is still writing its profile.</summary>
	public void Pump(bool gameSaveInProgress)
	{
		CollectWriting();
		var p = pending;
		if (p == null) return;
		var now = clock();
		if (gameSaveInProgress)
		{
			p.SawSave = true;
			p.LastChangeMs = now;
			if (now - p.StartedMs > MaxWaitMs)
			{
				Log.Warn($"[Saves] The game is still saving after {MaxWaitMs / 1000} s; generation {p.Generation} stays unverified.");
				pending = null;
			}
			return;
		}

		var state = p.Slot.ProfileFileState;
		if (!Equals(state, p.LastState))
		{
			p.LastState = state;
			p.LastChangeMs = now;
		}
		var touched = state.HasValue && !Equals(state, p.StateAtBegin);
		if (!p.SawSave && !touched && now - p.StartedMs < SettleMs) return;
		if (now - p.LastChangeMs < StableMs) return;
		if (writing != null) return;

		pending = null;
		Finish(p);
	}

	/// <summary>Waits for the background write (tests, shutdown) and raises <see cref="Committed"/>.</summary>
	public void WaitForWrites(int timeoutMs = 30_000)
	{
		var task = writing;
		if (task == null) return;
		try
		{
			task.Wait(timeoutMs);
		}
		catch (AggregateException)
		{
			// Reported by CollectWriting.
		}
		CollectWriting();
	}

	private void Finish(Pending p)
	{
		byte[] profile;
		byte format;
		try
		{
			profile = p.Slot.ReadProfile(out format);
		}
		catch (Exception ex)
		{
			Log.Error($"[Saves] Reading the profile of generation {p.Generation} failed", ex);
			return;
		}
		if (profile == null || profile.Length == 0)
		{
			Log.Warn($"[Saves] The profile is missing; generation {p.Generation} stays unverified.");
			return;
		}
		if (profile.Length > SaveBundle.MaxProfileBytes)
		{
			Log.Error($"[Saves] The profile is larger than {SaveBundle.MaxProfileBytes} bytes; generation {p.Generation} is not backed up.");
			return;
		}

		var sha = Sha256Hex.Of(profile);
		byte[] sidecar;
		try
		{
			sidecar = p.Slot.WriteSidecar(p.Generation, sha);
		}
		catch (Exception ex)
		{
			Log.Error($"[Saves] Stamping generation {p.Generation} failed", ex);
			return;
		}
		if (sidecar == null) return;

		var name = p.Slot.Name ?? string.Empty;
		var meta = new BundleMeta
		{
			SaveId = p.SaveId,
			Generation = p.Generation,
			CreatedUtc = DateTime.UtcNow,
			Name = name.Length > SaveBundle.MaxTextLength ? name.Substring(0, SaveBundle.MaxTextLength) : name,
			ProfileFormat = format,
			Schema = SlotValidator.CurrentSchema
		};
		CommittedSave Work()
		{
			var bytes = SaveBundle.Encode(meta, profile, sidecar);
			return new CommittedSave(meta, bytes, backups.Put(bytes));
		}

		if (!inline)
		{
			writing = Task.Run(Work);
			return;
		}
		try
		{
			Raise(Work());
		}
		catch (Exception ex)
		{
			Log.Error($"[Saves] Writing the backup of generation {p.Generation} failed", ex);
		}
	}

	private void CollectWriting()
	{
		var task = writing;
		if (task == null || !task.IsCompleted) return;
		writing = null;
		if (task.IsFaulted || task.IsCanceled)
		{
			Log.Error("[Saves] Writing the backup failed", task.Exception?.GetBaseException());
			return;
		}
		Raise(task.Result);
	}

	private void Raise(CommittedSave save)
	{
		Log.Info($"[Saves] Saved generation {save.Meta.Generation} of \"{save.Meta.Name}\".");
		try
		{
			Committed?.Invoke(save);
		}
		catch (Exception ex)
		{
			Log.Error("[Saves] A save listener failed", ex);
		}
	}

	private long NewestBackup(string saveId)
	{
		try
		{
			// After a restore, the newer backups moved to "kept": new saves continue above them, so a generation
			// number never names two different saves (players' copies are matched by generation).
			return Math.Max(backups.NewestGeneration(saveId), kept?.NewestGeneration(saveId) ?? 0);
		}
		catch (Exception ex)
		{
			Log.Warn($"[Saves] Could not list the backups: {ex.Message}");
			return 0;
		}
	}

	private sealed class Pending
	{
		public Pending(ISaveSlot slot, long generation, long startedMs, (DateTime, long)? stateAtBegin)
		{
			Slot = slot;
			SaveId = slot.SaveId;
			Generation = generation;
			StartedMs = startedMs;
			StateAtBegin = stateAtBegin;
			LastState = stateAtBegin;
		}

		public ISaveSlot Slot { get; }
		public string SaveId { get; }
		public long Generation { get; }
		public long StartedMs { get; }
		public (DateTime, long)? StateAtBegin { get; }
		public (DateTime, long)? LastState { get; set; }
		public long LastChangeMs { get; set; }
		public bool SawSave { get; set; }
	}
}