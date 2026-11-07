using System;

namespace CMS21Together.Shared.Data.Vanilla.Cars;

[Serializable]
public class ModFluidData
{
	public float Level;
	public float Condition;
	public ModCarFluid CarFluid;

	public ModFluidData(FluidData data)
	{
		Level = data.Level;
		Condition = data.Condition;
		if (data.CarFluid != null)
			CarFluid = new ModCarFluid(data.CarFluid);
	}

	/// <summary>Engine oil has no CarFluid in the game, so the packet is tagged with one; every receiver drops an untagged fluid.</summary>
	public static ModFluidData ForOil(FluidData oil)
	{
		var data = new ModFluidData(oil);
		data.CarFluid = new ModCarFluid(ModCarFluidType.EngineOil);
		return data;
	}

	public ModFluidData WithoutCarFluid()
	{
		var copy = (ModFluidData)MemberwiseClone();
		copy.CarFluid = null;
		return copy;
	}

	public FluidData ToGame()
	{
		var data = new FluidData();
		data.Level = Level;
		data.Condition = Condition;
		data.CarFluid = CarFluid != null ? CarFluid.ToGame() : null;

		return data;
	}
}