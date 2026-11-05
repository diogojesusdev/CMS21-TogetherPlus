using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace CMS21Together.Shared.Updates;

/// <summary>One release from the GitHub releases API (only the fields the version picker needs).</summary>
[DataContract]
public sealed class ReleaseInfo
{
	[DataMember(Name = "tag_name")] public string TagName;
	[DataMember(Name = "prerelease")] public bool PreRelease;
	[DataMember(Name = "draft")] public bool Draft;
	[DataMember(Name = "assets")] public ReleaseAsset[] Assets;

	/// <summary>Version inside the tag ("v0.5.0-rc18" -> "0.5.0-rc18").</summary>
	public string Version { get; internal set; }

	/// <summary>Both the package and its checksum are attached, so the updater can install it.</summary>
	public bool HasPackage { get; internal set; }
}

[DataContract]
public sealed class ReleaseAsset
{
	[DataMember(Name = "name")] public string Name;
}

/// <summary>Parses the repository's releases for the in-game version picker.</summary>
public static class ReleaseCatalog
{
	public const string Endpoint =
		"https://api.github.com/repos/diogojesusdev/CMS21-TogetherPlus/releases?per_page=100";

	/// <summary>
	/// Versions with a usable package, newest first. Drafts, unparseable tags and releases without the zip or its
	/// .sha256 are skipped: the updater would refuse them anyway.
	/// </summary>
	public static List<ReleaseInfo> Parse(string json)
	{
		var result = new List<ReleaseInfo>();
		if (string.IsNullOrWhiteSpace(json)) return result;

		List<ReleaseInfo> raw;
		try
		{
			using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
			raw = (List<ReleaseInfo>)new DataContractJsonSerializer(typeof(List<ReleaseInfo>)).ReadObject(stream);
		}
		catch (Exception ex)
		{
			throw new FormatException("The GitHub release list could not be read: " + ex.Message, ex);
		}
		if (raw == null) return result;

		foreach (var release in raw)
		{
			if (release == null || release.Draft) continue;
			var version = VersionText.FromTag(release.TagName);
			if (version == null) continue;

			var zip = "TogetherPlus-" + version + ".zip";
			var assets = release.Assets ?? Array.Empty<ReleaseAsset>();
			var hasZip = assets.Any(a => a != null && string.Equals(a.Name, zip, StringComparison.Ordinal));
			var hasHash = assets.Any(a => a != null && string.Equals(a.Name, zip + ".sha256", StringComparison.Ordinal));
			if (!hasZip || !hasHash) continue;

			release.Version = version;
			release.HasPackage = true;
			result.Add(release);
		}

		result.Sort((a, b) => VersionText.Compare(b.Version, a.Version));
		return result;
	}
}