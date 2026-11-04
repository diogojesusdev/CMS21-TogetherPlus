using System;
using System.IO;

namespace CMS21Together.Shared;

/// <summary>Together+ data that must survive reinstalling or verifying the game lives next to the vanilla saves.</summary>
public static class PlusPaths
{
	public static readonly string GameData = Environment.ExpandEnvironmentVariables(
		@"%USERPROFILE%\AppData\LocalLow\Red Dot Games\Car Mechanic Simulator 2021");

	public static string Root => Path.Combine(GameData, "TogetherPlus");
}