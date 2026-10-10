param([Parameter(Mandatory=$true)][string]$OpenApiPath,[string]$OutputPath='docs/api-route-inventory.md')
$ErrorActionPreference='Stop'
$document=Get-Content -LiteralPath $OpenApiPath -Raw -Encoding UTF8 | ConvertFrom-Json
$rows=New-Object 'System.Collections.Generic.List[string]'
$methods=@('get','post','put','patch','delete','head','options','trace')
foreach($path in ($document.paths.PSObject.Properties | Sort-Object Name)){
    foreach($entry in ($path.Value.PSObject.Properties | Where-Object {$methods -contains $_.Name} | Sort-Object Name)){
        $operation=$entry.Value
        $security=if($operation.security.Count -eq 0){'Anonymous'}elseif($operation.security[0].PSObject.Properties.Name -contains 'ProcessingWorker'){'ProcessingWorker'}else{'Bearer'}
        $headers=@($operation.parameters | Where-Object {$_.in -eq 'header' -and $_.required} | ForEach-Object {$_.name}) -join ', '
        if(!$headers){$headers='None'}
        $responses=($operation.responses.PSObject.Properties.Name | Sort-Object) -join ', '
        $rows.Add("| $($entry.Name.ToUpperInvariant()) | $($path.Name) | $security | $headers | $responses |")
    }
}
$summary="$($rows.Count) HTTP operations in the captured source artifact. Metadata inventory only; source/test/deployment acceptance is tracked separately in [the implementation checklist](api-implementation-checklist.md). Regenerate with scripts/generate-api-inventory.ps1 after contract changes."
$lines=@('# Route inventory generated from OpenAPI','',$summary,'','| Method | Endpoint | Authentication metadata | Required headers | Declared responses |','|---|---|---|---|---|')+$rows.ToArray()
[IO.File]::WriteAllLines([IO.Path]::GetFullPath($OutputPath),$lines,[Text.UTF8Encoding]::new($false))
