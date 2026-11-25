// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

use std::io::{self, Write};
use std::time::Duration;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

const BUFFER_SIZE: usize = 8192;
const CONNECT_TIMEOUT: Duration = Duration::from_secs(30);
const END_INPUT: u8 = 1;
const OUTPUT: u8 = 2;
const ERROR: u8 = 3;
const EXIT: u8 = 4;
const COMMAND: u8 = 5;
const READY: u8 = 6;
const USAGE: &str = "Usage: pwshc -pipe <name> -command <string>";

struct Arguments {
    pipe: String,
    command: String,
}

fn parse_arguments(args: impl IntoIterator<Item = String>) -> Result<Arguments, String> {
    let mut args = args.into_iter();
    let mut pipe = None;
    let mut command = None;
    while let Some(argument) = args.next() {
        let value = if argument.eq_ignore_ascii_case("-pipe") {
            &mut pipe
        } else if argument.eq_ignore_ascii_case("-command") {
            &mut command
        } else {
            return Err(format!("Unexpected argument: {argument}"));
        };
        if value.is_some() {
            return Err(format!("Duplicate argument: {argument}"));
        }
        *value = Some(
            args.next()
                .ok_or_else(|| format!("{argument} requires a value"))?,
        );
    }

    let pipe = pipe.ok_or("-pipe is required")?;
    validate_pipe_name(&pipe)?;
    Ok(Arguments {
        pipe,
        command: command.ok_or("-command is required")?,
    })
}

fn validate_pipe_name(name: &str) -> Result<(), String> {
    if name.trim().is_empty()
        || name.starts_with(['-', '/'])
        || name.contains(['/', '\\', ':', '\0'])
        || name.eq_ignore_ascii_case("anonymous")
    {
        return Err("Invalid pipe name".into());
    }

    #[cfg(windows)]
    let too_long = name.encode_utf16().count() > 256 - r"\\.\pipe\".len();
    #[cfg(unix)]
    let too_long = {
        use std::os::unix::ffi::OsStrExt;
        // sockaddr_un.sun_path includes a terminating null byte.
        #[cfg(target_os = "macos")]
        const PATH_LIMIT: usize = 104;
        #[cfg(not(target_os = "macos"))]
        const PATH_LIMIT: usize = 108;
        pipe_path(name).as_os_str().as_bytes().len() >= PATH_LIMIT
    };

    if too_long {
        return Err("Pipe name is too long".into());
    }
    Ok(())
}

#[cfg(unix)]
fn pipe_path(name: &str) -> std::path::PathBuf {
    // Match .NET's fallback, not Rust's per-user Darwin temporary directory.
    let directory = std::env::var_os("TMPDIR")
        .filter(|value| !value.is_empty())
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| std::path::PathBuf::from("/tmp"));
    directory.join(format!("CoreFxPipe_{name}"))
}

#[cfg(unix)]
async fn open_pipe(name: &str) -> io::Result<tokio::net::UnixStream> {
    let stream = tokio::net::UnixStream::connect(pipe_path(name)).await?;
    // SAFETY: geteuid has no preconditions and does not access caller memory.
    if stream.peer_cred()?.uid() != unsafe { libc::geteuid() } {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Runspace server belongs to a different user",
        ));
    }
    Ok(stream)
}

#[cfg(windows)]
async fn open_pipe(name: &str) -> io::Result<tokio::net::windows::named_pipe::NamedPipeClient> {
    // ClientOptions uses SECURITY_IDENTIFICATION by default.
    tokio::net::windows::named_pipe::ClientOptions::new().open(format!(r"\\.\pipe\{name}"))
}

fn can_retry(error: &io::Error) -> bool {
    #[cfg(windows)]
    if error.raw_os_error() == Some(231) {
        // ERROR_PIPE_BUSY: the server is completing the previous connection.
        return true;
    }
    matches!(
        error.kind(),
        io::ErrorKind::NotFound
            | io::ErrorKind::ConnectionRefused
            | io::ErrorKind::ConnectionReset
            | io::ErrorKind::ConnectionAborted
            | io::ErrorKind::BrokenPipe
            | io::ErrorKind::UnexpectedEof
            | io::ErrorKind::WouldBlock
    )
}

async fn read_ready(pipe: &mut (impl AsyncRead + Unpin)) -> io::Result<()> {
    let mut header = [0; 5];
    pipe.read_exact(&mut header).await?;
    if header != [READY, 0, 0, 0, 0] {
        return Err(invalid_packet());
    }
    Ok(())
}

async fn connect_pipe(name: &str) -> io::Result<impl AsyncRead + AsyncWrite + Unpin> {
    tokio::time::timeout(CONNECT_TIMEOUT, async {
        loop {
            let attempt = async {
                let mut pipe = open_pipe(name).await?;
                read_ready(&mut pipe).await?;
                Ok(pipe)
            }
            .await;
            match attempt {
                Ok(pipe) => break Ok(pipe),
                Err(error) if can_retry(&error) => {
                    // Retry only before readiness, never after sending a command.
                    tokio::time::sleep(Duration::from_millis(10)).await;
                }
                Err(error) => break Err(error),
            }
        }
    })
    .await
    .map_err(|_| {
        io::Error::new(
            io::ErrorKind::TimedOut,
            "Timed out waiting for the runspace server to become ready",
        )
    })?
}

