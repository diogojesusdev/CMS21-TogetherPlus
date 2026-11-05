using System;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// The handshake is read before any version check, so a malformed hello must fail fast and with a readable reason;
/// an hello from another protocol must stop parsing right after the protocol field, so the peer gets a version
/// mismatch instead of a format error (that message is what the auto-update uses to find the host's version).
/// </summary>
[TestFixture]
public class NetProtocolTests
{
	private static readonly byte[] HostKey = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

	private static NetFrame Frame(byte[] payload)
	{
		var frame = FrameCodec.Encode(NetFrameTypes.Hello, FrameFlags.None, 0, payload);
		return FrameCodec.Decode(frame, 0, frame.Length);
	}

	[Test]
	public void AHelloKeepsTheVersionAndTheHostKey()
	{
		var hello = NetProtocol.DecodeHello(Frame(NetProtocol.EncodeHello("0.5.0-rc19", HostKey)));

		Assert.That(hello.Protocol, Is.EqualTo(NetProtocol.Version));
		Assert.That(hello.ModVersion, Is.EqualTo("0.5.0-rc19"));
		Assert.That(hello.HostKey, Is.EqualTo(HostKey));
	}

	[Test]
	public void AClientWithNoVersionAndNoKeyStillSendsAHello()
	{
		var hello = NetProtocol.DecodeHello(Frame(NetProtocol.EncodeHello(null, null)));

		Assert.That(hello.ModVersion, Is.Empty);
		Assert.That(hello.HostKey, Is.Empty);
	}

	[Test]
	public void AnotherMagicIsRejectedBeforeAnythingElseIsRead()
	{
		using var writer = new NetWriter();
		writer.WriteUInt32(NetProtocol.Magic + 1);
		writer.WriteUInt16(NetProtocol.Version);
		writer.WriteString("0.5.0-rc19");

		var error = Assert.Throws<NetFormatException>(() => NetProtocol.DecodeHello(Frame(writer.ToArray())));
		Assert.That(error.Message, Does.Contain("Together+"));
	}

	[Test]
	public void AnotherProtocolStopsAfterTheProtocolField()
	{
		// The rest of the layout may differ in another protocol: parsing it would turn a readable version mismatch
		// into a format error, and the client would never learn the host's version.
		var full = NetProtocol.EncodeHello("0.5.0-rc19", HostKey, NetProtocol.Version + 1);
		var truncated = new byte[6];
		Array.Copy(full, truncated, truncated.Length);

		var hello = NetProtocol.DecodeHello(Frame(truncated));

		Assert.That(hello.Protocol, Is.EqualTo(NetProtocol.Version + 1));
		Assert.That(hello.ModVersion, Is.Null);
		Assert.That(hello.HostKey, Is.Null);
	}

	[TestCase(1)]
	[TestCase(5)]
	[TestCase(NetProtocol.HostKeyLength + 1)]
	[TestCase(255)]
	public void AHostKeyOfTheWrongLengthIsRejected(int length)
	{
		using var writer = new NetWriter();
		writer.WriteUInt32(NetProtocol.Magic);
		writer.WriteUInt16(NetProtocol.Version);
		writer.WriteString("0.5.0-rc19");
		writer.WriteByte((byte)length);
		writer.WriteBytes(new byte[Math.Min(length, 255)]);

		Assert.Throws<NetFormatException>(() => NetProtocol.DecodeHello(Frame(writer.ToArray())));
	}

	[Test]
	public void AWelcomeRoundTrips()
	{
		var (slot, max) = NetProtocol.DecodeWelcome(Frame(NetProtocol.EncodeWelcome(3, 8)));
		Assert.That((slot, max), Is.EqualTo((3, 8)));
	}

	[TestCase(0, 4)]
	[TestCase(-1, 4)]
	[TestCase(5, 4)]
	[TestCase(1, 0)]
	[TestCase(1, 65)]
	public void AnImpossibleWelcomeIsRejected(int slot, int max)
	{
		Assert.Throws<NetFormatException>(() => NetProtocol.DecodeWelcome(Frame(NetProtocol.EncodeWelcome(slot, max))));
	}

