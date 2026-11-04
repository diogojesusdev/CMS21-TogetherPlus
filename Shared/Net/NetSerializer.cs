using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace CMS21Together.Shared.Net;

/// <summary>
/// Whitelisted binary serializer replacing BinaryFormatter (audit A1).
/// The wire carries no type information: both sides use the static type, so a peer can never make us
/// instantiate an arbitrary type. Only primitives, a fixed set of BCL containers and [Serializable]
/// types from explicitly allowed assemblies are accepted; anything else fails when the model is built.
/// </summary>
public static class NetSerializer
{
	public const int MaxDepth = 32;
	public const int MaxCollectionCount = 1 << 24;

	private static readonly object BuildLock = new();
	private static readonly ConcurrentDictionary<Type, Codec> Codecs = new();
	private static readonly HashSet<Assembly> AllowedAssemblies = new() { typeof(NetSerializer).Assembly };
	private static Dictionary<Type, Codec> building;

	public static void AllowAssembly(Assembly assembly)
	{
		lock (BuildLock) AllowedAssemblies.Add(assembly);
	}

	public static void Write<T>(NetWriter writer, T value) => GetCodec(typeof(T)).Write(writer, value, 0);

	public static T Read<T>(NetReader reader) => (T)GetCodec(typeof(T)).Read(reader, 0);

	public static void Write(NetWriter writer, Type type, object value) => GetCodec(type).Write(writer, value, 0);

	public static object Read(NetReader reader, Type type) => GetCodec(type).Read(reader, 0);

	/// <summary>Builds (and caches) the codec for a type; throws <see cref="NotSupportedException"/> if any reachable field is not allowed.</summary>
	public static void Validate(Type type) => GetCodec(type);

	public static byte[] Serialize<T>(T value)
	{
		using var writer = new NetWriter();
		Write(writer, value);
		return writer.ToArray();
	}

	public static T Deserialize<T>(byte[] data)
	{
		var reader = new NetReader(data);
		var value = Read<T>(reader);
		if (reader.Remaining != 0) throw new NetFormatException($"{reader.Remaining} trailing bytes after {typeof(T).Name}.");
		return value;
	}

	private static Codec GetCodec(Type type)
	{
		if (Codecs.TryGetValue(type, out var codec)) return codec;
		lock (BuildLock)
		{
			if (Codecs.TryGetValue(type, out codec)) return codec;
			var outermost = building == null;
			if (outermost) building = new Dictionary<Type, Codec>();
			try
			{
				codec = Build(type);
				if (outermost)
					foreach (var built in building)
						Codecs[built.Key] = built.Value;
				return codec;
			}
			finally
			{
				if (outermost) building = null;
			}
		}
	}

	private static Codec Build(Type type)
	{
		if (building.TryGetValue(type, out var inProgress)) return inProgress;
		if (Codecs.TryGetValue(type, out var done)) return done;

		var primitive = PrimitiveCodec.TryCreate(type);
		if (primitive != null) return building[type] = primitive;

		if (type.IsEnum) return building[type] = new EnumCodec(type, Build(Enum.GetUnderlyingType(type)));

		var nullableOf = Nullable.GetUnderlyingType(type);
		if (nullableOf != null) return building[type] = new NullableCodec(Build(nullableOf));

		if (type.IsArray)
		{
			if (type.GetArrayRank() != 1) throw Unsupported(type, "multi-dimensional arrays are not supported");
			var element = type.GetElementType();
			if (element == typeof(byte)) return building[type] = new ByteArrayCodec();
			return building[type] = new ArrayCodec(element, Build(element));
		}

		if (type.IsGenericType)
		{
			var definition = type.GetGenericTypeDefinition();
			var args = type.GetGenericArguments();
			if (definition == typeof(List<>)) return building[type] = new ListCodec(type, Build(args[0]));
			if (definition == typeof(HashSet<>)) return building[type] = new ListCodec(type, Build(args[0]));
			if (definition == typeof(Dictionary<,>)) return building[type] = new DictionaryCodec(type, args, Build(args[0]), Build(args[1]), false);
			if (definition == typeof(ReadOnlyDictionary<,>)) return building[type] = new DictionaryCodec(type, args, Build(args[0]), Build(args[1]), true);
		}

		if (type.IsPrimitive || type.IsPointer || type.IsInterface || type.IsAbstract || type == typeof(object) || typeof(Delegate).IsAssignableFrom(type))
			throw Unsupported(type, "type is not a concrete serializable data type");
		if (!type.IsSerializable) throw Unsupported(type, "type is not marked [Serializable]");
		bool allowed;
		lock (BuildLock) allowed = AllowedAssemblies.Contains(type.Assembly);
		if (!allowed) throw Unsupported(type, $"assembly {type.Assembly.GetName().Name} is not whitelisted");
		if (type.ContainsGenericParameters) throw Unsupported(type, "open generic type");

		var objectCodec = new ObjectCodec(type);
		building[type] = objectCodec;
		objectCodec.Initialize(CollectFields(type).Select(f => (f, Build(f.FieldType))).ToArray());
		return objectCodec;
	}

