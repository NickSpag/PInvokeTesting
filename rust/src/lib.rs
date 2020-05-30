extern crate libc;

mod view;

use libc::c_char;
use std::ffi::CStr;
use view::RustView;

#[no_mangle]
pub extern "C" fn insert_text(ptr: *mut RustView, text: *const c_char) {
    let view = unsafe{
        assert!(!ptr.is_null());
        &mut *ptr
    };
    
    let text = unsafe{
        assert!(!text.is_null());
        CStr::from_ptr(text)
    };

    let text_str = text.to_str().unwrap();
    view.insert(text_str)
}

#[no_mangle]
pub unsafe extern "C" fn create_view() -> *mut RustView {
    println!("created");
    Box::into_raw(Box::new(RustView::new()))
}

#[no_mangle]
pub extern "C" fn free_view(ptr: *mut RustView) {
    if ptr.is_null() {
        return
    }
    unsafe {
        Box::from_raw(ptr);
    }
}