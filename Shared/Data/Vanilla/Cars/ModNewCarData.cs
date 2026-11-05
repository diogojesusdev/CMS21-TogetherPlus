using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using CMS21Together.Shared.Data.Vanilla.GarageTool;
using MelonLoader;
using UnhollowerBaseLib;

namespace CMS21Together.Shared.Data.Vanilla.Cars;

[Serializable]
public class ModNewCarData
{
	public float AdditionalCarRot;
	public List<ModBodyPartData> BodyPartsData;
	public ModBonusPartsData BonusPartsData;
	public ModCarInfoData CarInfoData;
	public string carToLoad;
	public ModColor color;
	public int configVersion;
	public bool customerCar;
	public int jobID;
	public ModTuningData ecuData;
	public ModEngineData EngineData;
	public string engineSwap;
	public ModColor factoryColor;
	public ModPaintType factoryPaintType;
	public float finalDriveRatio;
	public ModFluidsData FluidsData;
	public List<float> gearRatio;
	public bool HasCustomPaintType;
	public ModHeadLampAlignmentData HeadLampLeftAlignmentData;
	public ModHeadLampAlignmentData HeadlampRightAlignmentData;
	public int index;
	public ModLicencePlatesData LicencePlatesData;
	public bool LightsOn;
	public int measuredDragIndex;
	public int orderConnection;
	public ModPaintData PaintData;
	public List<ModPartData> PartData;
	public List<int> rimsSize;
	public List<int> tiresET;
	public List<int> tiresSize;
	public ModToolsData ToolsData;
	public string UId;
	public ModWheelsAlignmentData wheelsAlignmentData;
	public List<int> wheelsWidth;

	public int carPosition;

	public ModNewCarData(NewCarData newCarData, int placeNo = 0, int _jobID = -1)
	{
		carPosition = placeNo;

		if (newCarData != null)
		{
			AdditionalCarRot = newCarData.AdditionalCarRot;

			if (newCarData.BodyPartsData != null)
				BodyPartsData = CopyList(newCarData.BodyPartsData, part => new ModBodyPartData(part), nameof(BodyPartsData));

			if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("[ModNewCarData] body parts read; reading bonus parts, car info and engine data.");

			if (newCarData.BonusPartsData != null) 
				BonusPartsData = new ModBonusPartsData(newCarData.BonusPartsData);


			CarInfoData = new ModCarInfoData(newCarData.CarInfoData);


			carToLoad = newCarData.carToLoad;
			configVersion = newCarData.configVersion;
			customerCar = newCarData.customerCar;
			jobID = _jobID;

			ecuData = new ModTuningData(newCarData.ecuData);

			if (newCarData.color != null && newCarData.color.Length >= 4) color = new ModColor(newCarData.color[0], newCarData.color[1], newCarData.color[2], newCarData.color[3]);


			EngineData = new ModEngineData(newCarData.EngineData);


			engineSwap = newCarData.engineSwap;

			if (newCarData.factoryColor != null && newCarData.factoryColor.Length >= 4) factoryColor = new ModColor(newCarData.factoryColor[0], newCarData.factoryColor[1], newCarData.factoryColor[2], newCarData.factoryColor[3]);

			factoryPaintType = (ModPaintType)newCarData.factoryPaintType;
			finalDriveRatio = newCarData.finalDriveRatio;

			if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("[ModNewCarData] engine read; reading fluids, wheel arrays, licence plates and paint.");

			FluidsData = new ModFluidsData(newCarData.FluidsData);

			gearRatio = newCarData.gearRatio?.ToList();
			HasCustomPaintType = newCarData.HasCustomPaintType;

			HeadLampLeftAlignmentData = new ModHeadLampAlignmentData(newCarData.HeadlampLeftAlignmentData);
			HeadlampRightAlignmentData = new ModHeadLampAlignmentData(newCarData.HeadlampRightAlignmentData);

			index = newCarData.index;

			LicencePlatesData = new ModLicencePlatesData(newCarData.LicensePlatesData);

			LightsOn = newCarData.LightsOn;
			measuredDragIndex = newCarData.measuredDragIndex;
			orderConnection = newCarData.orderConnection;

			PaintData = new ModPaintData(newCarData.PaintData);

			if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("[ModNewCarData] paint read; reading the part list.");

			if (newCarData.PartData != null)
				PartData = CopyList(newCarData.PartData, part => new ModPartData(part), nameof(PartData));

			rimsSize = newCarData.rimsSize?.ToList();
			tiresET = newCarData.tiresET?.ToList();
			tiresSize = newCarData.tiresSize?.ToList();

			if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("[ModNewCarData] part list read; reading tools and alignment data.");

			ToolsData = new ModToolsData(newCarData.TooolsData);

			UId = newCarData.UId;


			wheelsAlignmentData = new ModWheelsAlignmentData(newCarData.WheelsAlignment);

			wheelsWidth = newCarData.wheelsWidth?.ToList();
		}
		else
		{
			Log.Error("Error: NewCarData is null in ModNewCarData constructor.");
		}
	}

