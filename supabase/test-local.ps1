
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$psql = (Get-Command psql -ErrorAction Stop).Source
[xml]$project = Get-Content (Join-Path $repo 'Fire3D/Fire3D.API/Fire3D.API.csproj')
$secretFile = Join-Path $env:APPDATA ('Microsoft\UserSecrets\' + $project.Project.PropertyGroup.UserSecretsId + '\secrets.json')
$secrets = [IO.File]::ReadAllText($secretFile) | ConvertFrom-Json
$connection = $secrets.'ConnectionStrings:DefaultConnection'
if (-not $connection) { $connection = $secrets.ConnectionStrings.DefaultConnection }
$cs = New-Object System.Data.Common.DbConnectionStringBuilder
$cs.set_ConnectionString($connection)
$server = [string]$cs.get_Item('Host')
if ($server -notin @('localhost','127.0.0.1','::1')) { throw 'Local PostgreSQL only. Remote connection refused.' }
$port = if ($cs.ContainsKey('Port')) { [string]$cs.get_Item('Port') } else { '5432' }
$env:PGPASSWORD = [string]$cs.get_Item('Password')
$env:PGCONNECT_TIMEOUT = '5'
$baseArgs = @('-X','-w','-h',$server,'-p',$port,'-U',([string]$cs.get_Item('Username')),'-v','ON_ERROR_STOP=1')
$tag = [Guid]::NewGuid().ToString('N').Substring(0,12)
$db = 'fire3d_sb_test_' + $tag
$prefix = 'fst_' + $tag + '_'
$migrator = $prefix + 'migrator'
$roleMap = [ordered]@{}
foreach ($r in @('fet3d_payos_ledger_owner','fet3d_payos_request_executor','fet3d_payos_webhook_executor','fire3d_api','anon','authenticated','service_role')) { $roleMap[$r] = $prefix + $r }
$local = Join-Path $repo '.codex/local'
New-Item -ItemType Directory -Path $local -Force | Out-Null
$testDir = Join-Path $local ('supabase-test-' + $tag)
New-Item -ItemType Directory -Path $testDir | Out-Null
function Invoke-Sql([string]$database, [string]$sql) {
    $file = Join-Path $testDir 'command.sql'
    [IO.File]::WriteAllText($file, $sql, [Text.UTF8Encoding]::new($false))
    $output = & $psql @baseArgs -d $database -f command.sql 2>&1
    $output | Set-Content (Join-Path $testDir 'last-command.log')
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    return $output
}
function Map-Roles([string]$sql) {
    foreach ($entry in $roleMap.GetEnumerator()) { $sql = $sql.Replace($entry.Key, $entry.Value) }
    return $sql
}
$created = $false
Push-Location -LiteralPath $testDir
$ErrorActionPreference = "Continue"
try {
    Invoke-Sql 'postgres' "CREATE ROLE $migrator NOSUPERUSER CREATEROLE NOCREATEDB NOLOGIN;" | Out-Null
    Invoke-Sql 'postgres' "CREATE DATABASE $db OWNER $migrator;" | Out-Null
    $created = $true
    $anon=$roleMap['anon']; $authenticated=$roleMap['authenticated']; $service=$roleMap['service_role']; $api=$roleMap['fire3d_api']
    Invoke-Sql $db @"
CREATE ROLE $anon NOLOGIN;
CREATE ROLE $authenticated NOLOGIN;
CREATE ROLE $service NOLOGIN;
CREATE TABLE public.managed_sentinel(id integer);
GRANT SELECT ON public.managed_sentinel TO $anon;
ALTER DEFAULT PRIVILEGES FOR ROLE $migrator IN SCHEMA public GRANT ALL ON TABLES TO $anon, $authenticated, $service;
ALTER DEFAULT PRIVILEGES FOR ROLE $migrator IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO $anon, $authenticated, $service;
"@ | Out-Null
    $migration = Map-Roles ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'migrations/20260916000100_fire3d_baseline.sql')))
    $migrationFile = Join-Path $testDir 'migration.sql'
    [IO.File]::WriteAllText($migrationFile, "SET ROLE $migrator;`n" + $migration, [Text.UTF8Encoding]::new($false))
    $output = & $psql @baseArgs -d $db --single-transaction -f migration.sql 2>&1
    $output | Set-Content (Join-Path $testDir 'migration.log')
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    Write-Output 'PASS: full baseline applied as NOSUPERUSER CREATEROLE owner.'
    foreach ($name in @('fixtures.sql','database_core.sql','database_phase2.sql')) {
        $content=Map-Roles ([IO.File]::ReadAllText((Join-Path $PSScriptRoot ('tests/' + $name))))
        [IO.File]::WriteAllText((Join-Path $testDir $name), $content, [Text.UTF8Encoding]::new($false))
    }
    foreach ($name in @('database_core.sql','database_phase2.sql')) {
        $output = & $psql @baseArgs -d $db -f $name 2>&1
        $output | Set-Content (Join-Path $testDir ($name + '.log'))
        if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
        Write-Output "PASS: $name invariants."
    }
    $security=@"
