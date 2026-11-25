// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

use std::io::Read;
use std::process::{Child, Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

struct Process(Child);

impl Drop for Process {
    fn drop(&mut self) {
        if self.0.try_wait().unwrap().is_none() {
            self.0.kill().unwrap();
        }
        self.0.wait().unwrap();
    }
}

fn wait(process: &mut Child) {
    let deadline = Instant::now() + Duration::from_secs(40);
    while process.try_wait().unwrap().is_none() {
        assert!(Instant::now() < deadline, "Process timed out");
        thread::sleep(Duration::from_millis(10));
    }
}

fn client(pipe: &str, command: &str) -> Output {
    let mut process = Process(
        Command::new(env!("CARGO_BIN_EXE_pwshc"))
            .args(["-pipe", pipe, "-command", command])
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap(),
    );
    let mut stdout = process.0.stdout.take().unwrap();
    let mut stderr = process.0.stderr.take().unwrap();
    let output = thread::spawn(move || {
        let mut bytes = Vec::new();
        stdout.read_to_end(&mut bytes).unwrap();
        bytes
    });
    let error = thread::spawn(move || {
        let mut bytes = Vec::new();
        stderr.read_to_end(&mut bytes).unwrap();
        bytes
    });
    wait(&mut process.0);
    Output {
        status: process.0.try_wait().unwrap().unwrap(),
        stdout: output.join().unwrap(),
        stderr: error.join().unwrap(),
    }
}

#[test]
#[ignore = "requires PWSHC_TEST_SERVER pointing to a pwsh build with -RunspaceServer"]
fn executes_against_powershell_and_preserves_runspace() {
    let pipe = format!(
        "pwshc-{}-{}",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_millis()
    );
    let mut server = Process(
        Command::new(std::env::var_os("PWSHC_TEST_SERVER").expect("Set PWSHC_TEST_SERVER"))
            .args(["-RunspaceServer", &pipe])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::inherit())
            .spawn()
            .unwrap(),
    );

    let result = client(&pipe, "$value = 40; function Get-Persistent { $value + 2 }");
    assert!(result.status.success(), "{result:?}");
    assert!(result.stdout.is_empty());
    assert!(result.stderr.is_empty());
    assert!(server.0.try_wait().unwrap().is_none());

    let result = client(&pipe, "Get-Persistent");
    assert!(result.status.success(), "{result:?}");
    assert_eq!(String::from_utf8(result.stdout).unwrap().trim(), "42");
    assert!(result.stderr.is_empty());

    let result = client(&pipe, "");
    assert!(result.status.success(), "{result:?}");
    assert!(result.stdout.is_empty());
    assert!(result.stderr.is_empty());

    let result = client(
        &pipe,
        "[Console]::OpenStandardOutput().Write([byte[]](0, 255, 65)); \
         [Console]::OpenStandardError().Write([byte[]](254, 66))",
    );
    assert!(result.status.success(), "{result:?}");
    assert_eq!(result.stdout, [0, 255, 65]);
    assert_eq!(result.stderr, [254, 66]);

    let text = "é💻".repeat(3000);
    let result = client(&pipe, &format!("[Console]::Out.Write('{text}')"));
    assert!(result.status.success(), "{result:?}");
    assert_eq!(result.stdout, text.as_bytes());
    assert!(result.stderr.is_empty());

    let result = client(&pipe, "Write-Error 'pwshc-error'");
    assert_eq!(result.status.code(), Some(1), "{result:?}");
    assert!(
        String::from_utf8(result.stderr)
            .unwrap()
            .contains("pwshc-error")
    );
    assert!(server.0.try_wait().unwrap().is_none());

    let result = client(&pipe, "exit 7");
    assert_eq!(result.status.code(), Some(7), "{result:?}");
    wait(&mut server.0);
    assert_eq!(server.0.try_wait().unwrap().unwrap().code(), Some(7));
}
