namespace CMS21Together.Shared.Economy;

/// <summary>Where the player is, as far as the wallet cares.</summary>
public enum WalletPlace
{
	/// <summary>Menu, unknown scene, or a scene still loading: money changes there are the game's own bookkeeping.</summary>
	None,
	Garage,

	/// <summary>Junkyard, auto salon, barn: players buy and sell there too.</summary>
	Away
}

/// <summary>
/// When a local money change is this player's own action, and when the game may be given the shared balance
/// (review fix #2: purchases outside the garage were never recorded, so they were free).
/// </summary>
public static class WalletRules
{
	/// <summary>The game shows the shared balance here, and its changes count.</summary>
	public static bool Live(WalletPlace place, bool garageReady) =>
		place == WalletPlace.Away || place == WalletPlace.Garage && garageReady;

	/// <summary>
	/// Before the host's first balance, a player's game shows 0 money and 0 scrap, so the game itself refuses every
	/// purchase (shop, junkyard, salon, barn, upgrades, paint). Phase 8: purchases in that window were free. Never on
	/// the host: its game's money seeds the shared wallet, and its wallet is seeded before its garage is playable.
	/// </summary>
	public static bool HoldAtZero(bool isHost, bool walletUsed, bool synced) => walletUsed && !isHost && !synced;

	/// <summary>Money may be spent now: the wallet is not used (sandbox, no session), or synced and shown.</summary>
	public static bool CanSpend(bool walletUsed, bool synced, bool balanceShown) => !walletUsed || synced && balanceShown;

	/// <summary>A local change is sent as this player's transaction.</summary>
	public static bool Record(bool applyingHostState, bool balanceShown, WalletPlace place, bool garageReady) =>
		!applyingHostState && balanceShown && Live(place, garageReady);
}