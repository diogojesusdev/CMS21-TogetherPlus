using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CMS21Together.Shared.Net;
using NUnit.Framework;

namespace CMS21Together.Tests;

[Serializable]
public enum SampleKind : short
{
	None,
	Engine = 7,
	Body = -3
}

[Serializable]
public struct SampleStruct
{
	public float X;
	public int Y;
	public SampleKind Kind;
}

[Serializable]
public class SampleNode
{
	public string Name;
	public SampleNode Child;
}

[Serializable]
public class SampleDto
{
	public bool Flag;
	public byte B;
	public sbyte Sb;
	public short S;
	public ushort Us;
	public char C;
	public int I;
	public uint Ui;
	public long L;
	public ulong Ul;
	public float F;
	public double D;
	public decimal M;
	public string Text;
	public string NullText;
	public Guid Id;
	public DateTime When;
	public TimeSpan Span;
	public int? MaybeInt;
	public int? MaybeNull;
	public SampleKind Kind;
	public SampleStruct Struct;
	public SampleStruct[] Structs;
	public byte[] Bytes;
	public int[] NullArray;
	public List<string> Names;
	public List<SampleNode> Nodes;
	public HashSet<int> Set;
	public Dictionary<string, List<bool>> Skills;
	public ReadOnlyDictionary<string, bool> Content;
	public SampleNode Tree;
	public SampleNode Missing;
	public string AutoProperty { get; set; }
	[NonSerialized] public object Ignored;
	private int hidden;

	public int Hidden
	{
		get => hidden;
		set => hidden = value;
	}
}

[Serializable]
public class HasObjectField
{
	public object Anything;
}

public class NotSerializable
{
	public int Value;
}

[Serializable]
public class HasNotSerializable
{
	public NotSerializable Inner;
}

[Serializable]
public class HasForeignType
{
	public Version Version;
}

[TestFixture]
public class NetSerializerTests
{
	private static SampleDto Sample() => new()
	{
		Flag = true,
		B = 200,
		Sb = -100,
		S = -12345,
		Us = 54321,
		C = 'Ω',
		I = int.MinValue,
		Ui = uint.MaxValue,
		L = long.MinValue,
		Ul = ulong.MaxValue,
		F = 3.5f,
		D = -1.25e300,
		M = 12345.6789m,
		Text = "garage ✓ ünïcode",
		NullText = null,
		Id = Guid.NewGuid(),
		When = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
		Span = TimeSpan.FromMinutes(90),
		MaybeInt = 42,
		MaybeNull = null,
		Kind = SampleKind.Body,
		Struct = new SampleStruct { X = 1.5f, Y = 2, Kind = SampleKind.Engine },
		Structs = new[] { new SampleStruct { X = 9f }, new SampleStruct { Y = -1 } },
		Bytes = new byte[] { 0, 1, 2, 255 },
		NullArray = null,
		Names = new List<string> { "a", null, "c" },
		Nodes = new List<SampleNode> { new() { Name = "n1" }, null },
		Set = new HashSet<int> { 1, 2, 3 },
		Skills = new Dictionary<string, List<bool>> { ["p1"] = new() { true, false }, ["p2"] = null },
		Content = new ReadOnlyDictionary<string, bool>(new Dictionary<string, bool> { ["dlc"] = true }),
		Tree = new SampleNode { Name = "root", Child = new SampleNode { Name = "leaf" } },
		Missing = null,
		AutoProperty = "prop",
		Ignored = new object(),
		Hidden = 77
	};

	[Test]
	public void RoundTripsEverySupportedShape()
	{
		var original = Sample();
		var copy = NetSerializer.Deserialize<SampleDto>(NetSerializer.Serialize(original));

		Assert.Multiple(() =>
		{
			Assert.That(copy.Flag, Is.True);
			Assert.That(copy.B, Is.EqualTo(original.B));
			Assert.That(copy.Sb, Is.EqualTo(original.Sb));
			Assert.That(copy.S, Is.EqualTo(original.S));
			Assert.That(copy.Us, Is.EqualTo(original.Us));
			Assert.That(copy.C, Is.EqualTo(original.C));
			Assert.That(copy.I, Is.EqualTo(original.I));
			Assert.That(copy.Ui, Is.EqualTo(original.Ui));
			Assert.That(copy.L, Is.EqualTo(original.L));
			Assert.That(copy.Ul, Is.EqualTo(original.Ul));
			Assert.That(copy.F, Is.EqualTo(original.F));
			Assert.That(copy.D, Is.EqualTo(original.D));
			Assert.That(copy.M, Is.EqualTo(original.M));
			Assert.That(copy.Text, Is.EqualTo(original.Text));
			Assert.That(copy.NullText, Is.Null);
			Assert.That(copy.Id, Is.EqualTo(original.Id));
			Assert.That(copy.When, Is.EqualTo(original.When));
			Assert.That(copy.When.Kind, Is.EqualTo(DateTimeKind.Utc));
			Assert.That(copy.Span, Is.EqualTo(original.Span));
			Assert.That(copy.MaybeInt, Is.EqualTo(42));
			Assert.That(copy.MaybeNull, Is.Null);
			Assert.That(copy.Kind, Is.EqualTo(SampleKind.Body));
			Assert.That(copy.Struct, Is.EqualTo(original.Struct));
			Assert.That(copy.Structs, Is.EqualTo(original.Structs));
			Assert.That(copy.Bytes, Is.EqualTo(original.Bytes));
			Assert.That(copy.NullArray, Is.Null);
			Assert.That(copy.Names, Is.EqualTo(original.Names));
			Assert.That(copy.Nodes[0].Name, Is.EqualTo("n1"));
			Assert.That(copy.Nodes[1], Is.Null);
			Assert.That(copy.Set, Is.EquivalentTo(original.Set));
			Assert.That(copy.Skills["p1"], Is.EqualTo(new[] { true, false }));
			Assert.That(copy.Skills["p2"], Is.Null);
			Assert.That(copy.Content["dlc"], Is.True);
			Assert.That(copy.Tree.Child.Name, Is.EqualTo("leaf"));
			Assert.That(copy.Missing, Is.Null);
			Assert.That(copy.AutoProperty, Is.EqualTo("prop"));
			Assert.That(copy.Ignored, Is.Null);
			Assert.That(copy.Hidden, Is.EqualTo(77));
		});
	}

