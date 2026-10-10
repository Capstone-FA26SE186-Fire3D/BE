param([Parameter(Mandatory=$true)][string]$OpenApiPath,[string]$OutputPath='docs/api-route-inventory.md')
$ErrorActionPreference='Stop'
$document=Get-Content -LiteralPath $OpenApiPath -Raw -Encoding UTF8 | ConvertFrom-Json
$rows=New-Object 'System.Collections.Generic.List[string]'
foreach($path in $document.paths.PSObject.Properties | Sort-Object Name){
 foreach($method in $path.Value.PSObject.Properties | Sort-Object Name){
  if($method.Name -notin @('get','post','put','patch','delete','head','options','trace')){continue}
  $operation=$method.Value
  $security=$operation.security;if($null -eq $security){$security=$document.security}
  $auth=@($security | ForEach-Object {$_.PSObject.Properties.Name} | Sort-Object -Unique) -join ', '
  if(!$auth){$auth='Anonymous'}
  $headers=@($operation.parameters | Where-Object {$_.'in' -eq 'header' -and $_.required} | ForEach-Object {$_.name}) -join ', '
  if(!$headers){$headers='None'}
  $responses=@($operation.responses.PSObject.Properties.Name | Sort-Object) -join ', '
  $rows.Add('| '+$method.Name.ToUpperInvariant()+' | '+$path.Name+' | '+$auth+' | '+$headers+' | '+$responses+' |')
 }
}
$preamble=@"
# Route inventory generated from OpenAPI

$($rows.Count) HTTP operations in the captured source test artifact. Includes metadata only; this is not a completion or deployment checklist. Run the generator again after route/metadata changes. Role/tenant/lifecycle and feature evidence: [selected-api-contract.md](selected-api-contract.md), [auth-api-checklist.md](auth-api-checklist.md), [api-implementation-checklist.md](api-implementation-checklist.md).

| Method | Endpoint | Authentication metadata | Required headers | Declared responses |
|---|---|---|---|---|
"@
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),$preamble+"`n"+($rows -join "`n")+"`n",(New-Object Text.UTF8Encoding($false)))