	private static List<FieldInfo> CollectFields(Type type)
	{
		var chain = new List<Type>();
		for (var t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
		{
			if (!t.IsSerializable) throw Unsupported(type, $"base type {t.FullName} is not [Serializable]");
			chain.Add(t);
		}
		chain.Reverse();

		var fields = new List<FieldInfo>();
		foreach (var t in chain)
			fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
				.Where(f => !f.IsNotSerialized)
				.OrderBy(f => f.Name, StringComparer.Ordinal));
		return fields;
	}

	private static NotSupportedException Unsupported(Type type, string reason) =>
		new($"NetSerializer cannot handle {type.FullName}: {reason}.");

	private static void CheckDepth(int depth)
	{
		if (depth > MaxDepth) throw new NetFormatException($"Object graph deeper than {MaxDepth} levels.");
	}

	private static int ReadCount(NetReader reader, Codec element)
	{
		var count = reader.ReadInt32();
		if (count < -1 || count > MaxCollectionCount) throw new NetFormatException($"Invalid collection count {count}.");
		if (count > 0 && element.MinSize > 0 && count > reader.Remaining / element.MinSize)
			throw new NetFormatException($"Collection count {count} exceeds the remaining data.");
		return count;
	}

	private abstract class Codec
	{
		/// <summary>Minimum encoded size, used to reject absurd counts before allocating.</summary>
		public int MinSize { get; protected set; }

		public abstract void Write(NetWriter writer, object value, int depth);
		public abstract object Read(NetReader reader, int depth);
	}

	private sealed class PrimitiveCodec : Codec
	{
		private readonly Action<NetWriter, object> write;
		private readonly Func<NetReader, object> read;

		private PrimitiveCodec(int size, Action<NetWriter, object> write, Func<NetReader, object> read)
		{
			MinSize = size;
			this.write = write;
			this.read = read;
		}

