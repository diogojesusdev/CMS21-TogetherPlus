namespace CMS21Together.Shared.Data.Vanilla.Cars;

/// <summary>
/// When a fluid level the game just wrote is this player's own doing, and may be sent to the other players.
/// <para>
/// Loading a car writes every one of its fluid levels through <c>FluidData.SetLevel</c>. A player walking back into
/// the garage therefore used to broadcast the car's load-time levels, reverting the work of whoever was draining or
/// filling that car. A car's fluids travel inside the car payload itself, so nothing is lost by staying quiet while
/// it loads.
/// </para>
/// </summary>
public static class FluidSendRules
{
	/// <param name="connected">A session is running.</param>
	/// <param name="applyingHostState">The host's state is being applied here right now.</param>
	/// <param name="suppressed">Inside the frame of an applied fluid packet.</param>
	/// <param name="gameReady">The garage finished loading.</param>
	/// <param name="inGarage">The player is in the garage (junkyard and salon cars reuse the loader ids).</param>
	/// <param name="carTracked">The car loader is a car the mod follows.</param>
	/// <param name="carReady">That car finished loading here (its parts were referenced).</param>
	/// <param name="carNeedsResync">That car is the copy left over from before a scene change.</param>
	public static bool ShouldSend(bool connected, bool applyingHostState, bool suppressed, bool gameReady,
		bool inGarage, bool carTracked, bool carReady, bool carNeedsResync) =>
		connected && !applyingHostState && !suppressed && gameReady && inGarage &&
		carTracked && carReady && !carNeedsResync;
}