	[Test]
	public void AReasonIsClampedToWhatTheFieldCanCarry()
	{
		var reason = new string('x', NetProtocol.MaxReasonLength * 3);
		var decoded = NetProtocol.DecodeReason(Frame(NetProtocol.EncodeReason(reason)));
		Assert.That(decoded, Has.Length.EqualTo(NetProtocol.MaxReasonLength));
	}

	[TestCase(null)]
	[TestCase("")]
	public void NoReasonDecodesAsNull(string reason)
	{
		Assert.That(NetProtocol.DecodeReason(Frame(NetProtocol.EncodeReason(reason))), Is.Null);
	}

	[TestCase(long.MinValue)]
	[TestCase(-1L)]
	[TestCase(0L)]
	[TestCase(1_700_000_000_000L)]
	[TestCase(long.MaxValue)]
	public void ATimestampKeepsEveryBit(long ms)
	{
		Assert.That(NetProtocol.DecodeTimestamp(Frame(NetProtocol.EncodeTimestamp(ms))), Is.EqualTo(ms));
	}

	[Test]
	public void TheHostKeyIsComparedExactly()
	{
		Assert.That(NetProtocol.KeyEquals(HostKey, (byte[])HostKey.Clone()), Is.True);

		var flipped = (byte[])HostKey.Clone();
		flipped[HostKey.Length - 1] ^= 1;
		Assert.That(NetProtocol.KeyEquals(HostKey, flipped), Is.False, "one bit matters");

		Assert.That(NetProtocol.KeyEquals(HostKey, new byte[NetProtocol.HostKeyLength - 1]), Is.False);
		Assert.That(NetProtocol.KeyEquals(null, HostKey), Is.False);
		Assert.That(NetProtocol.KeyEquals(HostKey, null), Is.False);
		Assert.That(NetProtocol.KeyEquals(null, null), Is.False);
		Assert.That(NetProtocol.KeyEquals(Array.Empty<byte>(), Array.Empty<byte>()), Is.False, "no key never matches");
	}

	[Test]
	public void APeerSilentForTheTimeoutIsGone()
	{
		var clock = new NetProtocol.StallClock();
		long now = 10_000;
		clock.Tick(now);
		long lastReceive = now;

		Assert.That(clock.TimedOut(now + NetProtocol.TimeoutMs, lastReceive), Is.False, "the window is exclusive");
		Assert.That(clock.TimedOut(now + NetProtocol.TimeoutMs + 1, lastReceive), Is.True);
	}

	[Test]
	public void OurOwnStallGivesEveryPeerTheFullWindowBack()
	{
		var clock = new NetProtocol.StallClock();
		long now = 10_000;
		clock.Tick(now);
		long lastReceive = now;

		// Loading a save on the main thread: no tick for longer than the stall threshold.
		now += NetProtocol.StallThresholdMs * 2;
		clock.Tick(now);

		Assert.That(clock.TimedOut(now, lastReceive), Is.False, "silence is measured from when we resumed");
		Assert.That(clock.TimedOut(now + NetProtocol.TimeoutMs, lastReceive), Is.False);
		Assert.That(clock.TimedOut(now + NetProtocol.TimeoutMs + 1, lastReceive), Is.True, "a peer that is really gone still times out");
	}

	[Test]
	public void AGapAtTheStallThresholdIsNotAStall()
	{
		var clock = new NetProtocol.StallClock();
		long now = 10_000;
		clock.Tick(now);
		long lastReceive = now;

		now += NetProtocol.StallThresholdMs;
		clock.Tick(now);

		Assert.That(clock.TimedOut(now + NetProtocol.TimeoutMs, lastReceive), Is.True, "only a gap longer than the threshold restarts the window");
	}
}