		public static Codec TryCreate(Type type)
		{
			if (type == typeof(bool)) return new PrimitiveCodec(1, (w, v) => w.WriteBool((bool)v), r => r.ReadBool());
			if (type == typeof(byte)) return new PrimitiveCodec(1, (w, v) => w.WriteByte((byte)v), r => r.ReadByte());
			if (type == typeof(sbyte)) return new PrimitiveCodec(1, (w, v) => w.WriteSByte((sbyte)v), r => r.ReadSByte());
			if (type == typeof(short)) return new PrimitiveCodec(2, (w, v) => w.WriteInt16((short)v), r => r.ReadInt16());
			if (type == typeof(ushort)) return new PrimitiveCodec(2, (w, v) => w.WriteUInt16((ushort)v), r => r.ReadUInt16());
			if (type == typeof(char)) return new PrimitiveCodec(2, (w, v) => w.WriteChar((char)v), r => r.ReadChar());
			if (type == typeof(int)) return new PrimitiveCodec(4, (w, v) => w.WriteInt32((int)v), r => r.ReadInt32());
			if (type == typeof(uint)) return new PrimitiveCodec(4, (w, v) => w.WriteUInt32((uint)v), r => r.ReadUInt32());
			if (type == typeof(long)) return new PrimitiveCodec(8, (w, v) => w.WriteInt64((long)v), r => r.ReadInt64());
			if (type == typeof(ulong)) return new PrimitiveCodec(8, (w, v) => w.WriteUInt64((ulong)v), r => r.ReadUInt64());
			if (type == typeof(float)) return new PrimitiveCodec(4, (w, v) => w.WriteSingle((float)v), r => r.ReadSingle());
			if (type == typeof(double)) return new PrimitiveCodec(8, (w, v) => w.WriteDouble((double)v), r => r.ReadDouble());
			if (type == typeof(decimal)) return new PrimitiveCodec(16, (w, v) => w.WriteDecimal((decimal)v), r => r.ReadDecimal());
			if (type == typeof(string)) return new PrimitiveCodec(4, (w, v) => w.WriteString((string)v), r => r.ReadString());
			if (type == typeof(Guid)) return new PrimitiveCodec(16, (w, v) => w.WriteBytes(((Guid)v).ToByteArray()), r => new Guid(r.ReadBytes(16)));
			if (type == typeof(TimeSpan)) return new PrimitiveCodec(8, (w, v) => w.WriteInt64(((TimeSpan)v).Ticks), r => new TimeSpan(r.ReadInt64()));
			if (type == typeof(DateTime))
				return new PrimitiveCodec(8, (w, v) => w.WriteInt64(((DateTime)v).ToBinary()), r =>
				{
					try
					{
						return DateTime.FromBinary(r.ReadInt64());
					}
					catch (ArgumentException ex)
					{
						throw new NetFormatException("Invalid DateTime.", ex);
					}
				});
			return null;
		}

		public override void Write(NetWriter writer, object value, int depth) => write(writer, value);
		public override object Read(NetReader reader, int depth) => read(reader);
	}

	private sealed class EnumCodec : Codec
	{
		private readonly Type type;
		private readonly Codec underlying;

		public EnumCodec(Type type, Codec underlying)
		{
			this.type = type;
			this.underlying = underlying;
			MinSize = underlying.MinSize;
		}

		public override void Write(NetWriter writer, object value, int depth) =>
			underlying.Write(writer, Convert.ChangeType(value, Enum.GetUnderlyingType(type)), depth);

		public override object Read(NetReader reader, int depth) => Enum.ToObject(type, underlying.Read(reader, depth));
	}

	private sealed class NullableCodec : Codec
	{
		private readonly Codec inner;

		public NullableCodec(Codec inner)
		{
			this.inner = inner;
			MinSize = 1;
		}

		public override void Write(NetWriter writer, object value, int depth)
		{
			writer.WriteBool(value != null);
			if (value != null) inner.Write(writer, value, depth + 1);
		}

		public override object Read(NetReader reader, int depth)
		{
			CheckDepth(depth);
			return reader.ReadBool() ? inner.Read(reader, depth + 1) : null;
		}
	}

	private sealed class ByteArrayCodec : Codec
	{
		public ByteArrayCodec() => MinSize = 4;

		public override void Write(NetWriter writer, object value, int depth)
		{
			if (value == null)
			{
				writer.WriteInt32(-1);
				return;
			}
			var bytes = (byte[])value;
			writer.WriteInt32(bytes.Length);
			writer.WriteBytes(bytes);
		}

		public override object Read(NetReader reader, int depth)
		{
			var count = reader.ReadInt32();
			if (count == -1) return null;
			if (count < -1) throw new NetFormatException($"Invalid byte[] length {count}.");
			return reader.ReadBytes(count);
		}
	}

	private sealed class ArrayCodec : Codec
	{
		private readonly Type elementType;
		private readonly Codec element;

		public ArrayCodec(Type elementType, Codec element)
		{
			this.elementType = elementType;
			this.element = element;
			MinSize = 4;
		}

		public override void Write(NetWriter writer, object value, int depth)
		{
			CheckDepth(depth);
			if (value == null)
			{
				writer.WriteInt32(-1);
				return;
			}
			var array = (Array)value;
			writer.WriteInt32(array.Length);
			foreach (var item in array) element.Write(writer, item, depth + 1);
		}

		public override object Read(NetReader reader, int depth)
		{
			CheckDepth(depth);
			var count = ReadCount(reader, element);
			if (count == -1) return null;
			var array = Array.CreateInstance(elementType, count);
			for (var i = 0; i < count; i++) array.SetValue(element.Read(reader, depth + 1), i);
			return array;
		}
	}

