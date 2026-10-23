// Edition 2024 warns about this; C# 15 makes its counterpart an error, so match it.
#![deny(unsafe_op_in_unsafe_fn)]

pub mod rope;
pub mod view;

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::Ordering;
use view::{RustView, UpdateCallback, LIVE_VIEWS};

/// Status codes returned across the boundary instead of panicking through it.
pub const FFI_OK: i32 = 0;
pub const FFI_NULL_ARGUMENT: i32 = 1;
pub const FFI_INVALID_UTF8: i32 = 2;
pub const FFI_PANIC: i32 = 3;
pub const FFI_INVALID_RANGE: i32 = 4;

/// Unwinding out of an `extern "C"` fn is undefined behaviour (it aborts on
/// Rust 1.81+), so every export runs its body through this.
pub(crate) fn guarded(body: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(body)).unwrap_or(FFI_PANIC)
}

#[unsafe(no_mangle)]
pub extern "C" fn create_view() -> *mut RustView {
    catch_unwind(|| Box::into_raw(Box::new(RustView::new()))).unwrap_or(std::ptr::null_mut())
}

/// Takes the view back from C#.
///
/// # Safety
/// `ptr` is null or came from `create_view`, and is freed exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_view(ptr: *mut RustView) {
    if ptr.is_null() {
        return
    }
    // SAFETY: the caller's contract; the SafeHandle calls this once per view.
    let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(ptr) })));
}

/// Takes a pointer and length rather than a NUL-terminated string, so the text
/// may contain NULs and the caller never has to append one.
///
/// # Safety
/// `ptr` is a live view, and `text` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn insert_text(ptr: *mut RustView, text: *const u8, len: usize) -> i32 {
    guarded(|| {
        if ptr.is_null() || (text.is_null() && len != 0) {
            return FFI_NULL_ARGUMENT;
        }
        // SAFETY: the caller's contract; C# pins a span and passes its length.
        let bytes = if len == 0 { &[][..] } else { unsafe { std::slice::from_raw_parts(text, len) } };
        let text_str = match std::str::from_utf8(bytes) {
            Ok(text_str) => text_str,
            Err(_) => return FFI_INVALID_UTF8,
        };
        // SAFETY: the caller's contract.
        unsafe { edit(ptr, |view| view.insert(text_str)) };
        FFI_OK
    })
}

/// Removes the last character.
///
/// # Safety
/// `ptr` is null or a live view.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn delete_backward(ptr: *mut RustView) -> i32 {
    guarded(|| {
        if ptr.is_null() {
            return FFI_NULL_ARGUMENT;
        }
        // SAFETY: the caller's contract.
        unsafe { edit(ptr, RustView::delete_backward) };
        FFI_OK
    })
}

/// Applies `change`, then runs the update callback.
///
/// # Safety
/// `ptr` is a live view.
unsafe fn edit(ptr: *mut RustView, change: impl FnOnce(&mut RustView)) {
    // The &mut has to end before the callback runs: C# reads the view back
    // from inside it, and a shared borrow alongside a live &mut is UB.
    let on_update = {
        // SAFETY: a live view, and nothing else borrows it during an edit.
        let view = unsafe { &mut *ptr };
        change(view);
        view.on_update()
    };
    if let Some((callback, user_data)) = on_update {
        callback(user_data);
    }
}

/// The text's length in bytes.
///
/// # Safety
/// `ptr` is null or a live view.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn view_len(ptr: *const RustView) -> usize {
    if ptr.is_null() {
        return 0;
    }
    // SAFETY: the caller's contract.
    unsafe { (*ptr).len() }
}

/// How many chunks the view's text is split into.
///
/// # Safety
/// `ptr` is null or a live view.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn view_chunk_count(ptr: *const RustView) -> usize {
    if ptr.is_null() {
        return 0;
    }
    // SAFETY: the caller's contract.
    unsafe { (*ptr).chunks().len() }
}

/// Lends one chunk's UTF-8 bytes.
///
/// # Safety
/// `ptr` is null or a live view, and `out_len` is null or writable. The returned
/// bytes are valid only until the next call that mutates or frees the view.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn view_chunk(ptr: *const RustView, index: usize, out_len: *mut usize) -> *const u8 {
    if ptr.is_null() || out_len.is_null() {
        return std::ptr::null();
    }
    // SAFETY: the caller's contract.
    let view = unsafe { &*ptr };
    match view.chunks().get(index) {
        Some(chunk) => {
            // SAFETY: the caller's contract.
            unsafe { *out_len = chunk.len() };
            chunk.as_ptr()
        }
        None => std::ptr::null(),
    }
}

/// Registers a callback run after every edit, or clears it when `callback` is null.
///
/// # Safety
/// `ptr` is null or a live view, and `callback` accepts `user_data` for as long
/// as the view lives.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn set_update_callback(
    ptr: *mut RustView,
    callback: Option<UpdateCallback>,
    user_data: *mut c_void,
) -> i32 {
    if ptr.is_null() {
        return FFI_NULL_ARGUMENT;
    }
    // SAFETY: the caller's contract.
    unsafe { (*ptr).set_on_update(callback.map(|cb| (cb, user_data))) };
    FFI_OK
}

#[unsafe(no_mangle)]
pub extern "C" fn live_views() -> usize {
    LIVE_VIEWS.load(Ordering::SeqCst)
}
