$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
[Reflection.Assembly]::LoadFrom((Join-Path $root 'dist\ghub-repair.exe')) | Out-Null
[GhubRepair.Repair]::Check()
$manifest=Get-Content -LiteralPath (Join-Path ([GhubRepair.Repair]::DataRoot) 'current.json') -Raw -Encoding UTF8
$depot=[GhubRepair.Repair]::SelectDepot($manifest)
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$request=[Net.HttpWebRequest]::Create($depot.Url)
$request.AllowAutoRedirect=$false
$request.Timeout=30000
$request.ReadWriteTimeout=30000
$response=$request.GetResponse()
try {
    $stream=$response.GetResponseStream()
    $memory=[IO.MemoryStream]::new()
    try {
        $buffer=New-Object byte[] 8192
        while (($count=$stream.Read($buffer,0,$buffer.Length)) -gt 0) {
            if ($memory.Length+$count -gt $depot.Size) { throw 'Response too large' }
            $memory.Write($buffer,0,$count)
        }
        if (-not [GhubRepair.Repair]::ValidBytes($memory.ToArray(),$depot)) { throw 'Official download checksum mismatch' }
        'PASS: live official HTTPS download matched installed manifest SHA256 and size ('+$depot.Size+' bytes); no cache changes.'
    } finally { $stream.Dispose(); $memory.Dispose() }
} finally { $response.Dispose() }
