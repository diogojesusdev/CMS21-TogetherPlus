using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace CMS21Together.Shared.Data;

[Serializable]
public class PlayerInfo : IPlayerProfile
{
	public string id;

	/// <summary>Steam id this profile belongs to (0 = direct IP or not yet linked).</summary>
	public ulong steamId;

	/// <summary>SHA-256 of the token issued to a direct-IP player; never sent over the network.</summary>
	public string tokenHash;
	public Vector3Serializable position;
	public QuaternionSerializable rotation;

	public int playerExp = 0;
	public int playerLevel = 0;
	public int skillPoints = 0;

	public Dictionary<string, List<bool>> skillsInfo = new Dictionary<string, List<bool>>();

	public PlayerInfo(string _id, Vector3 _position, Quaternion _rotation, int _exp, int _lvl, int points)
	{
		id = _id;
		position = new Vector3Serializable(_position);
		rotation = new QuaternionSerializable(_rotation);

		playerExp = _exp;
		playerLevel = _lvl;
		skillPoints = points;
	}
	
	string IPlayerProfile.ProfileId { get => id; set => id = value; }
	ulong IPlayerProfile.SteamId { get => steamId; set => steamId = value; }
	string IPlayerProfile.TokenHash { get => tokenHash; set => tokenHash = value; }

	public void UpdateStats(Vector3Serializable _position, QuaternionSerializable _rotation, int _exp, int _lvl, int points)
	{
		playerExp = _exp;
		playerLevel = _lvl;
		skillPoints = points;
		position = _position;
		rotation = _rotation;
	}
	public void UpdateSkill(string skill_ID, List<bool> skill)
	{
		skillsInfo[skill_ID] = skill;
		// Audit A18: no save per unlock; the next autosave, leave or exit writes it.
		PlusSaves.Scheduler.MarkDirty();
	}
}