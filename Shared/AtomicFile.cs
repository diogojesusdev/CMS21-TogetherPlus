using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CMS21Together.Shared;

/// <summary>Crash-safe file writes: a reader sees either the old file or the complete new one, never a torn mix.</summary>
public static class AtomicFile
{
	public static void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text ?? string.Empty));

	public static void WriteAllBytes(string path, byte[] bytes, string backupPath = null)
	{
		if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is empty.", nameof(path));
		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

		var temp = path + ".tmp";
		using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
		{
			stream.Write(bytes, 0, bytes.Length);
			stream.Flush(true);
		}

		// The previous version is copied, not moved: the target exists at every moment.
		if (backupPath != null && File.Exists(path)) File.Copy(path, backupPath, true);
		Rename(temp, path);
	}

	/// <summary>
	/// One atomic rename over the target (File.Replace is several steps: a kill between them leaves no target at all,
	/// which the save kill test found).
	/// </summary>
	private static void Rename(string from, string to)
	{
		if (Environment.OSVersion.Platform == PlatformID.Win32NT)
		{
			try
			{
				if (MoveFileEx(Path.GetFullPath(from), Path.GetFullPath(to), MoveFileReplaceExisting | MoveFileWriteThrough)) return;
				throw new IOException($"Could not replace {to}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
			}
			catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
			{
				// Not a real Windows kernel: fall through.
			}
		}
		if (File.Exists(to)) File.Replace(from, to, null, true);
		else File.Move(from, to);
	}

	private const int MoveFileReplaceExisting = 0x1;
	private const int MoveFileWriteThrough = 0x8;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

	/// <summary>Reads a file, or returns null when it does not exist.</summary>
	public static string ReadAllTextOrNull(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
}