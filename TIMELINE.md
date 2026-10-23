# .NET memory handling across the years, one branch at a time

Each `timeline/NN-*` branch builds on the one before it, so the diff between
two neighbours is exactly one improvement. Every branch builds and passes
`dotnet run -c Release` (after `cargo build --release` in `rust/`).

| Branch | Available since | What changes |
|---|---|---|
| `timeline/00-baseline-2020` | C# 8 / .NET Core 3.1 (2019) | The working fix, written with only what 2020 had |
| `timeline/01-readonlysequence` | .NET Core 2.1 (2018) | Rust's text in chunks, read in place as a `ReadOnlySequence<byte>` |

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
