using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnhollowerBaseLib;

namespace CMS21Together.Shared.Safety;

/// <summary>
/// The native code behind the game's methods. IL2CPP folds identical method bodies into one function (39 windows'
/// <c>HideAction</c> are one), and Harmony on MelonLoader 0.5.7 detours that function: a patch on one of them runs for
/// all of them, with an <c>__instance</c> of the wrong type. This finds those methods before they are patched.
/// </summary>
public static class NativeCode
{
	[DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr il2cpp_domain_get_assemblies(IntPtr domain, ref uint size);

	private static Dictionary<IntPtr, int> users;

	/// <summary>The method's native code, or zero when it is not a game (IL2CPP) method.</summary>
	public static IntPtr Of(MethodBase method)
	{
		try
		{
			var field = method == null ? null : UnhollowerUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
			if (field == null) return IntPtr.Zero;
			var info = (IntPtr)field.GetValue(null);
			// Il2CppMethodInfo starts with methodPointer.
			return info == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(info);
		}
		catch (Exception)
		{
			return IntPtr.Zero;
		}
	}

	/// <summary>How many game methods run <paramref name="method"/>'s native code (0 when unknown).</summary>
	public static int Users(MethodBase method)
	{
		var code = Of(method);
		if (code == IntPtr.Zero) return 0;
		return Index().TryGetValue(code, out var count) ? count : 0;
	}

	/// <summary>A few of the other methods that run <paramref name="method"/>'s native code, for the log.</summary>
	public static List<string> Others(MethodBase method, int max)
	{
		var names = new List<string>();
		var code = Of(method);
		if (code == IntPtr.Zero) return names;
		Visit((klass, info, pointer) =>
		{
			if (pointer != code || names.Count >= max) return;
			var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) + "." +
			           Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(info));
			if (method.DeclaringType == null || name != method.DeclaringType.Name + "." + method.Name) names.Add(name);
		});
		return names;
	}

	private static Dictionary<IntPtr, int> Index()
	{
		if (users != null) return users;
		var index = new Dictionary<IntPtr, int>();
		var watch = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			Visit((_, _, pointer) => index[pointer] = index.TryGetValue(pointer, out var count) ? count + 1 : 1);
			Log.Info($"[Patches] Indexed the native code of the game's methods ({index.Count} functions, {watch.ElapsedMilliseconds} ms).");
		}
		catch (Exception ex)
		{
			index.Clear();
			Log.Warn("[Patches] Could not index the game's native code; shared-code patches are not checked: " + ex.Message);
		}
		return users = index;
	}

	private static void Visit(Action<IntPtr, IntPtr, IntPtr> visit)
	{
		uint assemblies = 0;
		var list = il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref assemblies);
		for (var a = 0; a < assemblies; a++)
		{
			var image = IL2CPP.il2cpp_assembly_get_image(Marshal.ReadIntPtr(list, a * IntPtr.Size));
			var classes = IL2CPP.il2cpp_image_get_class_count(image);
			for (uint c = 0; c < classes; c++)
			{
				var klass = IL2CPP.il2cpp_image_get_class(image, c);
				if (klass == IntPtr.Zero) continue;
				var iter = IntPtr.Zero;
				IntPtr info;
				while ((info = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
				{
					var pointer = Marshal.ReadIntPtr(info);
					if (pointer != IntPtr.Zero) visit(klass, info, pointer);
				}
			}
		}
	}
}
