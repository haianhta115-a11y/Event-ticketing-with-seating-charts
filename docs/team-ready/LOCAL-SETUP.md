# Windows 11: fresh teammate setup (Delivery 02 candidate)

These changes are uncommitted. A fresh `develop` clone will contain them ONLY AFTER
Delivery 02 is reviewed and merged. Do not claim today's develop already has this setup.

## Requirements and safety

.NET SDK 10 (global.json permits the latest installed stable 10.0 feature band),
Node 24 LTS/npm 11, Docker Desktop, PostgreSQL 15, Redis 7, Git, PowerShell 7
and two terminals. Locally verified SDK 10.0.303, Node 24.21.0, npm 11.19.0.
No PRODUCT-01 database, private backup, Codex files or default demo password required.

Never use a local tunnel to remote PostgreSQL; never run historical EF migrations,
DB-03B, Down migrations, database/volume deletion, reset or prune to perform setup.
The initializer needs CREATEDB on your own local cluster, not a shared server.

## Terminal 1 — clone and dependencies

After Delivery 02 is merged:

```powershell
git clone --branch develop https://github.com/minhdoan6543-gif/Event-ticketing-with-seating-charts.git
cd Event-ticketing-with-seating-charts
dotnet --version
node --version
npm --version
dotnet restore EventTicketing.slnx
dotnet build EventTicketing.slnx -c Debug --no-restore
npm ci --prefix frontend --ignore-scripts
docker info
Get-NetTCPConnection -State Listen -LocalPort 5432,6379,5009,5173 -ErrorAction SilentlyContinue
. ./scripts/local-env.ps1 -ApiUrl 'http://127.0.0.1:5009' -FrontendUrl 'http://localhost:5173'
```

The script prompts for YOUR local username/password (hidden), quotes punctuation
with Npgsql, generates a fresh `event_ticketing_test_team_<32 hex>` name and an
in-memory JWT key, sets Redis DB 15 with a per-database prefix and 10-minute holds.
It does not connect/start/create anything. Save the displayed database NAME for
subsequent starts. Do not print environment variables/connection strings or enable
transcription/shell tracing. No real credentials are committed or printed by setup.

Inspect any existing listeners first; never stop/recreate another person's services.
If your own local PostgreSQL/Redis already run, verify their identity and credentials
and SKIP Compose creation. Choose other free API/frontend ports if needed and set
matching URLs in both terminals. No protected PRODUCT-01 services should be changed.

ONLY on a fresh local machine, with unused 5432/6379 and no preexisting volumes
under this Compose project, create your local services:

```powershell
docker compose -f docker-compose.local.yml config --quiet
docker compose -f docker-compose.local.yml up -d
docker compose -f docker-compose.local.yml ps
docker compose -f docker-compose.local.yml exec -T postgres pg_isready
docker compose -f docker-compose.local.yml exec -T redis redis-cli -n 15 PING
```

Separate project-specific named volumes, loopback ports; no fixed container names.
An environment password change does not update an existing PostgreSQL volume password.

## Initialize once, explicitly

```powershell
dotnet run --project tools/EventTicketing.LocalSetup --no-build -- init --database $env:LOCAL_TEAM_DATABASE
if ($LASTEXITCODE -ne 0) { throw 'Initialization stopped; preserve target and evidence' }
dotnet run --project tools/EventTicketing.LocalSetup --no-build -- verify --database $env:LOCAL_TEAM_DATABASE
```

The initializer allows only literal 127.0.0.1:5432, exact argument/connection target
equality and `event_ticketing_test_team_<32 lowercase hex>`. It checks server identity
and target absence before CREATE, refuses EVERY existing database (even empty),
creates its own target using template0 and rechecks emptiness. Schema is generated
transactionally from the current EF model, excluding ALL HasData seed operations.
Only existing role definitions are inserted; no historical users/passwords or fake
legacy owners/coordinates. Required tables, columns/nullability, indexes, keys and
checks are verified, including sales_transitions and account_activations.

