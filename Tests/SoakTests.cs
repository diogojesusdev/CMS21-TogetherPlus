using CMS21Together.BotHarness;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>Short runs of the bot harness; the hour-long soak is `BotHarness --seconds 3600` (or build.ps1 -Soak).</summary>
[TestFixture]
public class SoakTests
{
	[Test]
	public void EightPlayersOnABadNetworkWithChurn()
	{
		var result = Soak.Run(new SoakOptions
		{
			Players = 8,
			Seconds = 120,
			LatencyMs = 80,
			JitterMs = 20,
			LossRate = 0.05,
			ChurnPerMinute = 2,
			Seed = 11
		});
		TestContext.WriteLine(result);
		Assert.That(result.Errors, Is.Empty);
		Assert.That(result.Leaves, Is.GreaterThan(0), "churn did not happen");
		Assert.That(result.LargeMessages, Is.GreaterThan(0), "no chunked message was sent");
		Assert.That(result.ReliableReceived, Is.GreaterThan(result.ReliableSent));
	}

	[Test]
	public void SlowLinkWithLargeMessages()
	{
		var result = Soak.Run(new SoakOptions
		{
			Players = 8,
			Seconds = 90,
			LatencyMs = 150,
			JitterMs = 50,
			LossRate = 0.1,
			BytesPerSecond = 200_000,
			LargeMessageEverySeconds = 20,
			Seed = 7
		});
		TestContext.WriteLine(result);
		Assert.That(result.Errors, Is.Empty);
		Assert.That(result.LargeMessages, Is.GreaterThan(0));
	}

	[Test]
	public void RealTcpSockets()
	{
		var result = Soak.Run(new SoakOptions
		{
			Players = 8,
			Seconds = 3,
			Transport = SoakTransport.Tcp,
			ChurnPerMinute = 6,
			LargeMessageEverySeconds = 1,
			Seed = 3
		});
		TestContext.WriteLine(result);
		Assert.That(result.Errors, Is.Empty);
		Assert.That(result.ReliableReceived, Is.GreaterThan(0));
	}
}