	private sealed class ListCodec : Codec
	{
		private readonly Type type;
		private readonly Codec element;
		private readonly MethodInfo add;

		public ListCodec(Type type, Codec element)
		{
			this.type = type;
			this.element = element;
			add = type.GetMethod("Add", new[] { type.GetGenericArguments()[0] });
			MinSize = 4;
		}

		public override void Write(NetWriter writer, object value, int depth)
		{
			CheckDepth(depth);
			if (value == null)
			{
				writer.WriteInt32(-1);
				return;
			}
			var collection = (ICollection)(value as ICollection ?? ((IEnumerable)value).Cast<object>().ToList());
			writer.WriteInt32(collection.Count);
			foreach (var item in collection) element.Write(writer, item, depth + 1);
		}

		public override object Read(NetReader reader, int depth)
		{
			CheckDepth(depth);
			var count = ReadCount(reader, element);
			if (count == -1) return null;
			var collection = Activator.CreateInstance(type);
			var args = new object[1];
			for (var i = 0; i < count; i++)
			{
				args[0] = element.Read(reader, depth + 1);
				add.Invoke(collection, args);
			}
			return collection;
		}
	}

	private sealed class DictionaryCodec : Codec
	{
		private readonly Type dictionaryType;
		private readonly Type readOnlyType;
		private readonly Codec key;
		private readonly Codec value;

		public DictionaryCodec(Type type, Type[] args, Codec key, Codec value, bool readOnly)
		{
			dictionaryType = typeof(Dictionary<,>).MakeGenericType(args);
			readOnlyType = readOnly ? type : null;
			this.key = key;
			this.value = value;
			MinSize = 4;
		}

		public override void Write(NetWriter writer, object instance, int depth)
		{
			CheckDepth(depth);
			if (instance == null)
			{
				writer.WriteInt32(-1);
				return;
			}
			var dictionary = (IDictionary)instance;
			writer.WriteInt32(dictionary.Count);
			foreach (DictionaryEntry entry in dictionary)
			{
				key.Write(writer, entry.Key, depth + 1);
				value.Write(writer, entry.Value, depth + 1);
			}
		}

		public override object Read(NetReader reader, int depth)
		{
			CheckDepth(depth);
			var count = ReadCount(reader, key);
			if (count == -1) return null;
			var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType);
			for (var i = 0; i < count; i++)
			{
				var k = key.Read(reader, depth + 1);
				if (k == null) throw new NetFormatException("Null dictionary key.");
				if (dictionary.Contains(k)) throw new NetFormatException("Duplicate dictionary key.");
				dictionary.Add(k, value.Read(reader, depth + 1));
			}
			return readOnlyType == null ? dictionary : Activator.CreateInstance(readOnlyType, dictionary);
		}
	}

	private sealed class ObjectCodec : Codec
	{
		private readonly Type type;
		private readonly bool isClass;
		private (FieldInfo Field, Codec Codec)[] fields;

		public ObjectCodec(Type type)
		{
			this.type = type;
			isClass = !type.IsValueType;
			MinSize = isClass ? 1 : 0;
		}

		public void Initialize((FieldInfo, Codec)[] members)
		{
			fields = members;
			MinSize = isClass ? 1 : members.Sum(m => m.Item2.MinSize);
		}

		public override void Write(NetWriter writer, object value, int depth)
		{
			CheckDepth(depth);
			if (isClass)
			{
				writer.WriteBool(value != null);
				if (value == null) return;
			}
			else if (value == null)
			{
				value = FormatterServices.GetUninitializedObject(type);
			}
			foreach (var (field, codec) in fields) codec.Write(writer, field.GetValue(value), depth + 1);
		}

		public override object Read(NetReader reader, int depth)
		{
			CheckDepth(depth);
			if (isClass && !reader.ReadBool()) return null;
			var instance = FormatterServices.GetUninitializedObject(type);
			foreach (var (field, codec) in fields) field.SetValue(instance, codec.Read(reader, depth + 1));
			return instance;
		}
	}
}
