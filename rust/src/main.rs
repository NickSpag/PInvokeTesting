//! The out-of-process alternative to the library: one JSON request per line on
//! stdin, one JSON reply per line on stdout, the way xi-core talks to its
//! frontends. It edits the same RustView the library does.

use std::io::{self, BufRead, BufWriter, Write};

use ffi_test::view::RustView;
use serde::{Deserialize, Serialize};

#[derive(Deserialize)]
#[serde(tag = "method", content = "params", rename_all = "snake_case")]
enum Request {
    Insert { chars: String },
    DeleteBackward,
}

#[derive(Serialize)]
#[serde(tag = "method", content = "params", rename_all = "snake_case")]
enum Reply {
    Update { len: usize },
    Error { message: String },
}

fn main() -> io::Result<()> {
    let mut view = RustView::new();
    let stdin = io::stdin().lock();
    let mut stdout = BufWriter::new(io::stdout().lock());

    // Runs until the frontend closes stdin.
    for line in stdin.lines() {
        let reply = match serde_json::from_str::<Request>(&line?) {
            Ok(Request::Insert { chars }) => {
                view.insert(&chars);
                Reply::Update { len: view.len() }
            }
            Ok(Request::DeleteBackward) => {
                view.delete_backward();
                Reply::Update { len: view.len() }
            }
            Err(err) => Reply::Error { message: err.to_string() },
        };
        serde_json::to_writer(&mut stdout, &reply)?;
        stdout.write_all(b"\n")?;
        // Flush per reply: the frontend is waiting on it.
        stdout.flush()?;
    }
    Ok(())
}
