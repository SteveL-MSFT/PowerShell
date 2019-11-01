# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License.

Import-Module HelpersCommon

Describe 'PowerShell WinExe tests' -Tags CI {
    It 'pwshw.exe works' -Skip:(!$IsWindows) {
        pwshw -noprofile -command "'hello' > $TestDrive\out.txt"
        # since pwshw runs async, we wait til it's gone
        Wait-UntilTrue -sb { $null -eq (Get-Process pwshw) }

        "$TestDrive\out.txt" | Should -FileContentMatch 'hello'
    }
}