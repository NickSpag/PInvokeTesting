
use std::io;
use std::process;

mod view;
use view::RustView;

pub enum ReadError {
    /// An error occurred in the underlying stream
    Io(io::Error),
    Disconnect,
}

// /// A trait for types which can handle RPCs.
// ///
// /// Types which implement `MethodHandler` are also responsible for implementing
// /// `Parser`; `Parser` is provided when Self::Notification and Self::Request
// /// can be used with serde::DeserializeOwned.
// pub trait Handler {
//     type Notification: DeserializeOwned;
//     type Request: DeserializeOwned;
//     fn handle_notification(&mut self, ctx: &RpcCtx, rpc: Self::Notification);
//     fn handle_request(&mut self, ctx: &RpcCtx, rpc: Self::Request) -> Result<Value, RemoteError>;
//     #[allow(unused_variables)]
//     fn idle(&mut self, ctx: &RpcCtx, token: usize) {}
// }

fn main() {
    println!("Beginning main");

    let stdin = io::stdin();

    let mut input = String::new();
    match stdin.read_line(&mut input) {
        Ok(n) => {
            let _view = Box::into_raw(Box::new(RustView::new()));
            
            //println!("{} bytes read", n);
            //println!("{}", input);
            process::exit(0);
        }
        Err(error) => {
            println!("error: {}", error);
            process::exit(0);
        }
    }
}
