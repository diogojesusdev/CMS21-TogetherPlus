# The menu crash, found: a bool told to the GC as an object at address 1

**Status:** done in 0.5.0-rc33.

## Goal

> "CRASHED AGAIN"

Stop guessing. Read the crash dump instead of the log.

## Findings

### The dump

`Crash_2026-11-05_212611617\crash.dmp`, parsed directly (exception stream + module list):

```
ExceptionCode : 0xC0000005          (access violation)
FaultAddress  : 0x00007FFA318D0DC7
Info[0]=0 (read)  Info[1]=0x1       -> it tried to READ address 0x1
FAULT IN GameAssembly.dll  base 0x7FFA31620000  offset 0x2B0DC7
```

So it is **not** UnityPlayer and not the asset unload itself: it is IL2CPP code, dereferencing address 1.

The bytes at that offset in `GameAssembly.dll`:

```
48 8B 7C 24 60      mov  rdi, [rsp+60]
48 85 FF            test rdi, rdi        ; null check - PASSES, because rdi is 1
0F 84 25 01 00 00   je   ...
48 8B 07            mov  rax, [rdi]      ; <-- faults, reads 0x1
A8 01               test al, 1
0F 85 1A 01 00 00   jne  ...
48 83 E0 FE         and  rax, 0FFFFFFFFFFFFFFFEh
```

Reading an object's first word and masking the low bit is object-header / mark-bit handling: the collector. It was
handed a "reference" whose value is **1**, passed the null check, and dereferenced it.

### Where a reference of 1 comes from

`Il2CppArrays.CopyInline` (and before it, the original `Set`) wrote an inline value type like this:

```csharp
for (var word = 0; word < words; word++)
    IL2CPP.il2cpp_gc_wbarrier_set_field(array.Pointer, target + offset, Marshal.ReadIntPtr(source, offset));
```

It announced **every pointer-sized word of the struct** to the collector as a reference. But `NewCarData` and
`SaveData` are mostly not references - they hold `bool`, `int` and `float` between their pointers. A `bool` of true
is the value 1, so the write barrier recorded "this slot holds the object at address 1".

Nothing fails at that moment. It fails at the **next collection**, and `UnloadUnusedAssets` during a scene change is
exactly that - which is why the crash always appeared on the line after
`Unloading N Unused Serialized files`, and why it never happened at startup (nothing had copied a car yet).

This explains every observation across the whole investigation:

- Only ever after a garage session: `SavesManager.Initialize` and `DataHelper.Copy` copy `NewCarData`/`SaveData`
  through this path, and only a loaded save has cars to copy.
- Not fixed by releasing the mod's references (rc31) - the bad entry was recorded long before, and the log proved
  the release finished 4.3s before the crash.
- Not fixed by destroying the avatar bundle (rc32) - the serialized-file line was a coincidence of timing, not the
  cause.
- Consistent with the original plan's note that menu return "never worked": this code is older than any of the
  recent changes. The pre-rc28 version did `array[index] = value` and then the same barrier loop.

### Why a plain copy is correct

The mod already forces the collector to be non-incremental (`TogetherPlus.GcFix`, logged as
`[GC] Incremental GC turned off at runtime.`). Boehm then scans this memory itself at collection time and checks
whether a word genuinely points into the heap; it does not need - and must not be given - a list of slots asserted
to be references. The barrier is useful for a real reference store, which is a different code path
(`Store`, used for reference arrays) and is unchanged.

## Approach

1. `Il2CppArrays.CopyInline` copies the bytes plainly. No `il2cpp_gc_wbarrier_set_field` over struct contents, so no
   non-reference word can ever be presented to the collector as an object.
2. `Store` (real reference elements) keeps its single, correct barrier call.
3. The comment records the fault address, the disassembly and the reasoning, so this is not "simplified" back later.
4. Alternatives rejected: barriering only the words that really are reference fields (needs a per-field map of every
   game struct, and is still redundant with a non-incremental collector); keeping the barrier and masking suspicious
   values (guesswork on top of a wrong premise); `Marshal.Copy` through a managed buffer (an extra copy for no gain).

## Files

- `Shared/Il2CppArrays.cs` (`CopyInline`, `Set`)

## Testing

- `.\build.ps1 -Package` - all unit tests pass.
- In game, the case that crashed every time: host, accept a car order, go back to the main menu. It must reach the
  menu. Repeat several times in one launch, and start another session in between.
- Also check the save slots still show the right cars afterwards, since this is the code that copies them.

## Follow-ups

- If a crash ever returns here, the method that found it is in this plan: parse `crash.dmp` for the exception stream
  and module list, then disassemble the faulting offset in the module. The log alone was never going to show it.
