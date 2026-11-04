using System.Collections.Generic;
using System.Linq;
using CMS21Together.Shared.Net;

namespace CMS21Together.Shared.Features;

/// <summary>
/// Workshop (mod) cars allowed in a session (Phase 6b): only the ones every player has, so the host never spawns a car
/// someone cannot load.
/// </summary>
public sealed class WorkshopCars
{
	public const int MaxCars = 2048;

	private readonly Dictionary<int, HashSet<string>> reports = new();

	public void Report(int slot, IEnumerable<string> cars) => reports[slot] = new HashSet<string>(cars ?? Enumerable.Empty<string>());

	public bool Forget(int slot) => reports.Remove(slot);

	/// <summary>Forgets every player not in <paramref name="present"/>; true when one was forgotten (publish again).</summary>
	public bool KeepOnly(ICollection<int> present) => reports.Keys.Where(slot => !present.Contains(slot)).ToList().Count(Forget) > 0;

	/// <summary>The cars every reporting player has (sorted), or null before anyone reported.</summary>
	public List<string> Allowed()
	{
		if (reports.Count == 0) return null;
		HashSet<string> common = null;
		foreach (var cars in reports.Values)
		{
			if (common == null) common = new HashSet<string>(cars);
			else common.IntersectWith(cars);
		}
		return common.OrderBy(c => c, System.StringComparer.Ordinal).ToList();
	}

	public static void Write(Packet packet, IReadOnlyList<string> cars)
	{
		packet.Write(cars.Count);
		foreach (var car in cars) packet.Write(car);
	}

	public static List<string> Read(Packet packet)
	{
		var count = packet.ReadInt();
		if (count < 0 || count > MaxCars) throw new NetFormatException($"Workshop list of {count} cars.");
		var cars = new List<string>(count);
		for (var i = 0; i < count; i++)
		{
			var car = packet.Read<string>();
			if (string.IsNullOrEmpty(car) || car.Length > 256) throw new NetFormatException("Workshop car id.");
			cars.Add(car);
		}
		return cars;
	}
}