# Dot-source this script in the backend terminal. Secrets are prompted, not printed/saved.
param([string]$Database, [string]$ApiUrl='http://127.0.0.1:5009', [string]$FrontendUrl='http://localhost:5173')
$ErrorActionPreference='Stop'
if(!$Database){$Database='event_ticketing_test_team_'+[Guid]::NewGuid().ToString('N')}
if($Database -notmatch '^event_ticketing_test_team_[a-f0-9]{32}$'){throw 'New isolated team database name required'}
foreach($address in @($ApiUrl,$FrontendUrl)) {
  $uri=[Uri]$address
  if(!$uri.IsLoopback -or $uri.Scheme -ne 'http' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/'){
    throw 'Local loopback HTTP origins only'
  }
}
if(!$env:DB_USER){$env:DB_USER=Read-Host 'Your LOCAL PostgreSQL username'}
if(!$env:DB_PASSWORD){
  $teamPassword=Read-Host 'Your LOCAL PostgreSQL password' -AsSecureString
  $env:DB_PASSWORD=[System.Net.NetworkCredential]::new('', $teamPassword).Password
}
# Npgsql safely quotes punctuation in credentials. Never interpolate passwords into commands.
$repoRoot=Split-Path $PSScriptRoot -Parent
$npgsql=Join-Path $repoRoot 'EventTicketing.Api/bin/Debug/net10.0/Npgsql.dll'
if(!(Test-Path -LiteralPath $npgsql)){throw 'Run dotnet build EventTicketing.slnx first'}
Add-Type -Path $npgsql
$teamConnection=[Npgsql.NpgsqlConnectionStringBuilder]::new()
$teamConnection.Host='127.0.0.1'; $teamConnection.Port=5432; $teamConnection.Database=$Database
$teamConnection.Username=$env:DB_USER; $teamConnection.Password=$env:DB_PASSWORD
$teamConnection.IncludeErrorDetail=$false; $teamConnection.Pooling=$false
$env:ConnectionStrings__DefaultConnection=$teamConnection.ConnectionString
$env:ASPNETCORE_ENVIRONMENT='Development'; $env:DOTNET_ENVIRONMENT='Development'
$env:ASPNETCORE_URLS=$ApiUrl
$env:Redis__ConnectionString='127.0.0.1:6379,defaultDatabase=15,abortConnect=true'
$env:Redis__KeyPrefix='team_'+$Database.Substring('event_ticketing_test_team_'.Length)+':'
if(!$env:Jwt__Key){$env:Jwt__Key=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))}
$env:SeatHolds__DurationMinutes='10'; $env:BackgroundJobs__SeatHoldCleanupEnabled='true'
$env:Email__Mode='Pickup'; $env:Email__ActivationUrl=$FrontendUrl.TrimEnd('/')+'/activate'
$env:Email__PickupDirectory=Join-Path $repoRoot 'EventTicketing.Api/.local-mail'
$env:Cors__AllowedOrigins__0=$FrontendUrl.TrimEnd('/')
# Correct ASP.NET hierarchical category override (dots belong to the category name).
[Environment]::SetEnvironmentVariable('Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command','Warning','Process')
$env:LOCAL_TEAM_DATABASE=$Database
Write-Host "Local database: $Database; API: $ApiUrl; frontend: $FrontendUrl; Redis DB 15 (isolated prefix)."
Write-Host 'No connection was made. No credentials were printed or written to disk.'