	[Test]
	public void PrimitivesUseTheirRawSize()
	{
		Assert.That(NetSerializer.Serialize(5), Has.Length.EqualTo(4));
		Assert.That(NetSerializer.Serialize(true), Has.Length.EqualTo(1));
		Assert.That(NetSerializer.Serialize(SampleKind.Engine), Has.Length.EqualTo(2));
		Assert.That(NetSerializer.Serialize(1.5f), Has.Length.EqualTo(4));
	}

	[Test]
	public void NullTopLevelObjectRoundTrips()
	{
		Assert.That(NetSerializer.Deserialize<SampleDto>(NetSerializer.Serialize<SampleDto>(null)), Is.Null);
		Assert.That(NetSerializer.Deserialize<string>(NetSerializer.Serialize<string>(null)), Is.Null);
	}

	[TestCase(typeof(object))]
	[TestCase(typeof(HasObjectField))]
	[TestCase(typeof(HasNotSerializable))]
	[TestCase(typeof(HasForeignType))]
	[TestCase(typeof(Version))]
	[TestCase(typeof(int[,]))]
	[TestCase(typeof(IList<int>))]
	public void RejectsTypesOutsideTheWhitelist(Type type)
	{
		Assert.Throws<NotSupportedException>(() => NetSerializer.Validate(type));
	}

	[Test]
	public void TruncatedDataFailsCleanly()
	{
		var data = NetSerializer.Serialize(Sample());
		for (var length = 0; length < data.Length; length += 7)
		{
			var truncated = new byte[length];
			Array.Copy(data, truncated, length);
			Assert.Throws(Is.InstanceOf<NetFormatException>(), () => NetSerializer.Deserialize<SampleDto>(truncated), $"length {length}");
		}
	}

	[Test]
	public void HugeCollectionCountIsRejectedBeforeAllocating()
	{
		using var writer = new NetWriter();
		writer.WriteInt32(int.MaxValue);
		Assert.Throws<NetFormatException>(() => NetSerializer.Deserialize<List<long>>(writer.ToArray()));

		using var strings = new NetWriter();
		strings.WriteInt32(1_000_000);
		Assert.Throws<NetFormatException>(() => NetSerializer.Deserialize<string[]>(strings.ToArray()));
	}

	[Test]
	public void CyclesHitTheDepthLimit()
	{
		var node = new SampleNode { Name = "loop" };
		node.Child = node;
		Assert.Throws<NetFormatException>(() => NetSerializer.Serialize(node));
	}

	[Test]
	public void DeepWireDataHitsTheDepthLimit()
	{
		using var writer = new NetWriter();
		for (var i = 0; i < NetSerializer.MaxDepth + 5; i++)
		{
			writer.WriteBool(true);
			writer.WriteString("x");
		}
		writer.WriteBool(false);
		Assert.Throws<NetFormatException>(() => NetSerializer.Deserialize<SampleNode>(writer.ToArray()));
	}

	[Test]
	public void InvalidBoolAndDuplicateKeysAreRejected()
	{
		Assert.Throws<NetFormatException>(() => NetSerializer.Deserialize<bool>(new byte[] { 2 }));

		using var writer = new NetWriter();
		writer.WriteInt32(2);
		writer.WriteString("k");
		writer.WriteBool(true);
		writer.WriteString("k");
		writer.WriteBool(false);
		Assert.Throws<NetFormatException>(() => NetSerializer.Deserialize<Dictionary<string, bool>>(writer.ToArray()));
	}

	[Test]
	public void TrailingBytesAreRejected()
	{
		Assert.Throws<NetFormatException>(() => NetSerializer.Deserialize<int>(new byte[5]));
	}

	[Test]
	public void RandomGarbageNeverThrowsAnythingButFormatErrors()
	{
		var random = new Random(1234);
		for (var i = 0; i < 2000; i++)
		{
			var data = new byte[random.Next(0, 200)];
			random.NextBytes(data);
			try
			{
				NetSerializer.Deserialize<SampleDto>(data);
			}
			catch (NetFormatException)
			{
			}
		}
	}
}
