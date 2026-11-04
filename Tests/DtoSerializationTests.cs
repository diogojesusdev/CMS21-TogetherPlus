using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// Round-trips every [Serializable] DTO of the built mod through the mod's own NetSerializer, so a new DTO
/// field of an unsupported type (or a serializer regression) fails here instead of in-game.
/// Skipped when the mod DLL has not been built yet (run build.ps1).
/// </summary>
[TestFixture]
public class DtoSerializationTests
{
	private static Assembly mod;
	private static MethodInfo serialize;
	private static MethodInfo deserialize;
	private static MethodInfo validate;
	private static string repoRoot;

	[OneTimeSetUp]
	public void LoadMod()
	{
		var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CMS21-Together.csproj"))) dir = dir.Parent;
		if (dir == null) Assert.Ignore("Repository root not found.");
		repoRoot = dir.FullName;

		var dll = new[] { "Release", "Debug" }
			.Select(c => Path.Combine(repoRoot, "bin", c, "CMS21-Together.dll"))
			.Where(File.Exists)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
		if (dll == null) Assert.Ignore("CMS21-Together.dll not built; run build.ps1 first.");

		var probe = new[] { Path.GetDirectoryName(dll), Path.Combine(repoRoot, "Libs") };
		AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
		{
			var name = new AssemblyName(args.Name).Name + ".dll";
			var path = probe.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
			return path == null ? null : Assembly.LoadFrom(path);
		};

		mod = Assembly.LoadFrom(dll);
		var serializer = mod.GetType("CMS21Together.Shared.Net.NetSerializer", true);
		serialize = serializer.GetMethods().Single(m => m.Name == "Serialize" && m.IsGenericMethodDefinition);
		deserialize = serializer.GetMethods().Single(m => m.Name == "Deserialize" && m.IsGenericMethodDefinition);
		validate = serializer.GetMethod("Validate");
	}

	private static IEnumerable<Type> ModTypes()
	{
		try
		{
			return mod.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			return ex.Types.Where(t => t != null);
		}
	}

	private static List<Type> DataTypes() => ModTypes()
		.Where(t => t.Namespace != null && t.Namespace.StartsWith("CMS21Together.Shared.Data", StringComparison.Ordinal))
		.Where(t => t.IsSerializable && !t.IsGenericTypeDefinition && !t.IsNested)
		.OrderBy(t => t.FullName)
		.ToList();

	[Test]
	public void EveryDataTypeIsSupported()
	{
		var types = DataTypes();
		Assert.That(types.Count, Is.GreaterThan(40), "DTO discovery looks broken");
		var failures = new List<string>();
		foreach (var type in types)
		{
			try
			{
				validate.Invoke(null, new object[] { type });
			}
			catch (TargetInvocationException ex)
			{
				failures.Add($"{type.FullName}: {ex.InnerException?.Message}");
			}
		}
		Assert.That(failures, Is.Empty);
	}

