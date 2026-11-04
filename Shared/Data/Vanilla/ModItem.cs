using System;
using CMS21Together.Shared.Data.Vanilla.Cars;
using MelonLoader;
using UnhollowerBaseLib;
using UnityEngine;

namespace CMS21Together.Shared.Data.Vanilla;

[Serializable]
public class ModItem
{
	public ModColor Color;
	public float Condition;
	public float ConditionToShow;
	public float Dent;
	public ModGearboxData GearboxData;
	public bool IsExamined;
	public bool IsPainted;
	public bool IsTinted;
	public string Livery;
	public float LiveryStrength;

	public ModLPData LPData;

	public ModMountObjectData MountObjectData;
	public string NormalID;
	public bool OutsideRustEnabled;
	public ModPaintData PaintData;
	public ModPaintType PaintType;
	public int Quality;
	public int RepairAmount;
	public ModColor TintColor;
	public ModTuningData TuningData;
	public float WashFactor;
	public ModWheelData WheelData;
	public string ID;
	public long UID;

	public ModItem()
	{
	}

	public ModItem(Item item)
	{
		if (item != null)
		{
			Color = new ModColor(item.Color.GetColor());
			Condition = item.Condition;
			Dent = item.Dent;
			//this.GearboxData = item.GearboxData; // TODO: Handle class
			IsExamined = item.IsExamined;
			IsPainted = item.IsPainted;
			IsTinted = item.IsTinted;
			Livery = item.Livery;
			LiveryStrength = item.LiveryStrength;
			if (item.LPData != null) LPData = new ModLPData(item.LPData);
			if (item.MountObjectData != null) MountObjectData = new ModMountObjectData(item.MountObjectData);
			NormalID = item.NormalID;
			OutsideRustEnabled = item.OutsideRustEnabled;
			PaintData = new ModPaintData(item.PaintData);
			PaintType = (ModPaintType)item.PaintType;
			Quality = item.Quality;
			RepairAmount = item.RepairAmount;
			TintColor = new ModColor(item.TintColor.GetColor());
			TuningData = new ModTuningData(item.tuningData);
			WashFactor = item.WashFactor;
			WheelData = new ModWheelData(item.WheelData);
			ID = item.ID;
			UID = item.UID;
		}
		else
		{
			Log.Error("Error: Item is null in ModItem constructor.");
		}
	}

	public Item ToGame()
	{
		var original = new Item();

		// Phase 6b: colours and plate text were not applied, so painted items arrived unpainted elsewhere.
		if (Color != null) original.Color = new CustomColor { Color = new[] { Color.r, Color.g, Color.b, Color.a } };
		if (LPData != null) original.LPData = LPData.ToGame();
		original.Condition = Condition;
		original.Dent = Dent;
		original.IsExamined = IsExamined;
		original.IsPainted = IsPainted;
		original.IsTinted = IsTinted;
		original.Livery = Livery;
		original.LiveryStrength = LiveryStrength;
		original.NormalID = NormalID;
		original.OutsideRustEnabled = OutsideRustEnabled;
		original.PaintData = new ModPaintData().ToGame(PaintData);
		if (MountObjectData != null) original.MountObjectData = MountObjectData.ToGame();
		original.PaintType = (PaintType)PaintType;
		original.Quality = Quality;
		original.RepairAmount = RepairAmount;
		if (TintColor != null) original.TintColor = new CustomColor { Color = new[] { TintColor.r, TintColor.g, TintColor.b, TintColor.a } };
		original.WashFactor = WashFactor;
		if (WheelData != null) original.WheelData = new ModWheelData().ToGame(WheelData);
		original.ID = ID;
		original.UID = UID;
		return original;
	}
}