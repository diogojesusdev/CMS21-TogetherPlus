using CMS21Together.Shared.World;
using UnityEngine;

namespace CMS21Together.ClientSide.Data.Garage.Car;

/// <summary>
/// Stable car loader ids (audit A12): the index of the loader in the garage's list, found by object identity. Only a
/// loader outside that list falls back to the number at the end of its name (hf3 read one character of the name).
/// </summary>
public static class CarLoaderRegistry
{
	public static int IdOf(CarLoader loader)
	{
		if (loader == null) return -1;
		var loaders = GameData.Instance?.carLoaders;
		if (loaders != null)
			for (var i = 0; i < loaders.Length; i++)
				if (loaders[i] != null && loaders[i].Pointer == loader.Pointer)
					return i;
		return LoaderNames.IdFromName(loader.gameObject.name);
	}

	/// <summary>Id of the car loader a part, fluid or other car component belongs to (-1 when none).</summary>
	public static int IdOfParent(Component component)
	{
		if (component == null) return -1;
		var onCar = component.GetComponentInParent<CarLoaderOnCar>();
		return onCar != null ? IdOf(onCar.CarLoader) : -1;
	}
}