using CMS21Together.Shared;
using NUnit.Framework;

namespace CMS21Together.Tests;

public class LogThrottleTests
{
	[Test]
	public void Allow_LimitsEachKeyPerWindowAndCountsTheSkippedOnes()
	{
		var throttle = new LogThrottle(2, 1000);

		Assert.That(throttle.Allow(1, 0, out _), Is.True);
		Assert.That(throttle.Allow(1, 10, out _), Is.True);
		Assert.That(throttle.Allow(1, 20, out _), Is.False);
		Assert.That(throttle.Allow(1, 30, out _), Is.False);
		Assert.That(throttle.Allow(2, 40, out _), Is.True, "keys are independent");

		Assert.That(throttle.Allow(1, 1000, out var skipped), Is.True);
		Assert.That(skipped, Is.EqualTo(2));
		Assert.That(LogThrottle.Skipped(skipped), Is.EqualTo(" (+2 similar skipped)"));
		Assert.That(LogThrottle.Skipped(0), Is.Empty);
	}

	[Test]
	public void Allow_SurvivesTickCountWraparound()
	{
		var throttle = new LogThrottle(1, 1000);
		Assert.That(throttle.Allow(1, int.MaxValue - 10, out _), Is.True);
		Assert.That(throttle.Allow(1, int.MaxValue - 5, out _), Is.False);
		Assert.That(throttle.Allow(1, unchecked(int.MaxValue + 1000), out var skipped), Is.True);
		Assert.That(skipped, Is.EqualTo(1));
	}
}
