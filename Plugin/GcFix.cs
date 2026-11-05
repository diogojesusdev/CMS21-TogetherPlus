using System;
using System.Runtime.InteropServices;
using MelonLoader;

[assembly: MelonInfo(typeof(TogetherPlus.GcFix.GcFixPlugin), "TogetherPlus-GcFix", "1.1.0", "TogetherPlus")]

namespace TogetherPlus.GcFix;

/// <summary>
/// Turns the IL2CPP incremental GC off. In that mode an object held only through a store that skipped the write
/// barrier (Unhollower's list/array indexers and struct setters do) is freed while the game still uses it, which
/// corrupted the heap and crashed the game, most visibly when returning to the menu.
///
/// The Boehm collector reads GC_DISABLE_INCREMENTAL once, when IL2CPP starts. Setting it from a plugin is too late on
/// this MelonLoader version (the plugins load after that read), so the plugin also clears the collector's own flag in
/// memory before the mods load. Both paths are verified through il2cpp_gc_is_incremental(), and the in-memory patch
/// only ever writes 0 over a value the collector itself reports as "on".
/// </summary>
public class GcFixPlugin : MelonPlugin
{
	private const string EnvVar = "GC_DISABLE_INCREMENTAL";
	private const int PageReadWrite = 0x04;

	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SetEnvironmentVariable(string name, string value);

	// The C runtime keeps its own copy of the environment; some libraries read it instead of the Win32 block.
	[DllImport("ucrtbase", CharSet = CharSet.Ansi)]
	private static extern int _putenv_s(string name, string value);

	[DllImport("GameAssembly")] private static extern bool il2cpp_gc_is_incremental();

	[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string moduleName);

	[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern IntPtr GetProcAddress(IntPtr module, string procName);

	[DllImport("kernel32", SetLastError = true)]
	private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

	/// <summary>Before IL2CPP starts, for the MelonLoader versions where the plugin still runs first.</summary>
	public override void OnEarlyInitializeMelon()
	{
		// Diagnosis only: proves that the in-memory patch below works on its own.
		if (Environment.GetEnvironmentVariable("TOGETHERPLUS_GC_SKIP_ENV") == "1")
		{
			LoggerInstance.Msg("[GC] Skipping the environment variable (TOGETHERPLUS_GC_SKIP_ENV=1).");
			return;
		}

		var win32 = false;
		var crt = false;
		try
		{
			win32 = SetEnvironmentVariable(EnvVar, "1");
			Environment.SetEnvironmentVariable(EnvVar, "1");
		}
		catch (Exception ex)
		{
			LoggerInstance.Warning($"[GC] Could not set {EnvVar}: {ex.Message}");
		}

		try
		{
			crt = _putenv_s(EnvVar, "1") == 0;
		}
		catch (Exception ex)
		{
			LoggerInstance.Warning($"[GC] Could not set {EnvVar} in the C runtime: {ex.Message}");
		}

		LoggerInstance.Msg($"[GC] {EnvVar}=1 requested ({Environment.GetEnvironmentVariable(EnvVar) ?? "unset"}; win32={win32}, crt={crt}).");
	}

	/// <summary>Before the mods load: IL2CPP is up, so this is the last safe moment to switch the collector off.</summary>
	public override void OnPreModsLoaded()
	{
		try
		{
			if (!il2cpp_gc_is_incremental())
			{
				LoggerInstance.Msg("[GC] Incremental GC is off.");
				return;
			}

			var getter = GetProcAddress(GetModuleHandle("GameAssembly"), "il2cpp_gc_is_incremental");
			var flag = FindIncrementalFlag(getter);
			if (flag == IntPtr.Zero)
			{
				LoggerInstance.Warning("[GC] Incremental GC is still on and its flag could not be found in this GameAssembly build. " +
				                       "Set the launch option `set GC_DISABLE_INCREMENTAL=1 && \"%command%\"` for the game in Steam.");
				return;
			}

			var before = Marshal.ReadInt32(flag);
			if (!WriteInt32(flag, 0) || il2cpp_gc_is_incremental())
			{
				// Wrong address after all: put the collector's value back before saying anything.
				if (before != 0) WriteInt32(flag, before);
				LoggerInstance.Warning("[GC] Could not turn the incremental GC off at runtime. Set the launch option " +
				                       "`set GC_DISABLE_INCREMENTAL=1 && \"%command%\"` for the game in Steam.");
				return;
			}

			LoggerInstance.Msg("[GC] Incremental GC turned off at runtime.");
		}
		catch (Exception ex)
		{
			LoggerInstance.Warning($"[GC] Could not turn the incremental GC off: {ex.Message}");
		}
	}

	/// <summary>
	/// Finds the collector's incremental flag from the getter's own machine code, so the plugin keeps working when the
	/// game is updated: the getter reads exactly one global, with a RIP-relative move or compare.
	/// </summary>
	private IntPtr FindIncrementalFlag(IntPtr getter)
	{
		if (getter == IntPtr.Zero) return IntPtr.Zero;

		var code = new byte[32];
		Marshal.Copy(getter, code, 0, code.Length);

		for (var i = 0; i + 7 <= code.Length; i++)
		{
			var length = 0;
			var displacementOffset = 0;
			if (code[i] == 0x83 && code[i + 1] == 0x3D)
			{
				length = 7; // cmp dword ptr [rip+disp32], imm8  (the flag as an int)
				displacementOffset = i + 2;
			}
			else if (code[i] == 0x0F && code[i + 1] == 0xB6 && (code[i + 2] == 0x05 || code[i + 2] == 0x0D || code[i + 2] == 0x15))
			{
				length = 7; // movzx reg, byte ptr [rip+disp32]  (the flag as a bool)
				displacementOffset = i + 3;
			}
			else if (code[i] == 0x8B && (code[i + 1] == 0x05 || code[i + 1] == 0x0D || code[i + 1] == 0x15))
			{
				length = 6; // mov reg, dword ptr [rip+disp32]
				displacementOffset = i + 2;
			}

			if (length == 0) continue;

			var displacement = BitConverter.ToInt32(code, displacementOffset);
			var target = new IntPtr(getter.ToInt64() + i + length + displacement);
			int current;
			try
			{
				current = Marshal.ReadInt32(target);
			}
			catch (Exception)
			{
				continue;
			}

			// The collector reports "on": the flag is non-zero and fits in one byte (it is a boolean).
			if (current != 0 && (current & ~0xFF) == 0) return target;
		}

		return IntPtr.Zero;
	}

	private static bool WriteInt32(IntPtr address, int value)
	{
		uint oldProtect;
		if (!VirtualProtect(address, (UIntPtr)4, PageReadWrite, out oldProtect)) return false;
		try
		{
			Marshal.WriteInt32(address, value);
		}
		finally
		{
			VirtualProtect(address, (UIntPtr)4, oldProtect, out _);
		}

		return Marshal.ReadInt32(address) == value;
	}

}