fn invalid_packet() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "Invalid runspace pipe packet")
}

async fn write_packet(
    pipe: &mut (impl AsyncWrite + Unpin),
    kind: u8,
    payload: &[u8],
) -> io::Result<()> {
    pipe.write_all(&[kind]).await?;
    pipe.write_all(&(payload.len() as i32).to_le_bytes())
        .await?;
    pipe.write_all(payload).await
}

async fn execute(
    pipe: &mut (impl AsyncRead + AsyncWrite + Unpin),
    command: &str,
    output: &mut impl Write,
    error: &mut impl Write,
) -> io::Result<i32> {
    if command.is_empty() {
        write_packet(pipe, COMMAND, &[]).await?;
    } else {
        for chunk in command.as_bytes().chunks(BUFFER_SIZE) {
            write_packet(pipe, COMMAND, chunk).await?;
        }
    }
    write_packet(pipe, END_INPUT, &[]).await?;
    pipe.flush().await?;

    let mut payload = [0; BUFFER_SIZE];
    loop {
        let kind = pipe.read_u8().await?;
        let length = pipe.read_i32_le().await?;
        if !(0..=BUFFER_SIZE as i32).contains(&length) {
            return Err(invalid_packet());
        }
        let length = length as usize;
        match kind {
            OUTPUT | ERROR => {}
            EXIT if length == 4 => {}
            _ => return Err(invalid_packet()),
        }
        pipe.read_exact(&mut payload[..length]).await?;
        match kind {
            OUTPUT => {
                output.write_all(&payload[..length])?;
                output.flush()?;
            }
            ERROR => {
                error.write_all(&payload[..length])?;
                error.flush()?;
            }
            EXIT => return Ok(i32::from_le_bytes(payload[..4].try_into().unwrap())),
            _ => unreachable!(),
        }
    }
}

fn run(arguments: Arguments) -> io::Result<i32> {
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?
        .block_on(async {
            let mut pipe = connect_pipe(&arguments.pipe).await?;
            execute(
                &mut pipe,
                &arguments.command,
                &mut io::stdout().lock(),
                &mut io::stderr().lock(),
            )
            .await
        })
}

