using System;
using System.Collections.Generic;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Garage.Car;

namespace CMS21Together.Shared.Data.Vanilla.Cars;

[Serializable]
public class ModCarPart
{
	public int carPartID;

	public string name = string.Empty;
	public bool switched;
	public bool inprogress;
	public float condition;
	public bool unmounted;
	public string tunedID = string.Empty;
	public bool isTinted;
	public ModColor TintColor;
	public ModColor colors;
	public int paintType;
	public ModPaintData paintData;
	public float conditionStructure;
	public float conditionPaint;
	public string livery;
	public float liveryStrength;
	public bool outsaidRustEnabled;
	public float dent;
	public string additionalString;
	public List<ModCarPart> connectedParts = new();
	public int quality;
	public float Dust;
	public float washFactor;

	public ModCarPart(CarPart _part, int _carPartID, int carLoaderID)
	{
		if (_part == null) return;

		if (!string.IsNullOrEmpty(_part.name))
			name = _part.name;
		else
			name = "hood";

		carPartID = _carPartID;
		switched = _part.Switched;
		inprogress = _part.Switched;
		condition = _part.Condition;
		unmounted = _part.Unmounted;
		tunedID = _part.TunedID;
		isTinted = _part.IsTinted;
		TintColor = new ModColor(_part.TintColor);
		colors = new ModColor(_part.Color);
		paintType = (int)_part.PaintType;
		paintData = new ModPaintData(_part.PaintData);
		conditionStructure = _part.StructureCondition;
		conditionPaint = _part.ConditionPaint;
		livery = _part.Livery;
		liveryStrength = _part.LiveryStrength;
		outsaidRustEnabled = _part.OutsideRustEnabled;
		dent = _part.Dent;
		additionalString = _part.AdditionalString;
		Dust = _part.Dust;
		washFactor = _part.WashFactor;

		var loader = GameData.Instance?.carLoaders != null && carLoaderID >= 0 &&
		             carLoaderID < GameData.Instance.carLoaders.Length
			? GameData.Instance.carLoaders[carLoaderID]
			: null;
		if (loader != null && ClientData.Instance.loadedCars.TryGetValue(carLoaderID, out var car) && car?.CarPartInfo != null)
		{
			foreach (var attachedPart in _part.ConnectedParts)
			{
				var part = loader.GetCarPart(attachedPart);
				// A connected part that cannot be resolved has no key: sending it with the default 0 made every
				// receiver overwrite body panel 0.
				if (part == null) continue;
				if (!PartUpdateHooks.FindBodyPartInDictionary(car, attachedPart, out var key, false)) continue;
				connectedParts.Add(new ModCarPart(part, key, carLoaderID));
			}
		}

		quality = _part.Quality;
	}
}