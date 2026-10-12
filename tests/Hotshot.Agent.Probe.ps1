[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).Substring(0, 24)
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', "Hotshot.Agent.v1.$hash.$session", [IO.Pipes.PipeDirection]::InOut,
    [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
try {
    $pipe.Connect(3000)
    $request = [Text.Encoding]::UTF8.GetBytes('{"operation":"windows","limit":1}')
    $length = [BitConverter]::GetBytes([int]$request.Length)
    $pipe.Write($length, 0, 4)
    $pipe.Write($request, 0, $request.Length)
    $pipe.Flush()
    function Read-Exact([byte[]]$buffer) {
        $offset = 0
        while ($offset -lt $buffer.Length) {
            $read = $pipe.Read($buffer, $offset, $buffer.Length - $offset)
            if ($read -eq 0) { throw 'The agent connection closed without a response.' }
            $offset += $read
        }
    }
    $header = [byte[]]::new(4)
    Read-Exact $header
    $size = [BitConverter]::ToInt32($header, 0)
    if ($size -lt 1 -or $size -gt 32MB) { throw 'Invalid agent response length.' }
    $response = [byte[]]::new($size)
    Read-Exact $response
    [Console]::WriteLine([Text.Encoding]::UTF8.GetString($response))
} finally { $pipe.Dispose() }
