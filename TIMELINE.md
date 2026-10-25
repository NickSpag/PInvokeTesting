# .NET memory handling across the years, one branch at a time

Each `timeline/NN-*` branch builds on the one before it, so the diff between
two neighbours is exactly one improvement. Every branch builds and passes
`dotnet run -c Release` (after `cargo build --release` in `rust/`).

| Branch | Available since | What changes |
|---|---|---|
| `timeline/00-baseline-2020` | C# 8 / .NET Core 3.1 (2019) | The working fix, written with only what 2020 had |
| `timeline/01-readonlysequence` | .NET Core 2.1 (2018) | Rust's text in chunks, read in place as a `ReadOnlySequence<byte>` |
| `timeline/02-scoped-borrow` | C# 7.2 (2017), C# 8 (2019) | A `ref struct` borrow keeps the view alive and can't escape the method |
| `timeline/03-net5-function-pointers` | C# 9 / .NET 5 (Nov 2020) | Function pointers replace the callback delegate; `SuppressGCTransition` |
| `timeline/04-net7-libraryimport` | C# 11 / .NET 7 (Nov 2022) | `LibraryImport` writes the marshalling at compile time; `DisableRuntimeMarshalling` |
| `timeline/05-nativeaot-static-link` | .NET 7 (Nov 2022) | NativeAOT links the Rust archive into the executable |
| `timeline/06-net11-memory-safety` | C# 15 / .NET 11 (preview, 2026); Rust 2024 | Every operation on unmanaged memory is an explicit, commented `unsafe` region, on both sides |

## 00 — baseline, 2020

The interop fixed using only C# 8 and the .NET Core 3.1 API surface, which is
what this project targeted when it was written.

- **Ownership:** Rust boxes the view and hands C# a raw pointer
  (`Box::into_raw`); a `SafeHandle` owns it and calls `free_view` exactly once,
  from `Dispose` or from the finalizer.
- **Text in:** pointer + length, not a C string, so embedded NULs survive and
  Rust validates the UTF-8 and returns a status code rather than panicking.
- **Text out:** `view_text` lends Rust's bytes as a `ReadOnlySpan<byte>`, valid
  until the next edit. Nothing enforces that.
- **Rust calling C#:** a delegate. The marshaller builds a native thunk for it
  that is valid only while the delegate object lives, so one static instance
  is kept for the lifetime of the process. Rust gets a weak `GCHandle` as
  `user_data` to find its way back to the right `RustView`.

The runtime is newer than 3.1 (which is out of support); `LangVersion` 8.0
keeps the language honest, and the API surface is kept to 3.1's by hand.

## 01 — `ReadOnlySequence<byte>` (.NET Core 2.1, 2018)

Available in 2020 and not used, so it comes first. xi keeps text in a rope, a
tree of string chunks, so there is no single buffer for a span to point at.
The Rust view now stores its text the same way (16-byte chunks, small so the
demo spans several, never splitting a UTF-8 character), and C# reads it as a
`ReadOnlySequence<byte>`: the type .NET has for a chain of non-contiguous
memory, built for pipelines.

- **`UnmanagedChunk : MemoryManager<byte>`** turns a Rust pointer and length
  into a `Memory<byte>`, which is what a sequence segment holds. It owns
  nothing; Rust frees the bytes and the GC never moves them.
- **`ChunkSegment : ReadOnlySequenceSegment<byte>`** links them; each knows where
  it starts in the whole text.
- **No byte is copied** to read the text. Only `Text`, which builds a `string`,
  copies.
- **The callback carries no text any more**, since there is no contiguous text to
  pass; C# reads the view back from inside it. That exposed a Rust aliasing rule:
  `insert_text` now ends its `&mut` borrow of the view *before* calling back,
  because C# then takes a shared borrow of the same view.

What this does **not** fix: the sequence is valid only until the next edit or
until the view is freed, and nothing stops C# holding it longer. That is next.

## 02 — a scoped borrow (`ref struct`, C# 7.2 / C# 8)

Also available in 2020. Step 01's sequence points into Rust's memory and
nothing stopped C# keeping it after the view was freed. `TextBorrow` closes
both halves of that:

- **The view can't be freed under it.** Opening a borrow calls
  `DangerousAddRef` on the `SafeHandle`; closing it calls `DangerousRelease`. A
  `Dispose` of the view while a borrow is open only marks the handle closed, and
  `free_view` runs when the last borrow ends. Using the view after that throws
  `ObjectDisposedException` instead of reading freed memory.
