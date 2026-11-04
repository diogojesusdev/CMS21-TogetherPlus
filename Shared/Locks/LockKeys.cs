using System;

namespace CMS21Together.Shared.Locks;

/// <summary>
/// Names of lockable resources (plan section 3.2). Keys are '/'-separated and at most two levels deep for hierarchy:
/// <c>car/{L}</c> and <c>stand/{n}</c> are parents of their part and body keys.
/// </summary>
public static class LockKeys
{
	public const int MaxLength = 96;
	private const int MaxSegmentLength = 24;
	private const int MaxSegments = 6;

	private static readonly string[] Roots = { "car", "stand", "tool", "lifter", "park", "job", "item", "group" };

	public static string Car(int loader) => "car/" + loader;
	public static string Part(int loader, int type, int key, int index) => $"car/{loader}/part/{type}/{key}/{index}";
	public static string Body(int loader, int id) => $"car/{loader}/body/{id}";
	public static string Stand(int stand) => "stand/" + stand;
	public static string StandPart(int stand, int key) => $"stand/{stand}/part/{key}";
	public static string Tool(string type) => "tool/" + type;
	public static string Lifter(int loader) => "lifter/" + loader;
	public static string Park(int slot) => "park/" + slot;
	public static string Job(int id) => "job/" + id;
	public static string Item(long uid) => "item/" + uid;
	public static string Group(long uid) => "group/" + uid;

	/// <summary>Key of a part-script write: car loaders -1 and -2 are engine stands 1 and 2.</summary>
	public static string ForPartScript(int carLoaderID, int type, int key, int index) =>
		carLoaderID switch
		{
			-1 => StandPart(1, key),
			-2 => StandPart(2, key),
			_ => Part(carLoaderID, type, key, index)
		};

	public static bool IsValid(string key)
	{
		if (string.IsNullOrEmpty(key) || key.Length > MaxLength) return false;
		var segments = key.Split('/');
		if (segments.Length < 2 || segments.Length > MaxSegments) return false;
		if (Array.IndexOf(Roots, segments[0]) < 0) return false;
		foreach (var segment in segments)
		{
			if (segment.Length == 0 || segment.Length > MaxSegmentLength) return false;
			foreach (var c in segment)
				if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
					return false;
		}
		return true;
	}

	/// <summary>The whole-object key above a component (<c>car/3/part/...</c> gives <c>car/3</c>), or null for top-level keys.</summary>
	public static string Parent(string key)
	{
		if (key == null) return null;
		var first = key.IndexOf('/');
		if (first < 0) return null;
		var second = key.IndexOf('/', first + 1);
		return second < 0 ? null : key.Substring(0, second);
	}

	/// <summary>Components (parts, body panels, items) are the "one at a time per player" locks; stations and whole cars are not.</summary>
	public static bool IsComponent(string key) =>
		key != null && (key.Contains("/part/") || key.Contains("/body/") || key.StartsWith("item/", StringComparison.Ordinal) ||
		                key.StartsWith("group/", StringComparison.Ordinal));

	/// <summary>Two keys conflict when they are equal or one is the parent of the other.</summary>
	public static bool Conflicts(string a, string b) =>
		a != null && b != null && (string.Equals(a, b, StringComparison.Ordinal) || string.Equals(Parent(a), b, StringComparison.Ordinal) ||
		                           string.Equals(Parent(b), a, StringComparison.Ordinal));
}
