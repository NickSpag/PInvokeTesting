use std::sync::atomic::{AtomicUsize, Ordering};

/// How many views are alive, so the C# side can check its handles free them.
pub static LIVE_VIEWS: AtomicUsize = AtomicUsize::new(0);

/// Called after every edit with the view's text. Valid only for the call.
pub type UpdateCallback = extern "C" fn(user_data: *mut std::ffi::c_void, text: *const u8, len: usize);

pub struct RustView {
    text: String,
    on_update: Option<(UpdateCallback, *mut std::ffi::c_void)>,
}

impl RustView {
    pub fn new() -> RustView {
        LIVE_VIEWS.fetch_add(1, Ordering::SeqCst);
        RustView { text: String::from("hey dotnet!"), on_update: None }
    }

    pub fn text(&self) -> &str {
        &self.text
    }

    pub fn set_on_update(&mut self, on_update: Option<(UpdateCallback, *mut std::ffi::c_void)>) {
        self.on_update = on_update;
    }

    pub fn insert(&mut self, new_text: &str){
        self.text.push_str(new_text);
        if let Some((callback, user_data)) = self.on_update {
            callback(user_data, self.text.as_ptr(), self.text.len());
        }
    }
}

impl Drop for RustView {
    fn drop(&mut self) {
        LIVE_VIEWS.fetch_sub(1, Ordering::SeqCst);
    }
}
