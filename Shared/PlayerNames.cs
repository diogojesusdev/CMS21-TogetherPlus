using System.Text;

namespace CMS21Together.Shared;

/// <summary>Cleans player names received from the network before they reach the UI or logs.</summary>
public static class PlayerNames
{
	public const int MaxLength = 24;

	public static string Sanitize(string name, int slot)
	{
		var builder = new StringBuilder(MaxLength);
		if (name != null)
		{
			foreach (var c in name.Trim())
			{
				if (char.IsControl(c) || c == '<' || c == '>') continue;
				if (builder.Length >= MaxLength) break;
				builder.Append(c);
			}
		}
		var clean = builder.ToString().Trim();
		return clean.Length == 0 ? $"Player {slot}" : clean;
	}

	/// <summary>The name to pre-fill: the saved one if the player picked it, else the Steam name.</summary>
	public static string Default(string saved, string steamName)
	{
		var unset = string.IsNullOrWhiteSpace(saved) || saved.Trim() == "player";
		if (!unset || string.IsNullOrWhiteSpace(steamName)) return saved;
		return Sanitize(steamName, 1);
	}
}