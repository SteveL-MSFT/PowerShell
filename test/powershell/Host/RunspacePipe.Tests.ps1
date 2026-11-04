# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

Describe 'Runspace server command-line modes' -Tag 'CI' {
    BeforeAll {
        $powershell = Join-Path $PSHOME 'pwsh'
        $processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

        function Start-PipeProcess {
            param([string]$Parameter, [string]$PipeName, [string]$Command)

            $startInfo = [System.Diagnostics.ProcessStartInfo]::new($powershell)
            $startInfo.UseShellExecute = $false
            $startInfo.RedirectStandardInput = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            $startInfo.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
            $startInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $startInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
            $startInfo.ArgumentList.Add($Parameter)
            $startInfo.ArgumentList.Add($PipeName)
            if ($PSBoundParameters.ContainsKey('Command')) {
                $startInfo.ArgumentList.Add('-Command')
                $startInfo.ArgumentList.Add($Command)
            }
            $process = [System.Diagnostics.Process]::Start($startInfo)
            $processes.Add($process)
            @{
                Process = $process
                Output = $process.StandardOutput.ReadToEndAsync()
                Error = $process.StandardError.ReadToEndAsync()
            }
        }

        function Start-PipePair {
            $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
            @{
                PipeName = $pipeName
                Server = Start-PipeProcess '-RunspaceServer' $pipeName
                Client = Start-PipeProcess '-ConnectToServer' $pipeName
            }
        }

        function Complete-PipePair {
            param($Pair, [int]$ExitCode = 0, [switch]$ServerExits)

            $Pair.Client.Process.StandardInput.Close()
            $Pair.Client.Process.WaitForExit(15000) | Should -BeTrue
            $Pair.Client.Process.ExitCode | Should -Be $ExitCode
            if (!$ServerExits) {
                $Pair.Server.Process.WaitForExit(300) | Should -BeFalse
                $shutdown = Start-PipeProcess '-ConnectToServer' $Pair.PipeName -Command 'exit'
                $shutdown.Process.WaitForExit(15000) | Should -BeTrue
                $shutdown.Process.ExitCode | Should -Be 0
                $shutdown.Output.GetAwaiter().GetResult() | Should -BeExactly ''
                $shutdown.Error.GetAwaiter().GetResult() | Should -BeExactly ''
                $Pair.Server.Process.WaitForExit(15000) | Should -BeTrue
                $Pair.Server.Process.ExitCode | Should -Be 0
            }
            else {
                $Pair.Server.Process.WaitForExit(15000) | Should -BeTrue
                $Pair.Server.Process.ExitCode | Should -Be $ExitCode
            }

            $Pair.Server.Output.GetAwaiter().GetResult() | Should -BeExactly ''
            $Pair.Server.Error.GetAwaiter().GetResult() | Should -BeExactly ''
        }
    }

    AfterEach {
        foreach ($process in $processes) {
            if (!$process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }

            $process.Dispose()
        }

        $processes.Clear()
    }

    It 'waits for a connection before executing commands and preserves session state' {
        $pair = Start-PipePair
        $pair.Client.Process.StandardInput.WriteLine('$value = 40')
        $pair.Client.Process.StandardInput.WriteLine('$value + 2')
        Complete-PipePair $pair

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly "42$([Environment]::NewLine)"
        $pair.Client.Error.GetAwaiter().GetResult() | Should -BeExactly ''
    }

    It 'does not execute server stdin while waiting for a client' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $server = Start-PipeProcess '-RunspaceServer' $pipeName
        $server.Process.StandardInput.WriteLine("throw 'server-stdin-must-not-execute'")
        $server.Process.WaitForExit(500) | Should -BeFalse
        $pair = @{
            PipeName = $pipeName
            Server = $server
            Client = Start-PipeProcess '-ConnectToServer' $pipeName
        }
        Complete-PipePair $pair

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly ''
        $pair.Client.Error.GetAwaiter().GetResult() | Should -BeExactly ''
    }

    It 'forwards stdout, stderr, and host output independently' {
        $pair = Start-PipePair
        $pair.Client.Process.StandardInput.WriteLine("[Console]::Out.Write('stdout'); [Console]::Error.Write('stderr')")
        $pair.Client.Process.StandardInput.WriteLine("Write-Host 'host'")
        $pair.Client.Process.StandardInput.WriteLine("Write-Error 'error-marker'")
        $pair.Client.Process.StandardInput.WriteLine("'last'")
        Complete-PipePair $pair

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly "stdouthost$([Environment]::NewLine)last$([Environment]::NewLine)"
        $pair.Client.Error.GetAwaiter().GetResult() | Should -Match 'stderr.*Write-Error: error-marker'
    }

    It 'forwards raw standard handles without adding newlines' {
        $pair = Start-PipePair
        $pair.Client.Process.StandardInput.WriteLine("[Console]::OpenStandardOutput().Write([Text.Encoding]::UTF8.GetBytes('raw-out'))")
        $pair.Client.Process.StandardInput.WriteLine("[Console]::OpenStandardError().Write([Text.Encoding]::UTF8.GetBytes('raw-err'))")
        Complete-PipePair $pair

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly 'raw-out'
        $pair.Client.Error.GetAwaiter().GetResult() | Should -BeExactly 'raw-err'
    }

    It 'forwards native stdout and stderr without mixing streams' {
        $pair = Start-PipePair
        $pair.Client.Process.StandardInput.WriteLine(@'
& (Join-Path $PSHOME 'pwsh') -NoProfile -NoLogo -Command "[Console]::Out.Write('native-out'); [Console]::Error.Write('native-err')"
'@)
        Complete-PipePair $pair

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly 'native-out'
        $pair.Client.Error.GetAwaiter().GetResult() | Should -BeExactly 'native-err'
    }

    It 'handles multiline commands and an unterminated final input line' {
        $pair = Start-PipePair
        $pair.Client.Process.StandardInput.Write("function Test-PipeFunction {`n'function-output'`n}`nTest-PipeFunction")
        Complete-PipePair $pair

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly "function-output$([Environment]::NewLine)"
        $pair.Client.Error.GetAwaiter().GetResult() | Should -BeExactly ''
    }

    It 'reports incomplete commands at end of input instead of discarding them' {
        $pair = Start-PipePair
        $pair.Client.Process.StandardInput.WriteLine('if ($true) {')
        Complete-PipePair $pair -ExitCode 1

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly ''
        $pair.Client.Error.GetAwaiter().GetResult() | Should -Match 'Missing closing'
    }

    It 'forwards Unicode input and output larger than a transport packet' {
        $pair = Start-PipePair
        $text = ([string][char]0x00E9 + [char]0x03BB + [char]0x4E2D) * 10000
        $pair.Client.Process.StandardInput.WriteLine("[Console]::Out.Write('$text'); [Console]::Error.Write('$text')")
        Complete-PipePair $pair

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly $text
        $pair.Client.Error.GetAwaiter().GetResult() | Should -BeExactly $text
    }

    It 'exits the client but keeps the server alive when client stdin is empty' {
        $pair = Start-PipePair
        Complete-PipePair $pair

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly ''
        $pair.Client.Error.GetAwaiter().GetResult() | Should -BeExactly ''
    }

    It 'propagates an explicit exit code while client stdin remains open' {
        $pair = Start-PipePair
        $pair.Client.Process.StandardInput.WriteLine('exit 7')
        $pair.Client.Process.StandardInput.Flush()
        $pair.Client.Process.WaitForExit(15000) | Should -BeTrue
        Complete-PipePair $pair -ExitCode 7 -ServerExits

        $pair.Client.Output.GetAwaiter().GetResult() | Should -BeExactly ''
        $pair.Client.Error.GetAwaiter().GetResult() | Should -BeExactly ''
    }

    It 'reports an unexpected server disconnection on stderr' {
        $pair = Start-PipePair
        $markerPath = Join-Path $TestDrive 'connected'
        $escapedPath = $markerPath.Replace("'", "''")
        $pair.Client.Process.StandardInput.WriteLine("[IO.File]::WriteAllText('$escapedPath', 'connected')")
        $pair.Client.Process.StandardInput.Flush()
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path $markerPath) -and $timer.ElapsedMilliseconds -lt 15000) {
            Start-Sleep -Milliseconds 50
        }

        $markerPath | Should -Exist
        $pair.Server.Process.Kill()
        $pair.Server.Process.WaitForExit()
        $pair.Client.Process.WaitForExit(15000) | Should -BeTrue

        $pair.Client.Process.ExitCode | Should -Be 70
        $pair.Client.Error.GetAwaiter().GetResult() | Should -Not -BeNullOrEmpty
    }

    It 'reports malformed server packets without crashing' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $fakeServer = [System.IO.Pipes.NamedPipeServerStream]::new(
            $pipeName, [System.IO.Pipes.PipeDirection]::InOut, 1,
            [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::CurrentUserOnly)
        try {
            $client = Start-PipeProcess '-ConnectToServer' $pipeName
            $connection = $fakeServer.WaitForConnectionAsync()
            $connection.Wait(15000) | Should -BeTrue
            $fakeServer.Write([byte[]]@(6, 0, 0, 0, 0))
            $fakeServer.Write([byte[]]@(2, 255, 255, 255, 255))
            $fakeServer.Flush()
            $client.Process.WaitForExit(15000) | Should -BeTrue

            $client.Process.ExitCode | Should -Be 70
            $client.Output.GetAwaiter().GetResult() | Should -BeExactly ''
            $client.Error.GetAwaiter().GetResult() | Should -Match 'invalid transport packet'
        }
        finally {
            $fakeServer.Dispose()
        }
    }

    It 'executes one-shot commands in the same runspace across client connections' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $server = Start-PipeProcess '-RunspaceServer' $pipeName
        foreach ($command in @('$value = 40; function Test-Persistent { $value + 2 }', 'Test-Persistent')) {
            $client = Start-PipeProcess '-ConnectToServer' $pipeName -Command $command
            $client.Process.WaitForExit(15000) | Should -BeTrue
            $client.Process.ExitCode | Should -Be 0
            $client.Error.GetAwaiter().GetResult() | Should -BeExactly ''
            $server.Process.HasExited | Should -BeFalse
        }

        $client.Output.GetAwaiter().GetResult() | Should -BeExactly "42$([Environment]::NewLine)"
        $streamed = Start-PipeProcess '-ConnectToServer' $pipeName
        $streamed.Process.StandardInput.WriteLine('Test-Persistent')
        $streamed.Process.StandardInput.Close()
        $streamed.Process.WaitForExit(15000) | Should -BeTrue
        $streamed.Process.ExitCode | Should -Be 0
        $streamed.Output.GetAwaiter().GetResult() | Should -BeExactly "42$([Environment]::NewLine)"
        $streamed.Error.GetAwaiter().GetResult() | Should -BeExactly ''
        $server.Process.HasExited | Should -BeFalse
        $shutdown = Start-PipeProcess '-ConnectToServer' $pipeName -Command 'exit 7'
        $shutdown.Process.WaitForExit(15000) | Should -BeTrue
        $server.Process.WaitForExit(15000) | Should -BeTrue
        $shutdown.Process.ExitCode | Should -Be 7
        $server.Process.ExitCode | Should -Be 7
    }

    It 'executes a multiline command as one script without reading client stdin' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $server = Start-PipeProcess '-RunspaceServer' $pipeName
        $client = Start-PipeProcess '-ConnectToServer' $pipeName -Command "if (`$true) {`n[Console]::Out.Write('one-shot')`n}`n[Console]::Error.Write('command-error')"
        $pair = @{ PipeName = $pipeName; Server = $server; Client = $client }
        $client.Process.WaitForExit(15000) | Should -BeTrue
        Complete-PipePair $pair

        $client.Output.GetAwaiter().GetResult() | Should -BeExactly 'one-shot'
        $client.Error.GetAwaiter().GetResult() | Should -BeExactly 'command-error'
    }

    It 'forwards a one-shot command larger than a packet and executes it in the server process' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $server = Start-PipeProcess '-RunspaceServer' $pipeName
        $text = ([string][char]0x00E9 + [char]0x03BB + [char]0x4E2D) * 10000
        $client = Start-PipeProcess '-ConnectToServer' $pipeName -Command "[Console]::Out.Write('$text'); [Console]::Error.Write('$text'); `$PID"
        $pair = @{ PipeName = $pipeName; Server = $server; Client = $client }
        $client.Process.WaitForExit(15000) | Should -BeTrue
        Complete-PipePair $pair

        $client.Output.GetAwaiter().GetResult() | Should -BeExactly "$text$($server.Process.Id)$([Environment]::NewLine)"
        $client.Error.GetAwaiter().GetResult() | Should -BeExactly $text
    }

    It 'returns a failing command status without shutting down the server' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $server = Start-PipeProcess '-RunspaceServer' $pipeName
        $client = Start-PipeProcess '-ConnectToServer' $pipeName -Command "throw 'command-failed'"
        Complete-PipePair @{ PipeName = $pipeName; Server = $server; Client = $client } -ExitCode 1

        $client.Error.GetAwaiter().GetResult() | Should -Match 'command-failed'
    }

    It 'preserves command success state when returning results to the client' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $server = Start-PipeProcess '-RunspaceServer' $pipeName
        $failed = Start-PipeProcess '-ConnectToServer' $pipeName -Command "Write-Error 'failed'"
        $failed.Process.WaitForExit(15000) | Should -BeTrue
        $failed.Process.ExitCode | Should -Be 1
        $failed.Error.GetAwaiter().GetResult() | Should -Match 'failed'
        $next = Start-PipeProcess '-ConnectToServer' $pipeName -Command '$?'
        Complete-PipePair @{ PipeName = $pipeName; Server = $server; Client = $next }

        $next.Output.GetAwaiter().GetResult() | Should -BeExactly "False$([Environment]::NewLine)"
        $next.Error.GetAwaiter().GetResult() | Should -BeExactly ''
    }

    It 'accepts empty, whitespace, and comment-only commands without shutting down the server' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $server = Start-PipeProcess '-RunspaceServer' $pipeName
        foreach ($command in @('', ' ', '# comment')) {
            $client = Start-PipeProcess '-ConnectToServer' $pipeName -Command $command
            $client.Process.WaitForExit(15000) | Should -BeTrue
            $client.Process.ExitCode | Should -Be 0
            $client.Output.GetAwaiter().GetResult() | Should -BeExactly ''
            $client.Error.GetAwaiter().GetResult() | Should -BeExactly ''
            $server.Process.HasExited | Should -BeFalse
        }

        Complete-PipePair @{ PipeName = $pipeName; Server = $server; Client = $client }
    }

    It 'keeps state and accepts another client after an abrupt disconnect' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $server = Start-PipeProcess '-RunspaceServer' $pipeName
        $client = Start-PipeProcess '-ConnectToServer' $pipeName
        $markerPath = Join-Path $TestDrive 'persistent-state'
        $escapedPath = $markerPath.Replace("'", "''")
        $client.Process.StandardInput.WriteLine("`$value = 123; [IO.File]::WriteAllText('$escapedPath', 'ready'); Start-Sleep -Milliseconds 500; [Console]::Out.Write('x' * 100000); [Console]::Error.Write('y' * 100000); `$value++")
        $client.Process.StandardInput.Flush()
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path $markerPath) -and $timer.ElapsedMilliseconds -lt 15000) {
            Start-Sleep -Milliseconds 50
        }

        $markerPath | Should -Exist
        $client.Process.Kill()
        $client.Process.WaitForExit()
        $next = Start-PipeProcess '-ConnectToServer' $pipeName -Command '$value'
        $next.Process.WaitForExit(15000) | Should -BeTrue
        $next.Process.ExitCode | Should -Be 0
        $next.Output.GetAwaiter().GetResult() | Should -BeExactly "124$([Environment]::NewLine)"
        $next.Error.GetAwaiter().GetResult() | Should -BeExactly ''
        $server.Process.HasExited | Should -BeFalse
        $shutdown = Start-PipeProcess '-ConnectToServer' $pipeName -Command 'exit'
        $shutdown.Process.WaitForExit(15000) | Should -BeTrue
        $server.Process.WaitForExit(15000) | Should -BeTrue
        $server.Process.ExitCode | Should -Be 0
        $server.Error.GetAwaiter().GetResult() | Should -Not -BeNullOrEmpty
    }

    It 'accepts another client after rejecting malformed client packets' {
        $pipeName = 'ps-test-' + [guid]::NewGuid().ToString('N')
        $server = Start-PipeProcess '-RunspaceServer' $pipeName
        $badClient = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $badClient.Connect(15000)
            $ready = [byte[]]::new(5)
            $badClient.ReadExactly($ready)
            $badClient.Write([byte[]]@(255, 0, 0, 0, 0))
            $badClient.Flush()
        }
        finally {
            $badClient.Dispose()
        }

        $next = Start-PipeProcess '-ConnectToServer' $pipeName -Command "'after-malformed'"
        $next.Process.WaitForExit(15000) | Should -BeTrue
        $next.Output.GetAwaiter().GetResult() | Should -BeExactly "after-malformed$([Environment]::NewLine)"
        $next.Error.GetAwaiter().GetResult() | Should -BeExactly ''
        $next.Process.ExitCode | Should -Be 0
        $server.Process.HasExited | Should -BeFalse
        $shutdown = Start-PipeProcess '-ConnectToServer' $pipeName -Command 'exit'
        $shutdown.Process.WaitForExit(15000) | Should -BeTrue
        $server.Process.WaitForExit(15000) | Should -BeTrue
        $server.Process.ExitCode | Should -Be 0
        $server.Error.GetAwaiter().GetResult() | Should -Match 'invalid transport packet'
    }
}
