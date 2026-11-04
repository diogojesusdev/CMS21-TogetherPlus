using System;

namespace CMS21Together.Shared.Data.Vanilla;

/// <summary>A licence plate item's text (Phase 6b: was empty, so plates arrived blank on other players' games).</summary>
[Serializable]
public class ModLPData
{
	public string Name;
	public string Custom;

	public ModLPData() { }

	public ModLPData(LPData data)
	{
		if (data == null) return;
		Name = data.Name;
		Custom = data.Custom;
	}

	public LPData ToGame() => new() { Name = Name ?? string.Empty, Custom = Custom ?? string.Empty };
}