	/// <summary>
	/// Copies a game list without List.ToArray. A freshly loaded job car can have a list whose backing array is still
	/// null; ToArray then throws in native code (ArgumentNullException) and the whole car was lost. Unhollower's
	/// List._items getter cannot detect that (it wraps the address of the field, so it is never null), so the array
	/// pointer is read from the field's slot. A list without storage is copied as empty (with a warning naming it), so
	/// the rest of the car still syncs.
	/// </summary>
	private static List<TOut> CopyList<TIn, TOut>(Il2CppSystem.Collections.Generic.List<TIn> source, Func<TIn, TOut> convert, string name)
		where TIn : Il2CppObjectBase
	{
		var result = new List<TOut>();
		if (source == null) return result;

		int count;
		IntPtr arrayPointer;
		try
		{
			count = source.Count;
			// List._items.Pointer is the address of the field; the field holds the real array (or null).
			var slot = source._items?.Pointer ?? IntPtr.Zero;
			arrayPointer = slot == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(slot);
		}
		catch (Exception ex)
		{
			Log.Warn($"[ModNewCarData] {name} could not be read and was left empty: {ex.Message}");
			return result;
		}

		if (arrayPointer == IntPtr.Zero)
		{
			Log.Warn($"[ModNewCarData] {name} reports {count} item(s) but has no storage; left empty so the car still syncs.");
			return result;
		}

		try
		{
			var items = new Il2CppReferenceArray<TIn>(arrayPointer);
			var length = Math.Min(count, items.Length);
			if (count > items.Length)
				Log.Warn($"[ModNewCarData] {name} reports {count} item(s) but its storage holds {items.Length}; reading {length}.");

			for (var i = 0; i < length; i++)
			{
				try
				{
					var item = items[i];
					if (item != null) result.Add(convert(item));
				}
				catch (Exception ex)
				{
					Log.Warn($"[ModNewCarData] {name}[{i}] could not be read: {ex.Message}");
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warn($"[ModNewCarData] {name} could not be read completely ({result.Count} kept): {ex.Message}");
		}

		return result;
	}

	/// <summary>
	/// True when this car cannot be handed to the game: the reason is a player-readable phrase for the log. Checked on
	/// the receiving side before any game object is built from it (a native crash cannot be caught, see CarPayloadRules).
	/// </summary>
	public bool IfBroken(out string reason)
	{
		reason = CarPayloadRules.Problem(
			carToLoad,
			BodyPartsData?.Count ?? 0,
			PartData?.Count ?? 0,
			CarPayloadRules.AllPresent(BodyPartsData) && CarPayloadRules.AllPresent(PartData),
			Widest(wheelsWidth, tiresET, tiresSize, rimsSize),
			gearRatio?.Count ?? 0);

		return reason != null;
	}

	/// <summary>The length of the longest of the optional wheel arrays (0 when all are null).</summary>
	private static int Widest(params List<int>[] arrays)
	{
		var widest = 0;
		foreach (var array in arrays)
			if (array != null && array.Count > widest)
				widest = array.Count;
		return widest;
	}

	public NewCarData ToGame()
	{
		var newData = new NewCarData();

		newData.AdditionalCarRot = AdditionalCarRot;
		if (BodyPartsData != null)
		{
			newData.BodyPartsData = new Il2CppSystem.Collections.Generic.List<BodyPartData>();
			foreach (var bodyPartData in BodyPartsData)
				if (bodyPartData != null)
					newData.BodyPartsData.Add(bodyPartData.ToGame());
		}

		newData.BonusPartsData = BonusPartsData.ToGame();
		newData.CarInfoData = CarInfoData.ToGame(CarInfoData);
		newData.carToLoad = carToLoad;
		newData.configVersion = configVersion;
		newData.customerCar = customerCar;
		newData.ecuData = ecuData.ToGame();
		newData.color = color != null ? new[] { color.r, color.g, color.b, color.a } : null;
		newData.EngineData = EngineData.ToGame();
		newData.engineSwap = engineSwap;
		newData.factoryColor = factoryColor != null ? new[] { factoryColor.r, factoryColor.g, factoryColor.b, factoryColor.a } : null;
		newData.factoryPaintType = (PaintType)factoryPaintType;
		newData.finalDriveRatio = finalDriveRatio;
		newData.FluidsData = FluidsData.ToGame();
		newData.gearRatio = gearRatio != null ? gearRatio.ToArray() : null;
		newData.HasCustomPaintType = HasCustomPaintType;
		newData.HeadlampLeftAlignmentData = HeadLampLeftAlignmentData.ToGame();
		newData.HeadlampRightAlignmentData = HeadlampRightAlignmentData.ToGame();
		newData.index = index;
		newData.LicensePlatesData = LicencePlatesData.ToGame();
		newData.LightsOn = LightsOn;
		newData.measuredDragIndex = measuredDragIndex;
		newData.orderConnection = orderConnection;
		newData.PaintData = PaintData.ToGame(PaintData);
		if (PartData != null)
		{
			newData.PartData = new Il2CppSystem.Collections.Generic.List<PartData>();
			foreach (var modPartData in PartData)
				if (modPartData != null)
					newData.PartData.Add(modPartData.ToGame());
		}

		newData.rimsSize = rimsSize != null ? rimsSize.ToArray() : null;
		newData.tiresET = tiresET != null ? tiresET.ToArray() : null;
		newData.tiresSize = tiresSize != null ? tiresSize.ToArray() : null;
		newData.TooolsData = ToolsData.ToGame();
		newData.UId = UId;
		newData.WheelsAlignment = wheelsAlignmentData.ToGame(wheelsAlignmentData);
		newData.wheelsWidth = wheelsWidth != null ? wheelsWidth.ToArray() : null;

		return newData;
	}
}