	[Test]
	public void PacketPayloadTypesAreSupported()
	{
		Type T(string name) => mod.GetType(name, true);
		var newCarData = T("CMS21Together.Shared.Data.Vanilla.Cars.ModNewCarData");
		var payloads = new[]
		{
			typeof(ReadOnlyDictionary<string, bool>),
			typeof(List<bool>),
			typeof(Dictionary<string, List<bool>>),
			typeof(Dictionary<,>).MakeGenericType(typeof(int), newCarData),
			T("CMS21Together.Shared.PacketTypes"),
			T("CMS21Together.ClientSide.Data.Player.InventoryAction")
		};
		foreach (var type in payloads) Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { type }), type.FullName);
	}

	[Test]
	public void EveryDataTypeRoundTrips()
	{
		var failures = new List<string>();
		foreach (var type in DataTypes())
		{
			for (var seed = 1; seed <= 3; seed++)
			{
				try
				{
					var original = Make(type, new Random(seed * 7919 + type.FullName.Length), 0);
					var bytes = (byte[])serialize.MakeGenericMethod(type).Invoke(null, new[] { original });
					var copy = deserialize.MakeGenericMethod(type).Invoke(null, new object[] { bytes });
					var difference = Compare(type, original, copy, type.Name);
					if (difference != null) failures.Add(difference);
				}
				catch (TargetInvocationException ex)
				{
					failures.Add($"{type.FullName}: {ex.InnerException}");
				}
			}
		}
		Assert.That(failures, Is.Empty);
	}

	[Test]
	public void HostileBytesForEveryDataTypeOnlyCauseFormatErrors()
	{
		var formatError = mod.GetType("CMS21Together.Shared.Net.NetFormatException", true);
		var failures = new List<string>();
		foreach (var type in DataTypes())
		{
			var random = new Random(type.FullName.Length * 131);
			var valid = (byte[])serialize.MakeGenericMethod(type).Invoke(null, new[] { Make(type, random, 0) });
			var reader = deserialize.MakeGenericMethod(type);
			for (var i = 0; i < 300; i++)
			{
				byte[] data;
				if (i % 3 == 0)
				{
					data = new byte[random.Next(0, 200)];
					random.NextBytes(data);
				}
				else if (i % 3 == 1)
					data = valid.Take(random.Next(valid.Length)).ToArray();
				else
				{
					data = (byte[])valid.Clone();
					for (var m = random.Next(1, 4); m > 0 && data.Length > 0; m--) data[random.Next(data.Length)] = (byte)random.Next(256);
				}
				try
				{
					reader.Invoke(null, new object[] { data });
				}
				catch (TargetInvocationException ex) when (formatError.IsInstanceOfType(ex.InnerException))
				{
				}
				catch (TargetInvocationException ex)
				{
					failures.Add($"{type.FullName}: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
					break;
				}
			}
		}
		Assert.That(failures, Is.Empty);
	}

	private static IEnumerable<FieldInfo> SerializedFields(Type type)
	{
		for (var t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
			foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				if (!field.IsNotSerialized)
					yield return field;
	}

	private static object Make(Type type, Random random, int depth)
	{
		if (type == typeof(bool)) return random.Next(2) == 1;
		if (type == typeof(byte)) return (byte)random.Next(256);
		if (type == typeof(sbyte)) return (sbyte)random.Next(-128, 128);
		if (type == typeof(short)) return (short)random.Next(short.MinValue, short.MaxValue);
		if (type == typeof(ushort)) return (ushort)random.Next(ushort.MaxValue);
		if (type == typeof(char)) return (char)random.Next(32, 0xD7FF);
		if (type == typeof(int)) return random.Next(int.MinValue, int.MaxValue);
		if (type == typeof(uint)) return (uint)random.Next();
		if (type == typeof(long)) return ((long)random.Next() << 32) | (uint)random.Next();
		if (type == typeof(ulong)) return ((ulong)random.Next() << 32) | (uint)random.Next();
		if (type == typeof(float)) return (float)(random.NextDouble() * 1000 - 500);
		if (type == typeof(double)) return random.NextDouble() * 1e6;
		if (type == typeof(decimal)) return (decimal)random.Next() / 100m;
		if (type == typeof(string)) return random.Next(5) == 0 ? null : "s" + random.Next() + "✓";
		if (type == typeof(Guid)) return Guid.NewGuid();
		if (type == typeof(DateTime)) return new DateTime(2020, 1, 1).AddSeconds(random.Next());
		if (type == typeof(TimeSpan)) return TimeSpan.FromSeconds(random.Next());
		if (type.IsEnum)
		{
			var values = Enum.GetValues(type);
			return values.Length == 0 ? Activator.CreateInstance(type) : values.GetValue(random.Next(values.Length));
		}
		var nullable = Nullable.GetUnderlyingType(type);
		if (nullable != null) return random.Next(3) == 0 ? null : Make(nullable, random, depth);

		var count = depth > 3 ? 0 : random.Next(0, 3);
		if (type.IsArray)
		{
			var array = Array.CreateInstance(type.GetElementType(), count);
			for (var i = 0; i < count; i++) array.SetValue(Make(type.GetElementType(), random, depth + 1), i);
			return array;
		}
		if (type.IsGenericType)
		{
			var definition = type.GetGenericTypeDefinition();
			var args = type.GetGenericArguments();
			if (definition == typeof(List<>) || definition == typeof(HashSet<>))
			{
				var collection = Activator.CreateInstance(type);
				var add = type.GetMethod("Add");
				for (var i = 0; i < count; i++) add.Invoke(collection, new[] { Make(args[0], random, depth + 1) });
				return collection;
			}
			if (definition == typeof(Dictionary<,>) || definition == typeof(ReadOnlyDictionary<,>))
			{
				var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args));
				for (var i = 0; i < count * 3 && dictionary.Count < count; i++)
				{
					var key = Make(args[0], random, depth + 1);
					if (key != null && !dictionary.Contains(key)) dictionary[key] = Make(args[1], random, depth + 1);
				}
				return definition == typeof(Dictionary<,>) ? dictionary : Activator.CreateInstance(type, dictionary);
			}
		}

		if (!type.IsValueType && depth > 4) return null;
		var instance = FormatterServices.GetUninitializedObject(type);
		foreach (var field in SerializedFields(type)) field.SetValue(instance, Make(field.FieldType, random, depth + 1));
		return instance;
	}

	private static string Compare(Type type, object expected, object actual, string path)
	{
		if (expected == null || actual == null)
			return expected == null && actual == null ? null : $"{path}: expected {expected ?? "null"}, got {actual ?? "null"}";
		if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(TimeSpan))
			return Equals(expected, actual) ? null : $"{path}: expected {expected}, got {actual}";
		var nullable = Nullable.GetUnderlyingType(type);
		if (nullable != null) return Compare(nullable, expected, actual, path);

		if (expected is IDictionary expectedMap)
		{
			var actualMap = (IDictionary)actual;
			if (expectedMap.Count != actualMap.Count) return $"{path}: dictionary count {expectedMap.Count} vs {actualMap.Count}";
			var valueType = type.GetGenericArguments()[1];
			foreach (DictionaryEntry entry in expectedMap)
			{
				if (!actualMap.Contains(entry.Key)) return $"{path}: missing key {entry.Key}";
				var difference = Compare(valueType, entry.Value, actualMap[entry.Key], $"{path}[{entry.Key}]");
				if (difference != null) return difference;
			}
			return null;
		}
		if (expected is IEnumerable expectedItems && type != typeof(string))
		{
			var elementType = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
			var left = expectedItems.Cast<object>().ToList();
			var right = ((IEnumerable)actual).Cast<object>().ToList();
			if (left.Count != right.Count) return $"{path}: count {left.Count} vs {right.Count}";
			for (var i = 0; i < left.Count; i++)
			{
				var difference = Compare(elementType, left[i], right[i], $"{path}[{i}]");
				if (difference != null) return difference;
			}
			return null;
		}

		foreach (var field in SerializedFields(type))
		{
			var difference = Compare(field.FieldType, field.GetValue(expected), field.GetValue(actual), $"{path}.{field.Name}");
			if (difference != null) return difference;
		}
		return null;
	}
}