fn main() {
    let arguments = match parse_arguments(std::env::args().skip(1)) {
        Ok(arguments) => arguments,
        Err(error) => {
            eprintln!("pwshc: {error}\n{USAGE}");
            std::process::exit(64);
        }
    };
    match run(arguments) {
        Ok(status) => std::process::exit(status),
        Err(error) => {
            eprintln!("pwshc: {error}");
            std::process::exit(70);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(args: &[&str]) -> Result<Arguments, String> {
        parse_arguments(args.iter().map(|value| (*value).to_owned()))
    }

    fn runtime() -> tokio::runtime::Runtime {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap()
    }

    #[test]
    fn parses_required_arguments_in_either_order() {
        let args = parse(&["-PIPE", "test", "-Command", "'hello world'"]).unwrap();
        assert_eq!(args.pipe, "test");
        assert_eq!(args.command, "'hello world'");
        assert_eq!(
            parse(&["-command", "", "-pipe", "test"]).unwrap().command,
            ""
        );
    }

    #[test]
    fn rejects_missing_duplicate_or_extra_arguments() {
        for args in [
            vec![],
            vec!["-pipe"],
            vec!["-pipe", "test"],
            vec!["-command", "42"],
            vec!["-pipe", "test", "-command"],
            vec!["-pipe", "test", "-pipe", "other", "-command", "42"],
            vec!["-pipe", "test", "-command", "42", "-command", "43"],
            vec!["-pipe", "test", "-command", "42", "-NoProfile"],
            vec!["-pipe", "test", "-command", "42", "extra"],
        ] {
            assert!(parse(&args).is_err(), "{args:?}");
        }
    }

    #[test]
    fn rejects_invalid_and_overlong_pipe_names() {
        for name in [
            "",
            " ",
            "-name",
            "/name",
            "a/b",
            "a\\b",
            "a:b",
            "a\0b",
            "ANONYMOUS",
        ] {
            assert!(validate_pipe_name(name).is_err(), "{name:?}");
        }
        assert!(validate_pipe_name(&"x".repeat(300)).is_err());
        assert!(validate_pipe_name("ps-test-123").is_ok());
    }

    #[test]
    fn requires_exact_readiness_packet() {
        runtime().block_on(async {
            for header in [[READY, 1, 0, 0, 0], [OUTPUT, 0, 0, 0, 0]] {
                assert_eq!(
                    read_ready(&mut &header[..]).await.unwrap_err().kind(),
                    io::ErrorKind::InvalidData
                );
            }
            assert!(read_ready(&mut &[READY, 0][..]).await.is_err());
            read_ready(&mut &[READY, 0, 0, 0, 0][..]).await.unwrap();
        });
    }

    #[cfg(unix)]
    #[test]
    fn reconnects_before_readiness_without_sending_or_replaying_commands() {
        runtime().block_on(async {
            let name = format!("pwshc-retry-{}", std::process::id());
            let path = pipe_path(&name);
            let listener = tokio::net::UnixListener::bind(&path).unwrap();
            let server = tokio::spawn(async move {
                let (mut stale, _) = listener.accept().await.unwrap();
                let mut byte = [0];
                assert!(
                    tokio::time::timeout(Duration::from_millis(25), stale.read(&mut byte))
                        .await
                        .is_err(),
                    "Client sent data before readiness"
                );
                drop(stale);
                let (mut ready, _) = listener.accept().await.unwrap();
                write_packet(&mut ready, READY, &[]).await.unwrap();
                let mut request = [0; 11];
                ready.read_exact(&mut request).await.unwrap();
                assert_eq!(request, [COMMAND, 1, 0, 0, 0, b'x', END_INPUT, 0, 0, 0, 0]);
                // Disconnection after submission must fail, not replay the command.
                drop(ready);
                assert!(
                    tokio::time::timeout(Duration::from_millis(25), listener.accept())
                        .await
                        .is_err()
                );
            });
            let mut pipe = connect_pipe(&name).await.unwrap();
            assert!(
                execute(&mut pipe, "x", &mut Vec::new(), &mut Vec::new())
                    .await
                    .is_err()
            );
            server.await.unwrap();
            std::fs::remove_file(path).unwrap();
        });
    }

    #[test]
    fn chunks_unicode_commands_and_relays_raw_streams_and_exit_code() {
        runtime().block_on(async {
            let (mut client, mut server) = tokio::io::duplex(64);
            let command = format!("{}💻\n'next line'", "x".repeat(BUFFER_SIZE - 1));
            let expected = command.clone();
            let server = tokio::spawn(async move {
                let mut received = Vec::new();
                loop {
                    let kind = server.read_u8().await.unwrap();
                    let length = server.read_i32_le().await.unwrap() as usize;
                    if kind == END_INPUT {
                        assert_eq!(length, 0);
                        break;
                    }
                    assert_eq!(kind, COMMAND);
                    assert!(length <= BUFFER_SIZE);
                    let start = received.len();
                    received.resize(start + length, 0);
                    server.read_exact(&mut received[start..]).await.unwrap();
                }
                assert_eq!(received, expected.as_bytes());
                write_packet(&mut server, OUTPUT, &[0, 255, b'o'])
                    .await
                    .unwrap();
                write_packet(&mut server, ERROR, &[254, b'e'])
                    .await
                    .unwrap();
                write_packet(&mut server, OUTPUT, b"more").await.unwrap();
                write_packet(&mut server, EXIT, &7_i32.to_le_bytes())
                    .await
                    .unwrap();
            });
            let (mut output, mut error) = (Vec::new(), Vec::new());
            assert_eq!(
                execute(&mut client, &command, &mut output, &mut error)
                    .await
                    .unwrap(),
                7
            );
            assert_eq!(output, [0, 255, b'o', b'm', b'o', b'r', b'e']);
            assert_eq!(error, [254, b'e']);
            server.await.unwrap();
        });
    }

    #[test]
    fn sends_empty_command_marker() {
        runtime().block_on(async {
            let (mut client, mut server) = tokio::io::duplex(64);
            let server = tokio::spawn(async move {
                let mut frames = [0; 10];
                server.read_exact(&mut frames).await.unwrap();
                assert_eq!(frames, [COMMAND, 0, 0, 0, 0, END_INPUT, 0, 0, 0, 0]);
                write_packet(&mut server, EXIT, &0_i32.to_le_bytes())
                    .await
                    .unwrap();
            });
            assert_eq!(
                execute(&mut client, "", &mut Vec::new(), &mut Vec::new())
                    .await
                    .unwrap(),
                0
            );
            server.await.unwrap();
        });
    }

    #[test]
    fn rejects_invalid_frames_and_disconnects() {
        runtime().block_on(async {
            for response in [
                vec![OUTPUT, 255, 255, 255, 255],
                vec![OUTPUT, 1, 32, 0, 0],
                vec![READY, 0, 0, 0, 0],
                vec![EXIT, 0, 0, 0, 0],
                vec![OUTPUT, 2, 0, 0, 0, b'x'],
                vec![],
            ] {
                let (mut client, mut server) = tokio::io::duplex(64);
                let server = tokio::spawn(async move {
                    let mut request = [0; 10];
                    server.read_exact(&mut request).await.unwrap();
                    server.write_all(&response).await.unwrap();
                });
                assert!(
                    execute(&mut client, "", &mut Vec::new(), &mut Vec::new())
                        .await
                        .is_err()
                );
                server.await.unwrap();
            }
        });
    }
}
