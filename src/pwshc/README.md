# pwshc

A standalone Rust client for `pwsh -RunspaceServer`. No PowerShell runtime or
local runspace is loaded by the client. Supports Windows named pipes and the
Unix-domain sockets used by .NET named pipes on Linux and macOS.

Build with a Rust toolchain supporting edition 2024:

```sh
cargo build --release --manifest-path src/pwshc/Cargo.toml
```

The executable is `src/pwshc/target/release/pwshc` (`pwshc.exe` on Windows).
It is built separately from PowerShell; existing PowerShell packaging is unchanged.

Start a server with a PowerShell build that supports runspace-server mode:

```powershell
pwsh -RunspaceServer mypipe
```

Then connect from another terminal:

```powershell
pwshc -pipe mypipe -command '$value = 40'
pwshc -pipe mypipe -command '$value + 2'
pwshc -pipe mypipe -command 'exit'
```

Both arguments are required, are case-insensitive, and may appear in either
order. Quote the command as one shell argument; an empty command is allowed.
Other switches, duplicate arguments, and extra positional arguments are errors.
Unlike the full `pwsh` client, `pwshc` does not read stdin, and `-command -`
is a literal command, not a request to read stdin.

The client sends the command as UTF-8, copies stdout and stderr independently
without decoding or adding newlines, and returns the server's command exit code.
Unix shells expose only the low eight bits of an exit code. Argument errors
return 64; connection and protocol errors return 70 and print a diagnostic to
stderr.

Disconnecting leaves the server and its runspace state alive; an explicit
PowerShell `exit` shuts down the server. Connection and readiness have a combined
30-second timeout. Once a command has been sent it is never retried, and command
execution itself has no timeout. On Unix, use the same `TMPDIR` as the server so
both processes resolve the same .NET `CoreFxPipe_` socket path. Unix peers must
belong to the client's effective user. Windows connections are local and use
identification-level security, preventing server impersonation of the client.

Run unit and mock-protocol tests:

```sh
cargo test --manifest-path src/pwshc/Cargo.toml
```

Run end-to-end tests against a freshly built server (POSIX shell example):

```sh
PWSHC_TEST_SERVER="$PWD/src/powershell-unix/bin/Debug/net11.0/pwsh" \
  cargo test --manifest-path src/pwshc/Cargo.toml --test runspace_server -- --ignored
```

On Windows, set `PWSHC_TEST_SERVER` to the built `pwsh.exe` before running the
same `cargo test` command.
