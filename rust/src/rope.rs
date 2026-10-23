//! The same handle-and-borrow interop as `RustView`, over Lapce's fork of xi's
//! rope instead of the 16-byte toy chunks. Lapce keeps this rope in its UI
//! process and edits it with direct calls; this is that, from C#.

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicUsize, Ordering};

use lapce_xi_rope::Rope;

use crate::{guarded, FFI_INVALID_RANGE, FFI_INVALID_UTF8, FFI_NULL_ARGUMENT, FFI_OK};

/// How many ropes are alive, so the C# side can check its handles free them.
static LIVE_ROPES: AtomicUsize = AtomicUsize::new(0);

pub struct RopeView {
    rope: Rope,
}

impl RopeView {
    fn new() -> RopeView {
        LIVE_ROPES.fetch_add(1, Ordering::SeqCst);
        RopeView { rope: Rope::from("hey dotnet!") }
    }
}

impl Drop for RopeView {
    fn drop(&mut self) {
        LIVE_ROPES.fetch_sub(1, Ordering::SeqCst);
    }
}

/// One leaf of the rope, as C# reads it.
#[repr(C)]
pub struct Chunk {
    pub ptr: *const u8,
    pub len: usize,
}

#[unsafe(no_mangle)]
pub extern "C" fn rope_create() -> *mut RopeView {
    catch_unwind(|| Box::into_raw(Box::new(RopeView::new()))).unwrap_or(std::ptr::null_mut())
}

/// # Safety
/// `ptr` is null or came from `rope_create`, and is freed exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rope_free(ptr: *mut RopeView) {
    if ptr.is_null() {
        return;
    }
    // SAFETY: the caller's contract; the SafeHandle calls this once per rope.
    let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(ptr) })));
}

/// Replaces the bytes `start..end` with `text`, the one edit a rope has.
/// Both ends must fall on character boundaries.
///
/// # Safety
/// `ptr` is a live rope, and `text` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rope_edit(ptr: *mut RopeView, start: usize, end: usize, text: *const u8, len: usize) -> i32 {
    guarded(|| {
        if ptr.is_null() || (text.is_null() && len != 0) {
            return FFI_NULL_ARGUMENT;
        }
        // SAFETY: the caller's contract.
        let view = unsafe { &mut *ptr };
        let rope = &mut view.rope;
        if start > end || end > rope.len() || !rope.is_codepoint_boundary(start) || !rope.is_codepoint_boundary(end) {
            return FFI_INVALID_RANGE;
        }
        // SAFETY: the caller's contract; C# pins a span and passes its length.
        let bytes = if len == 0 { &[][..] } else { unsafe { std::slice::from_raw_parts(text, len) } };
        match std::str::from_utf8(bytes) {
            Ok(text_str) => {
                rope.edit(start..end, text_str);
                FFI_OK
            }
            Err(_) => FFI_INVALID_UTF8,
        }
    })
}

/// Removes the last character.
///
/// # Safety
/// `ptr` is null or a live rope.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rope_delete_backward(ptr: *mut RopeView) -> i32 {
    guarded(|| {
        if ptr.is_null() {
            return FFI_NULL_ARGUMENT;
        }
        // SAFETY: the caller's contract.
        let rope = unsafe { &mut (*ptr).rope };
        let end = rope.len();
        if let Some(start) = rope.prev_codepoint_offset(end) {
            rope.edit(start..end, "");
        }
        FFI_OK
    })
}

/// # Safety
/// `ptr` is null or a live rope.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rope_len(ptr: *const RopeView) -> usize {
    if ptr.is_null() {
        return 0;
    }
    // SAFETY: the caller's contract.
    unsafe { (*ptr).rope.len() }
}

/// Lends every leaf in one call: writes up to `capacity` of them to `out` and
/// returns how many there are, so a caller whose buffer was too small can retry
/// with the right size. The bytes are valid only until the next edit or free.
///
/// # Safety
/// `ptr` is null or a live rope, and `out` has room for `capacity` chunks.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rope_chunks(ptr: *const RopeView, out: *mut Chunk, capacity: usize) -> usize {
    if ptr.is_null() {
        return 0;
    }
    // SAFETY: the caller's contract.
    let rope = unsafe { &(*ptr).rope };
    let mut count = 0;
    // An empty rope yields one empty chunk; skip it so empty means no chunks.
    for chunk in rope.iter_chunks(..).filter(|chunk| !chunk.is_empty()) {
        if count < capacity && !out.is_null() {
            // SAFETY: the caller's contract; count < capacity.
            unsafe { out.add(count).write(Chunk { ptr: chunk.as_ptr(), len: chunk.len() }) };
        }
        count += 1;
    }
    count
}

#[unsafe(no_mangle)]
pub extern "C" fn live_ropes() -> usize {
    LIVE_ROPES.load(Ordering::SeqCst)
}