BEGIN;
DO `$checks`$
DECLARE t text; r text;
BEGIN
 FOREACH r IN ARRAY ARRAY['$anon','$authenticated','$service'] LOOP
  IF has_table_privilege(r,'public.users','SELECT') OR has_table_privilege(r,'public.auth_refresh_tokens','SELECT')
    OR has_function_privilege(r,'public.apply_verified_payos_webhook(uuid,text,text,bigint,numeric,text,jsonb)','EXECUTE') THEN
   RAISE EXCEPTION 'Client role privileges leaked: %',r;
  END IF;
 END LOOP;
 IF NOT has_table_privilege('$anon','public.managed_sentinel','SELECT') THEN RAISE EXCEPTION 'Managed object modified'; END IF;
 IF has_table_privilege('$api','public.payment_transactions','INSERT') OR has_table_privilege('$api','public.users','DELETE')
    OR has_table_privilege('$api','public.audit_logs','UPDATE') THEN RAISE EXCEPTION 'Runtime role too broad'; END IF;
 IF EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE n.nspname='public' AND c.relkind='r' AND c.relname<>'managed_sentinel' AND NOT c.relrowsecurity) THEN
   RAISE EXCEPTION 'Application table missing RLS';
 END IF;
END `$checks`$;
SET LOCAL ROLE $api;
INSERT INTO public.users(id,email,password_hash,full_name,role) VALUES('99999999-9999-4999-8999-999999999999','migration@example.test','synthetic-hash','Fixture','PlatformAdmin');
UPDATE public.users SET last_login_at=now() WHERE email='migration@example.test';
INSERT INTO public.organizations(name,slug) VALUES('Migration test','migration-test');
INSERT INTO public.auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at)
VALUES(gen_random_uuid(),'99999999-9999-4999-8999-999999999999',gen_random_uuid(),repeat('a',64),now(),now()+interval '7 days');
UPDATE public.auth_refresh_tokens SET revoked_at=now();
INSERT INTO public.audit_logs(user_id,actor_type,action,target_entity,target_id)
VALUES('99999999-9999-4999-8999-999999999999','User','Login','users','99999999-9999-4999-8999-999999999999');
DO `$runtime`$ BEGIN
 IF (SELECT count(*) FROM public.users WHERE email='migration@example.test')<>1 THEN RAISE EXCEPTION 'Runtime RLS blocked auth'; END IF;
END `$runtime`$;
RESET ROLE;
ROLLBACK;
"@
    Invoke-Sql $db $security | Out-Null
    Write-Output 'PASS: client denial, RLS, managed-object preservation and restricted runtime auth writes (rolled back).'
    $again = & $psql @baseArgs -d $db --single-transaction -f migration.sql 2>&1
    if ($LASTEXITCODE -eq 0 -or ($again -join "`n") -notmatch 'Fresh-project baseline only') { throw 'Repeat apply did not fail at preflight.' }
    Write-Output 'PASS: repeat baseline safely rejected at preflight.'
    Invoke-Sql $db (Map-Roles ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'verify.sql')))) | Out-Null
    Write-Output 'PASS: post-migration verification script.'
    Write-Output "Evidence directory: $testDir"
} finally {
    # Only generated names from this run may be deleted. Never target the configured application DB.
    if ($db -notmatch '^fire3d_sb_test_[a-f0-9]{12}$' -or $migrator -notmatch '^fst_[a-f0-9]{12}_migrator$') { throw 'Unsafe cleanup name.' }
    if ($created) { Invoke-Sql 'postgres' "DROP DATABASE $db;" | Out-Null }
    $cleanup = @($roleMap.Values) + @($migrator)
    foreach ($r in $cleanup) {
        if (-not $r.StartsWith($prefix)) { throw 'Unsafe role cleanup.' }
        Invoke-Sql 'postgres' "DROP ROLE IF EXISTS $r;" | Out-Null
    }
    $env:PGPASSWORD=$null; $secrets=$null; $cs.Clear()
    Pop-Location
}