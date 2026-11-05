using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CMS21Together.Shared.Updates;
using NUnit.Framework;

namespace CMS21Together.Tests;

/// <summary>
/// Rules that unit tests cannot execute because the code needs the real game, but that a source scan can still
/// guard: the IL2CPP write barrier, the single logging gateway, the installer's encoding, and the wording that
/// couples the host's rejection to the updater's version lookup. Each of these has already caused a shipped bug.
/// </summary>
[TestFixture]
public class SourceInvariantTests
{
	private static string RepoRoot
	{
		get
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null)
			{
				if (File.Exists(Path.Combine(directory.FullName, "CMS21-Together.csproj"))) return directory.FullName;
				directory = directory.Parent;
			}

			return null;
		}
	}

	/// <summary>The shipped code: test and harness code is not in the mod the player runs.</summary>
	private static readonly string[] SkippedFolders = { "obj", "bin", ".git", "dist", "Tests" };

	private static IEnumerable<string> SourceFiles(string root) =>
		Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
			.Where(file => !file.Split(Path.DirectorySeparatorChar).Any(SkippedFolders.Contains));

	private static string Read(string root, params string[] parts) => File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));

	[SetUp]
	public void RequireTheSourceTree()
	{
		if (RepoRoot == null) Assert.Ignore("The repository root was not found next to the test output; the source scan needs the checkout.");
	}

	[Test]
	public void Il2CppArraysAreOnlyWrittenThroughTheBarrierHelper()
	{
		// An indexer write skips the GC write barrier: the incremental GC frees a live object and the game crashes
		// later, far from here. Il2CppArrays.Set/Of are the only sanctioned writers.
		var declaration = new Regex(@"Il2Cpp(?:Reference|String)Array(?:<[^>\r\n]+>)?\s+(?<name>\w+)", RegexOptions.Compiled);
		var offenders = new List<string>();

		foreach (var file in SourceFiles(RepoRoot))
		{
			if (file.EndsWith(Path.Combine("Shared", "Il2CppArrays.cs"), StringComparison.OrdinalIgnoreCase)) continue;

			var text = File.ReadAllText(file);
			foreach (var name in declaration.Matches(text).Cast<Match>().Select(m => m.Groups["name"].Value).Distinct())
			{
				var write = new Regex(@"(?<![\w.])" + Regex.Escape(name) + @"\s*\[[^\]\r\n]*\]\s*=(?!=)", RegexOptions.Compiled);
				foreach (Match match in write.Matches(text))
					offenders.Add($"{Path.GetFileName(file)}: {match.Value.Trim()}");
			}
		}

		Assert.That(offenders, Is.Empty, "an IL2CPP array written through its indexer skips the GC write barrier: use Il2CppArrays.Set/Of");
	}

	[Test]
	public void OnlyTheLogFacadeTalksToMelonLoader()
	{
		// Users set the level in MelonPreferences.cfg, and Log throttles and formats; a direct MelonLogger call
		// ignores both and cannot be silenced.
		var allowed = Path.Combine("Shared", "MelonLogSink.cs");
		var offenders = SourceFiles(RepoRoot)
			.Where(file => !file.EndsWith(allowed, StringComparison.OrdinalIgnoreCase))
			.Where(file => File.ReadAllText(file).Contains("MelonLogger"))
			.Select(file => Path.GetFileName(file))
			.ToList();

		Assert.That(offenders, Is.Empty, "use Shared/Log.cs instead of MelonLogger directly");
	}

	[Test]
	public void TheInstallerScriptsStayBomFreeAscii()
	{
		// rc1 shipped install.ps1 with a UTF-8 BOM and "irm | iex" refused to parse it.
		foreach (var name in new[] { "Install.bat", "install.ps1", "uninstall.ps1" })
		{
			var bytes = File.ReadAllBytes(Path.Combine(RepoRoot, name));

			Assert.That(bytes.Length, Is.GreaterThan(3), $"{name} is empty");
			Assert.That(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, Is.False,
				$"{name} must stay BOM-free: 'irm | iex' cannot parse a byte order mark");
			Assert.That(bytes.Any(b => b > 0x7F), Is.False, $"{name} must stay ASCII: it runs in a console with any code page");
		}
	}

	[Test]
	public void TheInstallerStillInstallsTheTwoPluginsTheUpdaterCannotReplaceItself()
	{
		// The auto-updater only replaces Mods\CMS21-Together.dll: the GC fix and the updater itself change only
		// through install.ps1, so the installer has to keep shipping both.
		var script = Read(RepoRoot, "install.ps1");

		Assert.That(script, Does.Contain(@"Plugins\TogetherPlus.GcFix.dll"));
		Assert.That(script, Does.Contain(@"Plugins\TogetherPlus.Updater.dll"));
		Assert.That(script, Does.Contain("CMS21-Together.version"), "the updater reads the installed version from this file");
	}

	[Test]
	public void TheBuildStillWritesTheChecksumTheUpdaterRequires()
	{
		// A release without the .sha256 asset is refused by the updater, so every player would stay on the old build.
		var build = Read(RepoRoot, "build.ps1");

		Assert.That(build, Does.Contain("Get-FileHash"));
		Assert.That(build, Does.Contain(".sha256"));
	}

	[Test]
	public void TheHostNeverReadsAClientSlotItDidNotCheck()
	{
		// connectedClients[slot].x throws for a packet that arrives after its player disconnected, and the router counts
		// it as a handler error towards the 50-error kick. TryGetValue is the pattern every handler should use.
		var read = new Regex(@"connectedClients\s*\[[^\]\r\n]+\]\s*\.", RegexOptions.Compiled);
		var offenders = SourceFiles(RepoRoot)
			.Where(file => file.Contains(Path.Combine("ServerSide") + Path.DirectorySeparatorChar))
			.SelectMany(file => read.Matches(File.ReadAllText(file)).Cast<Match>().Select(match => $"{Path.GetFileName(file)}: {match.Value}"))
			.ToList();

		Assert.That(offenders, Is.Empty, "use connectedClients.TryGetValue before touching a client");
	}

	[Test]
	public void TheHandshakeRejectionStillParsesAsTheHostsVersion()
	{
		// NetHost rejects with "Mod version mismatch: host has TogetherPlus-<version>, ..."; the client extracts that
		// version and asks the updater for exactly that tag. Changing either side silently disables host matching.
		var source = Read(RepoRoot, "Shared", "Net", "NetHost.cs");
		var start = source.IndexOf("Mod version mismatch", StringComparison.Ordinal);
		Assert.That(start, Is.GreaterThanOrEqualTo(0), "the wording the client parses is gone from NetHost");

		var end = source.IndexOf('"', start);
		Assert.That(end, Is.GreaterThan(start));

		var sentence = source.Substring(start, end - start)
			.Replace("{modVersion}", "TogetherPlus-0.5.0-rc19")
			.Replace("{hello.ModVersion}", "TogetherPlus-0.5.0-rc20");

		Assert.That(sentence, Does.Not.Contain("{"), "the rejection template changed shape; update this test and VersionText together");
		Assert.That(VersionText.HostVersionIn(sentence), Is.EqualTo("0.5.0-rc19"),
			"the client must find the host's version in the rejection it receives");
	}

	[Test]
	public void TheGarageCarLoadHookNeverSkipsTheGamesLoad()
	{
		// CarLoader.LoadCarFromFile() is StartCoroutine(LoadCarFromFile(name)). A prefix that returns false on the
		// (string) overload leaves StartCoroutine with a null enumerator and Unity throws "routine is null", which
		// ends the garage car loop after the first loader - whose car was already deleted. The hook may only watch.
		var source = Read(RepoRoot, "ClientSide", "Data", "Garage", "Car", "CarSpawnHooks.cs");
		var start = source.IndexOf("public static bool LoadCarFromFileHook", StringComparison.Ordinal);
		Assert.That(start, Is.GreaterThanOrEqualTo(0), "the garage car load hook is gone; update this test");

		var next = source.IndexOf("[Harmony", start, StringComparison.Ordinal);
		var body = next > start ? source.Substring(start, next - start) : source.Substring(start);

		Assert.That(body, Does.Not.Contain("return false"),
			"the game starts the enumerator carried by this method: skipping it makes StartCoroutine throw 'routine is null'");
	}

	[Test]
	public void ThePacketEnumStaysOrdinal()
	{
		// The pinned table in PacketTypesTests only protects the wire if the enum has no explicit numbers: an
		// assignment would decouple the value from its position in the list.
		var source = Read(RepoRoot, "Shared", "PacketTypes.cs");
		var assignments = Regex.Matches(source, @"^\s*[A-Za-z_]\w*\s*=(?!=)", RegexOptions.Multiline)
			.Cast<Match>()
			.Select(m => m.Value.Trim())
			.ToList();

		Assert.That(assignments, Is.Empty, "PacketTypes must stay a plain list: the wire type is the ordinal");
	}
}
