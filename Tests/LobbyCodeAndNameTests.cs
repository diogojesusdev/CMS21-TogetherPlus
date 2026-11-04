using CMS21Together.Shared;
using NUnit.Framework;

namespace CMS21Together.Tests;

public class LobbyCodeAndNameTests
{
	[TestCase(1UL)]
	[TestCase(61UL)]
	[TestCase(62UL)]
	[TestCase(76561198012345678UL)]
	[TestCase(ulong.MaxValue)]
	public void LobbyCodes_RoundTrip(ulong steamId)
	{
		for (var i = 0; i < 20; i++)
		{
			var code = SteamworksUtils.GetServerID(steamId);
			Assert.That(SteamworksUtils.TryConvertServerID(code, out var decoded), Is.True, code);
			Assert.That(decoded, Is.EqualTo(steamId), code);
			Assert.That(SteamworksUtils.TryConvertServerID($"  {code} ", out decoded), Is.True);
		}
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase("A")]
	[TestCase("abc-def")]
	[TestCase("00")]
	[TestCase("zzzzzzzzzzzzzzzzzzzzzzzzz0")]
	public void InvalidLobbyCodes_AreRejected(string code)
	{
		Assert.That(SteamworksUtils.TryConvertServerID(code, out var id), Is.False);
		Assert.That(id, Is.Zero);
	}

	[TestCase("  Bob  ", 2, ExpectedResult = "Bob")]
	[TestCase("<color=red>X</color>", 2, ExpectedResult = "color=redX/color")]
	[TestCase("a\nb\tc", 2, ExpectedResult = "abc")]
	[TestCase(null, 3, ExpectedResult = "Player 3")]
	[TestCase("   ", 4, ExpectedResult = "Player 4")]
	[TestCase("<>", 5, ExpectedResult = "Player 5")]
	public string PlayerNames_AreSanitized(string name, int slot) => PlayerNames.Sanitize(name, slot);

	[Test]
	public void PlayerNames_AreCapped()
	{
		Assert.That(PlayerNames.Sanitize(new string('x', 100), 2).Length, Is.EqualTo(PlayerNames.MaxLength));
	}
}