param([string]$Filter='', [string]$ResultName='delivery-01.trx')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if($ResultName -notmatch '^[A-Za-z0-9_.-]+\.trx$'){throw 'Invalid result filename'}
$oldPostgres=$env:BATCH1A1_POSTGRES; $oldRedis=$env:BATCH1A1_REDIS
try {
  $localUser=(docker exec event_ticketing_db printenv POSTGRES_USER).Trim()
  if($LASTEXITCODE -ne 0){throw 'Local credential lookup failed'}
  $localPassword=(docker exec event_ticketing_db printenv POSTGRES_PASSWORD).Trim()
  if($LASTEXITCODE -ne 0){throw 'Local credential lookup failed'}
  $identity='SELECT current_database(),inet_server_addr(),inet_server_port();' | docker exec -i event_ticketing_db sh -lc 'PGPASSWORD="$POSTGRES_PASSWORD" PGOPTIONS="-c default_transaction_read_only=on" psql -X -A -t -h 127.0.0.1 -U "$POSTGRES_USER" -v ON_ERROR_STOP=1 -d postgres'
  if($LASTEXITCODE -ne 0 -or ($identity -join '').Trim() -ne 'postgres|127.0.0.1|5432'){throw 'Local database identity mismatch'}
  $env:BATCH1A1_POSTGRES='Host=127.0.0.1;Port=5432;Database=event_ticketing_test_delivery_config;Username="'+$localUser.Replace('"','""')+'";Password="'+$localPassword.Replace('"','""')+'";Include Error Detail=false'
  $env:BATCH1A1_REDIS='127.0.0.1:6379,defaultDatabase=15,abortConnect=true'
  $arguments=@('test',(Join-Path $repo 'EventTicketing.Tests/EventTicketing.Tests.csproj'),'--no-build','--no-restore',
    '--logger',('trx;LogFileName='+$ResultName),'--results-directory',(Join-Path $repo 'EventTicketing.Tests/TestResults/delivery'))
  if($Filter){$arguments+=@('--filter',$Filter)}
  & dotnet @arguments
  $result=$LASTEXITCODE
} finally {
  $env:BATCH1A1_POSTGRES=$oldPostgres; $env:BATCH1A1_REDIS=$oldRedis
}
exit $result
