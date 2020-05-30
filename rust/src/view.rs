pub struct RustView {
    text: String
}

impl RustView {
    pub fn new() -> RustView {
        RustView { text: String::from("hey dotnet!") }
    }

    pub fn insert(&mut self, new_text: &str){
        self.text.push_str(new_text);
    }
}