- **The borrow can't outlive its method.** It's a `ref struct` (C# 7.2), so the
  compiler rejects storing it in a field, boxing it, capturing it in a lambda,
  or using it in an `async` method. `using` on a ref struct came with C# 8.
  Build with `-p:DefineConstants=DEMO_ESCAPE` to see the errors:

  ```
  error CS0029: Cannot implicitly convert type 'TextBorrow' to 'object'
  error CS8175: Cannot use ref local 'borrow' inside an anonymous method, lambda expression, or query expression
  error CS8400: Feature 'ref and unsafe in async and iterator methods' is not available in C# 8.0
  error CS8345: Field or auto-implemented property cannot be of type 'TextBorrow' unless it is an instance member of a ref struct
  ```
- **Chunks come out as `ReadOnlySpan<byte>`** through a ref struct enumerator, so
  they can't escape either. The public `ReadOnlySequence` from step 01 is gone:
  it's an ordinary struct over heap objects, so it could be kept anywhere.
- **A copied borrow releases once.** A ref struct can still be copied, so the
  reference is held by a small heap object that releases on the first `Dispose`.

What the compiler does *not* catch: inside one method, a span read from the
borrow can still be used after the borrow's `Dispose`, and editing the view
while a borrow is open invalidates it. Rust's borrow checker would reject both.

## 03 — function pointers (C# 9 / .NET 5, November 2020)

Six months after this project's last commit.

- **`delegate* unmanaged[Cdecl]` + `[UnmanagedCallersOnly]` replace the delegate.**
  `&OnUpdate` is the address of a real native entry point the runtime generates
  for a static method. There is no delegate object, so there is no keep-alive:
  the static field step 00 needed, and the crash you get without it, are gone.
  It's also cheaper to call, since no marshalling thunk sits in between.
- **`nuint`** spells `size_t` directly instead of `UIntPtr`.
- **`[SuppressGCTransition]`** on `view_chunk_count`, `view_chunk` and
  `live_views`. Every P/Invoke normally switches the thread to preemptive GC
  mode and back; for a call that runs a few instructions, that switch costs
  more than the call. It is only safe for functions that never block, never
  take long and never call back into .NET — a GC can't proceed while one runs.
- **`Encoding.GetString(in ReadOnlySequence<byte>)`** decodes across the chunks
  directly, without the intermediate `ToArray()`.

## 04 — `LibraryImport` (C# 11 / .NET 7, November 2022)

- **`[LibraryImport]` replaces `[DllImport]`.** `DllImport` has the runtime
  generate a marshalling stub when the method is first called. `LibraryImport`
  is a source generator: the stub is ordinary C# written at compile time, which
  you can read, step into, and ship under NativeAOT and trimming. Here is what it
  writes for `insert_text` — the `SafeHandle` reference counting that used to be
  invisible:

  ```csharp
  private static partial int _insert_text(RustViewHandle view, byte* text, nuint len)
  {
      nint __view_native = default;
      int __retVal = default;
      SafeHandleMarshaller<RustViewHandle>.ManagedToUnmanagedIn __view_native__marshaller = new();
      try
      {
          __view_native__marshaller.FromManaged(view);      // DangerousAddRef
          __view_native = __view_native__marshaller.ToUnmanaged();
          __retVal = __PInvoke(__view_native, text, len);
      }
      finally
      {
          __view_native__marshaller.Free();                 // DangerousRelease
      }
      return __retVal;

      [DllImport("ffi_test", EntryPoint = "insert_text", ExactSpelling = true)]
      static extern unsafe int __PInvoke(nint __view_native, byte* __text_native, nuint __len_native);
  }
  ```

  To see it all, build with `-p:EmitCompilerGeneratedFiles=true`.
- **`[assembly: DisableRuntimeMarshalling]`** switches off the runtime's
  built-in marshaller for the whole assembly. Any signature that would have
  needed it is now a compile error rather than a hidden stub. Here, nothing
  changed: every signature was already blittable or handled by the generator.
- **`"..."u8` literals** put UTF-8 constants straight into the binary as a
  `ReadOnlySpan<byte>`, so the demo's fixed strings reach Rust without encoding.

## 05 — NativeAOT with the Rust library linked in (.NET 7, November 2022)

NativeAOT compiles the whole program to machine code at publish time, and it
can link a static library into the result. The Rust crate now also builds a
`staticlib` (`libffi_test.a`, the same C ABI as an archive), and the project
lists it as a `NativeLibrary` with `DirectPInvoke` for `ffi_test`:

```
cargo build --release                          # in rust/
dotnet publish -c Release -r osx-arm64 -o out  # in PInvokeTesting/
rm out/libffi_test.dylib && out/PInvokeTesting # still passes
```

- **No library to find.** Without `DirectPInvoke`, a P/Invoke resolves its
  library and symbol at runtime (`dlopen`/`dlsym`) on first call. With it, each
  call is a direct call the linker resolved, into code inside the executable:
  `insert_text`, `create_view` and `view_chunk` appear in its debug symbols, and
  nothing in it refers to `libffi_test` any more.
- **This is where step 04 pays off.** NativeAOT can't generate marshalling stubs
  at runtime, so the compile-time stubs `LibraryImport` writes are what make
  these calls possible at all.
- `dotnet run` is unchanged: the JIT build still loads the dylib.

Two things to know. The linker warns that Homebrew's Rust standard library was
built for macOS 26 while .NET links for macOS 12 and up; the binary runs here,
and a `rustup` toolchain (whose standard library targets older releases) would
silence it. And the executable is about 40 MB because it carries
BenchmarkDotNet; the interop itself adds very little.

## 06 — the updated memory safety rules (C# 15 / .NET 11 preview) and Rust 2024

**Needs the .NET 11 SDK** (built against `11.0.100-rc.2`, pinned in
`global.json`). Both the language feature and the SDK are previews, and
Microsoft says the details may change.

The project opts in with:

```xml
<LangVersion>preview</LangVersion>
<Features>$(Features);updated-memory-safety-rules</Features>
```

That changes what `unsafe` means. It used to mark code that *mentions*
pointers, with no effect on callers. Now it marks the operations that *touch*
unmanaged memory, and on a member it's a contract: callers must be in an
`unsafe` context, so the obligation to audit moves to them. Flipping the switch
with no other change gave five errors:

```
NativeBinding.cs(17,40): error CS9377: The 'unsafe' modifier does not have any effect here under the current memory safety rules.
NativeBinding.cs(33,37): error CS9389: 'extern' member must be marked 'unsafe' or 'safe'.
NativeBinding.cs(59,38): error CS9389: 'extern' member must be marked 'unsafe' or 'safe'.
NativeChunks.cs(12,25):  error CS9377: (same, on UnmanagedChunk)
RustViewHandle.cs(8,25): error CS9377: (same, on RustView)
```

- **`unsafe` on a class is now an error**: a type has no callers to hand an
  obligation to.
- **Every binding says `safe` or `unsafe`.** The compiler demanded it for only
  two of the seven, `free_view` and `live_views`, because those are the ones the
  `LibraryImport` generator emits as a plain `extern`; the other five go through
  a generated wrapper. They're all marked anyway, with the reason beside each:
  `free_view`, `insert_text`, `view_chunk` and `set_update_callback` are
  `unsafe`, while `create_view`, `view_chunk_count` and `live_views` are `safe`.
- **`unsafe` on a method no longer makes its body unsafe.** Marking a method
  and entering an unsafe region are separate now, so even `FreeView` has an
  `unsafe { }` block inside its `unsafe` signature.
- **The .NET 11 libraries carry the markers too.** `new Span<byte>(void*, int)`,
  `new MemoryHandle(void*)`, `GCHandle.FromIntPtr` and `GCHandle.Free` are all
  requires-unsafe, so the compiler found every place that needed a reason to be
  sound — eleven of them, each now an `unsafe { }` block or the new single
  expression form, `unsafe(expr)`, with a comment saying why it's sound.
- **What's left safe is the API.** `RustView`, `TextBorrow` and
  `RustBindings.InsertUtf8` call no unsafe member from the outside, and
  `Program.cs` contains no `unsafe` at all. `fixed` no longer needs an unsafe
  context either: pinning is safe, dereferencing isn't.
- The assembly is stamped with `MemorySafetyRulesAttribute`, and members marked
  `unsafe` carry `RequiresUnsafeAttribute`, so other assemblies built under the
  new rules see the obligation too.

**Rust's edition 2024 made the same two changes**, and the crate moves to it
here. `#[no_mangle]` is now `#[unsafe(no_mangle)]` (an unmangled export can clash
with another symbol of the same name, so writing one is an assertion), and the
body of an `unsafe fn` is no longer an unsafe block. The second is a warning in
Rust (`unsafe_op_in_unsafe_fn`); the crate denies it to match C#. Each operation
now sits in its own `unsafe { }` with a `// SAFETY:` comment, and each export has
a `# Safety` section saying what its caller must guarantee.

So both sides of the boundary now say, at each operation, what makes it sound —
and those comments describe the same contract from opposite ends.

Step 02's escape demo reads differently under the newer compiler: C# 13 allows
a ref struct in an `async` method as long as it isn't held across an `await`,
so that error is now `CS4007: Instance of type 'TextBorrow' cannot be preserved
across 'await' or 'yield' boundary`.

## Benchmarks: the question the project started with

PInvokeTesting was written in 2020 to find out whether driving the Rust view
in-process beats xi's usual setup of a separate process speaking JSON over
stdin and stdout. The 2020 benchmarks couldn't answer it: the Rust binary
exited after reading one line, nothing ever read a reply, and the two
benchmarks timed different operations.

Now both sides do the same work per operation — type a character and backspace
it, so the text never grows, with each edit answered by the new length:

- **`InProcess`**: two P/Invoke calls, each running the update callback into
  C#, then reading the length.
- **`OverPipe`**: two JSON round trips to `ffi_test_bin`, which reads one
  request per line, edits the same `RustView`, and writes a reply. The C# side
  uses the `System.Text.Json` source generator; the Rust side uses `serde_json`.

Run with `dotnet run -c Release -- bench`. On an Apple M4, .NET 11 RC2:

| Method    | Mean         | Allocated | Ratio |
|---------- |-------------:|----------:|------:|
| InProcess |     44.46 ns |         - |  1.00 |
| OverPipe  | 19,349.41 ns |    1360 B |   435 |

About 435 times faster, with no allocation at all. But 19 µs for two edits
still fits about 800 keystrokes into a single 60 Hz frame, so for typing the
pipe was never the bottleneck. What the gap does matter for is the traffic that
is many small requests: xi asks its frontend to measure text widths, and
in-process that's a function call rather than a round trip.

Two things changed along the way:

- **`PublishAot` is now set only when publishing for a runtime.** Set
  unconditionally, it writes NativeAOT's feature switches into *every* build's
  runtimeconfig, `IsDynamicCodeSupported: false` among them. `dotnet run` then
  runs on the JIT while behaving as if it were AOT, and BenchmarkDotNet, which
  generates code at runtime, refused to run.
- **BenchmarkDotNet stays out of the NativeAOT build.** It needs the JIT anyway,
  and leaving it out takes the published executable from about 40 MB to 3.5 MB.
  The pipe check still runs there: the source-generated JSON works under AOT.

## Lapce's rope, alongside the toy chunks

[Lapce](https://github.com/lapce/lapce) is xi's successor in practice: it keeps
xi's rope (as the maintained fork `lapce-xi-rope`) but drops xi's process split.
Its UI process holds the rope and edits it with direct calls, and only files,
language servers and plugins sit behind a boundary, which on a local workspace
is a thread rather than a process. That is the in-process answer this project
was testing for, so the rope is now here too, next to the original:

- **Rust:** `rope.rs` wraps `lapce_xi_rope::Rope` with the same patterns as
  `RustView`: a boxed handle, status codes, panics caught at the boundary,
  `# Safety` on every export. Edits are Lapce's one primitive, replacing a byte
  range, checked to fall on character boundaries.
- **One call for all the leaves.** `rope_chunks` writes every leaf's pointer and
  length into a buffer C# provides, where `RustView` needs one P/Invoke per chunk.
- **C#:** `RopeView` and `RopeHandle`, and `TextBorrow` now works over either
  store through a small `NativeTextHandle` base class: same lease, same spans.
  No update callback, since Lapce's UI owns the rope and needs no one to tell it
  what it just did.
- `RustView`, its benchmarks and the JSON pipe are unchanged, so the
  comparisons below are against the same code as before.

Edits (type a character and backspace it; Apple M4, .NET 11 RC2):

| Method        | Mean         | Allocated (.NET) | Ratio |
|-------------- |-------------:|-----------------:|------:|
| InProcess     |     45.15 ns |                - |  1.00 |
| RopeInProcess |    652.25 ns |                - | 14.45 |
| OverPipe      | 19,464.47 ns |           1360 B |   431 |

The rope is about 14 times slower to edit than the toy chunks: each edit
rebuilds a path of its shared tree (O(log n), allocating on the Rust heap,
which BenchmarkDotNet doesn't see). That's what buys cheap snapshots and undo.
It is still 30 times faster than one trip over the pipe.

Reading the whole text back:

| Read            | Size   | 16-byte chunks          | Lapce's rope           | Faster |
|---------------- |-------:|------------------------:|-----------------------:|-------:|
| Walk in place   | 1 KB   |    1.5 µs,   5 KB alloc |  0.11 µs,  0.2 KB alloc |    14× |
| Walk in place   | 64 KB  |     98 µs, 328 KB alloc |   1.6 µs,    5 KB alloc |    60× |
| Walk in place   | 1 MB   |    3.1 ms, 5.2 MB alloc |    27 µs,   82 KB alloc |   115× |
| Copy to string  | 1 KB   |    6.7 µs,  12 KB alloc |  0.43 µs,  2.5 KB alloc |    15× |
| Copy to string  | 64 KB  |    1.1 ms, 933 KB alloc |    48 µs,  141 KB alloc |    22× |
| Copy to string  | 1 MB   |     29 ms,  15 MB alloc |   1.6 ms,  3.7 MB alloc |    18× |

Almost all of that is leaf size: a 1 MB document is 65,536 toy chunks but
about 1,000 rope leaves, and the read costs about the same per chunk either
way. What's left on the table: building the `ReadOnlySequence` still allocates
two objects per leaf, and the string copy decodes through intermediate buffers
(3.7 MB allocated to produce a 2 MB string).
