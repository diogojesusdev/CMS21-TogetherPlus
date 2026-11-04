namespace CMS21Together.Shared.Persistence;

public enum RecoveryDecision
{
	Ignore,

	/// <summary>Ask the host whether to restore from the player's copy.</summary>
	Prompt
}

/// <summary>A joining player's newest valid copy of the host's save (recovery path 1).</summary>
public sealed class ReplicaOffer
{
	public string SaveId;
	public long Generation;
	public string BundleSha256;
	public long CreatedUtcTicks;
}

/// <summary>Plan §3.5, recovery path 1: when the host is asked to restore from a player's copy.</summary>
public static class RecoveryRules
{
	/// <param name="hostSaveId">Save id of the host's session.</param>
	/// <param name="hostGeneration">Generation of the host's current files.</param>
	/// <param name="hostDamaged">The host's files were missing or damaged when the session loaded.</param>
	/// <param name="alreadyAsked">The host already answered for this generation (no prompt twice).</param>
	public static RecoveryDecision Evaluate(string hostSaveId, long hostGeneration, bool hostDamaged, ReplicaOffer offer, bool alreadyAsked)
	{
		if (offer == null || alreadyAsked) return RecoveryDecision.Ignore;
		if (!SaveIds.IsValid(offer.SaveId) || offer.SaveId != hostSaveId) return RecoveryDecision.Ignore;
		if (offer.Generation <= 0 || !Sha256Hex.IsValid(offer.BundleSha256)) return RecoveryDecision.Ignore;
		if (offer.Generation > hostGeneration) return RecoveryDecision.Prompt;
		return hostDamaged ? RecoveryDecision.Prompt : RecoveryDecision.Ignore;
	}
}