using System;

namespace CMS21Together.Shared.Data.Vanilla.GarageTool;

[Serializable]
public class ModNewMachines
{
	public ModGroupItem GroupOnWheelBalancer;

	public bool WheelWasBalanced;

	public ModGroupItem GroupOnTireChanger;

	public bool GroupOnTireChangerIsMounting;

	public ModGroupItem GroupOnSpringClamp;

	public bool GroupOnSpringClampIsMounting;

	public ModGroupItem GroupOnEngineStand;

	public float EngineStandAngle;

	public ModItem ItemOnBatteryCharger;

	public ModItem ItemOnBrakeLathe;

	public ModNewMachines(NewMachines data)
	{
		GroupOnWheelBalancer = Wrap(data.GroupOnWheelBalancer);
		WheelWasBalanced = data.WheelWasBalanced;
		GroupOnTireChanger = Wrap(data.GroupOnTireChanger);
		GroupOnTireChangerIsMounting = data.GroupOnTireChangerIsMounting;
		GroupOnSpringClamp = Wrap(data.GroupOnSpringClamp);
		GroupOnSpringClampIsMounting = data.GroupOnSpringClampIsMounting;
		GroupOnEngineStand = Wrap(data.GroupOnEngineStand);
		EngineStandAngle = data.EngineStandAngle;
		ItemOnBatteryCharger = data.ItemOnBatteryCharger != null ? new ModItem(data.ItemOnBatteryCharger) : new ModItem();
	}

	// An empty machine slot is null in the game; that is normal, not an error.
	private static ModGroupItem Wrap(GroupItem group)
	{
		return group != null ? new ModGroupItem(group) : new ModGroupItem();
	}

	public NewMachines ToGame()
	{
		var a = new NewMachines();
		a.GroupOnWheelBalancer = GroupOnWheelBalancer.ToGame();
		a.WheelWasBalanced = WheelWasBalanced;
		a.GroupOnTireChanger = GroupOnTireChanger.ToGame();
		a.GroupOnTireChangerIsMounting = GroupOnTireChangerIsMounting;
		a.GroupOnSpringClamp = GroupOnSpringClamp.ToGame();
		a.GroupOnSpringClampIsMounting = GroupOnSpringClampIsMounting;
		a.GroupOnEngineStand = GroupOnEngineStand.ToGame();
		a.EngineStandAngle = EngineStandAngle;
		a.ItemOnBatteryCharger = ItemOnBatteryCharger.ToGame();
		return a;
	}
}