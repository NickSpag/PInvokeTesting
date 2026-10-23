# .NET memory handling across the years, one branch at a time

Each `timeline/NN-*` branch builds on the one before it, so the diff between
two neighbours is exactly one improvement. Every branch builds and passes
`dotnet run -c Release` (after `cargo build --release` in `rust/`).

| Branch | Available since | What changes |
|---|---|---|
| `timeline/00-baseline-2020` | C# 8 / .NET Core 3.1 (2019) | The working fix, written with only what 2020 had |
| `timeline/01-readonlysequence` | .NET Core 2.1 (2018) | Rust's text in chunks, read in place as a `ReadOnlySequence<byte>` |
| `timeline/02-scoped-borrow` | C# 7.2 (2017), C# 8 (2019) | A `ref struct` borrow keeps the view alive and can't escape the method |

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
