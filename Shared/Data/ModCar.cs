using System;
using System.Collections.Generic;

namespace CMS21Together.Shared.Data;

[Serializable]
public class ModCar
{
	public int carLoaderID;
	public string carID;
	public int configVersion;
	public int carPosition;
	public bool customerCar;

	public bool isReady;
	public bool needResync;
	public bool isFromServer;

	[NonSerialized] public ModCarPartInfo CarPartInfo;

	public ModCar(int _carLoaderID, string _carID, int _configVersion, int _carPosition = -1, bool _customerCar = false)
	{
		carLoaderID = _carLoaderID;
		carID = _carID;
		CarPartInfo = new ModCarPartInfo();
		carPosition = _carPosition;
		configVersion = _configVersion;
		customerCar = _customerCar;

		isFromServer = false;
		isReady = false;
	}
}

public class ModCarPartInfo
{
	public Dictionary<int, CarPart> BodyPartsReferences = new();
	public Dictionary<int, PartScript> DriveshaftPartsReferences = new();
	public Dictionary<int, PartScript> EnginePartsReferences = new();
	public Dictionary<int, List<PartScript>> OtherPartsReferences = new();
	public Dictionary<int, List<PartScript>> SuspensionPartsReferences = new();

	/// <summary>
	/// Lets go of every game part this car referenced. A loaded car keeps well over a hundred IL2CPP wrappers alive,
	/// each with its own GC handle; holding them while Unity unloads the garage is a use-after-free in the unload's
	/// reference walk (see Shutdown).
	/// </summary>
	public void Clear()
	{
		BodyPartsReferences.Clear();
		DriveshaftPartsReferences.Clear();
		EnginePartsReferences.Clear();
		foreach (var parts in OtherPartsReferences.Values) parts?.Clear();
		OtherPartsReferences.Clear();
		foreach (var parts in SuspensionPartsReferences.Values) parts?.Clear();
		SuspensionPartsReferences.Clear();
	}
}