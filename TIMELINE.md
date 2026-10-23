# .NET memory handling across the years, one branch at a time

Each `timeline/NN-*` branch builds on the one before it, so the diff between
two neighbours is exactly one improvement. Every branch builds and passes
`dotnet run -c Release` (after `cargo build --release` in `rust/`).

| Branch | Available since | What changes |
|---|---|---|
| `timeline/00-baseline-2020` | C# 8 / .NET Core 3.1 (2019) | The working fix, written with only what 2020 had |

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