The local setup marker records model SQL hash, NOT EF migration history. No automatic
cleanup/drop on failure. App startup never creates schema/migrates/seeds. This schema
is NOT a reconciled historical migration baseline; future changes need separate review.

## Optional legitimate Organizer/Admin local fixtures

```powershell
dotnet run --project tools/EventTicketing.LocalSetup --no-build -- fixture --database $env:LOCAL_TEAM_DATABASE --email organizer-local@example.test --role Organizer
dotnet run --project tools/EventTicketing.LocalSetup --no-build -- fixture --database $env:LOCAL_TEAM_DATABASE --email admin-local@example.test --role Admin
```

Prompts for new passwords hidden, minimum 12 characters. No shared/default password.
Existing account collision refuses password/role overwrite. Uses production EF models
and PasswordHasher only on its own marked fresh local database; no public provisioning
or role-management endpoint. Buyer registers/activates through normal web flow.

## Start backend / frontend

Backend in configured terminal 1:

```powershell
dotnet run --project EventTicketing.Api --no-build --no-launch-profile
```

Terminal 2 at repository root:

```powershell
$env:VITE_API_BASE_URL='/api'
$env:VITE_API_PROXY_TARGET='http://127.0.0.1:5009'
npm --prefix frontend run dev -- --host 127.0.0.1 --port 5173 --strictPort
(Invoke-WebRequest 'http://127.0.0.1:5009/health/ready').StatusCode
```

The readiness command runs from another available terminal while Vite occupies terminal 2.
Equivalent frontend commands in Git Bash:

```bash
export VITE_API_BASE_URL='/api'
export VITE_API_PROXY_TARGET='http://127.0.0.1:5009'
npm --prefix frontend run dev -- --host 127.0.0.1 --port 5173 --strictPort
```

Open http://localhost:5173. Readiness checks SELECT 1 + Redis PING, not schema; use
the schema verifier too. On later starts dot-source local-env with
`-Database '<YOUR SAVED TEAM DB NAME>'` and matching origins; run verify, NOT init.
A new JWT key invalidates old tokens; login again. A stable key may be supplied via
private secret management before sourcing, never committed. Hold cleanup is explicitly
enabled; first session deadline stays authoritative and is not renewed by reload.

## Manual journey / quality gates

Register Buyer at `/register`; inspect YOUR ignored `EventTicketing.Api/.local-mail/*.json`
locally and open activation link. Never share/commit activation messages. Login `/login`.
Organizer/Admin: `/events`, `/events/create`, `/events/<id>` for edit/showtimes/import/
publish/sales. Buyer: Home → `/shows/<id>` → `/shows/<id>/seats` for batch hold,
countdown/reload/release. Check Buyer management 403 and foreign Organizer ownership 403.

```powershell
dotnet build EventTicketing.slnx -c Release
dotnet format EventTicketing.slnx --verify-no-changes --no-restore
dotnet run --project tests/EnvironmentSafetyChecks -c Release
node --test frontend/tests/*.test.mjs
npm --prefix frontend run lint
npm --prefix frontend run build
git diff --check
```

Backend suite: configure BATCH1A1_POSTGRES for the verified local test cluster and
BATCH1A1_REDIS for DB 15, CI_BUILD_CONFIGURATION=Release and FIX03_PLAYWRIGHT pointing
to a separately installed Playwright/Chrome test runtime. Run
`node tests/ci/run-backend-tests.mjs`; fixtures create new isolated DBs, Redis prefixes
and ephemeral browser ports, without cleanup. Required skipped/missing tests fail.

Production frontend defaults to same-origin `/api`; deployment requires reverse proxy
or approved build-time VITE_API_BASE_URL, CORS and HTTPS activation page. VITE_* is
public: never credentials. Existing DTO does not provide section/price; do not invent
them. S-07.3/S-08.3/S-12.4 remain blocked by S-15/S-16 owners' actual contracts.
