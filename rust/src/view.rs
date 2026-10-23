use std::sync::atomic::{AtomicUsize, Ordering};

/// How many views are alive, so the C# side can check its handles free them.
pub static LIVE_VIEWS: AtomicUsize = AtomicUsize::new(0);

/// Called after every edit. The callee reads the text back through the view.
pub type UpdateCallback = extern "C" fn(user_data: *mut std::ffi::c_void);

/// Small on purpose, so even the demo's text spans several chunks. xi's rope
/// keeps leaves of roughly 1 KB.
pub const CHUNK_CAPACITY: usize = 16;

/// Text held as a chain of chunks, the way xi's rope holds it: there is no one
/// contiguous buffer to lend out.
pub struct RustView {
    chunks: Vec<String>,
    len: usize,
    on_update: Option<(UpdateCallback, *mut std::ffi::c_void)>,
}

impl RustView {
    pub fn new() -> RustView {
        LIVE_VIEWS.fetch_add(1, Ordering::SeqCst);
        let mut view = RustView { chunks: Vec::new(), len: 0, on_update: None };
        view.insert("hey dotnet!");
        view
    }

    pub fn chunks(&self) -> &[String] {
        &self.chunks
    }

    /// Total length of the text in bytes.
    pub fn len(&self) -> usize {
        self.len
    }

    pub fn is_empty(&self) -> bool {
        self.len == 0
    }

    pub fn on_update(&self) -> Option<(UpdateCallback, *mut std::ffi::c_void)> {
        self.on_update
    }

    pub fn set_on_update(&mut self, on_update: Option<(UpdateCallback, *mut std::ffi::c_void)>) {
        self.on_update = on_update;
    }

    /// Appends, filling the last chunk before starting a new one and never
    /// splitting a UTF-8 character across two chunks.
    pub fn insert(&mut self, new_text: &str){
        self.len += new_text.len();
        let mut rest = new_text;
        while !rest.is_empty() {
            let room = match self.chunks.last() {
                Some(last) => CHUNK_CAPACITY - last.len(),
                None => 0,
            };
            let mut split = room.min(rest.len());
            while !rest.is_char_boundary(split) {
                split -= 1;
            }
            if split == 0 {
                self.chunks.push(String::with_capacity(CHUNK_CAPACITY));
                continue;
            }
            self.chunks.last_mut().unwrap().push_str(&rest[..split]);
            rest = &rest[split..];
        }
    }
}

impl RustView {
    /// Removes the last character, the whole character, like a backspace at the
    /// end of the text. Drops the chunk it leaves empty.
    pub fn delete_backward(&mut self) {
        while let Some(last) = self.chunks.last_mut() {
            match last.pop() {
                Some(removed) => {
                    self.len -= removed.len_utf8();
                    if last.is_empty() {
                        self.chunks.pop();
                    }
                    return;
                }
                None => {
                    self.chunks.pop();
                }
            }
        }
    }
}

impl Default for RustView {
    fn default() -> Self {
        Self::new()
    }
}

impl Drop for RustView {
    fn drop(&mut self) {
        LIVE_VIEWS.fetch_sub(1, Ordering::SeqCst);
    }
}
