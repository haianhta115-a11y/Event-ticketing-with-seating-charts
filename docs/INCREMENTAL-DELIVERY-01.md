# Delivery 01: verified core, code review only

## Scope and quality gates

One dependency-consistent bundle covers local dependency guards (partial S-01),
S-02 auth/authorization, S-03 registration/activation, S-04 events/showtimes,
S-05/S-06 preview/import/validation, R-01 reservation safety, F-01 multipart
transport, Home visibility and the legacy fail-closed boundary. Existing seat-map
consumers stay matched to the Showtime API; this is not S-07–S-14 acceptance.

The GIT-02 manifest has 113 code/test/config paths (112 copies and one deletion),
this guide and the manifest itself: 115 delivery paths. Its base is
c2d1667dfac006ca3f4cb2eb9ce847477f3aef05. Do not reuse the GIT-01 source projector:
it would overwrite quality fixes with the older original source.

Verified on the detached candidate based on that develop commit:
backend 256 passed / 0 failed / 0 skipped; frontend 39 / 0 / 0;
real browser smoke 12 / 0 / 0; environment safety 16 / 0.
Backend build: 0 errors / 0 warnings; frontend production build succeeds;
dotnet format, ESLint and diff check pass. npm audit reports zero vulnerabilities.
The source-map-js transitive patch is 1.2.1 -> 1.2.2, with no major upgrades.
The browser harness is local evidence only, excluded from this delivery.
The hold countdown has three portable regression tests; the browser rerun also
checks event-ID navigation clears stale preview before confirmed import.

## Runtime and deployment prerequisites

Use .NET 10, a Node version compatible with the locked Vite version, PostgreSQL 15
and Redis 7. Root API/tests and frontend are authoritative, not duplicate projects.
Provide ConnectionStrings__DefaultConnection, Redis__ConnectionString and a
secret-managed Jwt__Key (or JWT_KEY), minimum 32 bytes.
DB_USER/DB_PASSWORD resolve local Compose/JSON placeholders; never commit values.

Development and Testing reject remote PostgreSQL/Redis. Testing only permits
event_ticketing_test_* and Redis DB15. BATCH1A1_POSTGRES/BATCH1A1_REDIS configure
fixtures; the local role needs CREATE DATABASE. tests/run-local-tests.ps1 is an
explicit local-Docker runner, reads credentials without displaying them, uses
only isolated fixtures and restores process environment. It does not migrate
existing databases. Do not use protected databases as fixtures.

Startup has no automatic migration, EnsureCreated or demo/account seeder.
IMPORTANT: outside Development/Testing, seat-hold cleanup is enabled by default
and can write a database. Set BackgroundJobs__SeatHoldCleanupEnabled=false during
rehearsal/rollout until the target schema and data are approved. Readiness is NOT
a schema compatibility proof. No production or shared database was used here.

Testing uses fake email. Development Pickup writes ignored private .local-mail.
SMTP deployment needs Email__Mode, Email__ActivationUrl, Email__Host,
Email__Port, Email__From, Email__Username and Email__Password with HTTPS activation
URL. Frontend still defaults to localhost:5009; staging destination/CORS require
a separate configuration decision.
CI currently triggers main only, has no isolated PostgreSQL/Redis fixture setup,
and deployment is independent. The leader must wire mandatory develop PR checks
or explicitly accept manual evidence before merge. Do not auto-deploy this PR.
Rotate historically exposed credentials through the responsible secret owner;
a snapshot PR does not remove secrets already reachable from develop.

## Migration gate: source artifacts, never execute the historical chain

Included definitions/designers:
FixArchitecture_LinkSeatsToShowtime, AddSeatCoordinates, AddAccountActivations,
AddEventOwnerForeignKey, and AppDbContextModelSnapshot.

AddAccountActivations and AddEventOwnerForeignKey are directly instantiated by
unit tests, so their class definitions are needed for that test compilation.
The architecture/coordinate definitions and all designers/snapshot are EF source
history, not required by ordinary application compilation and NOT a rollout plan.
They remain unchanged rather than silently fabricating a reconciled history.

NEVER run the historical chain (including dotnet ef database update) against an
existing team database. Architecture drops performances/renames performance_id;
coordinates adds NOT NULL/default zero but active geometry is nullable without
defaults, and the snapshot still has the old defaults. Activation/owner objects
may already exist through additive scripts. Do not invent mappings, geometry,
owners or __EFMigrationsHistory rows.

Legacy performance_id NOT NULL constraints still block modern seat/category
writes. DB-03B, verified backup availability, real mappings/geometry/NULL owners,
schema fingerprint and EF baseline reconciliation require separate approval.
This PR is code integration only, not authorization to deploy or execute DB-03B.

## Manual next steps, only after approval

1. Keep the original backup/pre-s06-checkpoint checkout untouched. Use the retained
   GIT-02 candidate worktree (detached at the manifest base) as the delivery source.
   Its fixed files, not the old source checkout, are authoritative.
2. In that candidate, fetch origin develop. Stop if origin/develop differs from
   the manifest base. Recompare and retest before proceeding if develop moved.
3. Verify every manifest SHA-256; deletion must still be absent. Confirm the
   working-tree path set is exactly the manifest (no outputs/private artifacts).
   The manifest self entry intentionally has no recursive hash.
4. Only with approval, create codex/delivery-01-verified-core IN THE CANDIDATE
   with git switch -c. This bases the new branch on develop, not backup history.
   Do not merge or cherry-pick the 38 backup-only commits.
5. Recheck build/format/lint/tests and security on the delivery branch. Commands:
   dotnet build EventTicketing.slnx;
   dotnet format EventTicketing.slnx --verify-no-changes --no-restore;
   ./tests/run-local-tests.ps1 (local Docker fixtures only);
   dotnet run --project tests/EnvironmentSafetyChecks/EnvironmentSafetyChecks.csproj;
   in frontend: npm ci --ignore-scripts, npm run lint,
   node --test tests/*.test.mjs, npm run build; git diff --check.
   Check each exit code. A changed UI requires the real browser smoke to rerun;
   do not label the excluded local harness as an available portable CI suite.
6. Stage only the 115 manifest paths, never git add .; inspect staged names,
   staged diff, secrets and diff --cached --check. Example:
   $files=(Get-Content docs/git02/candidate-files.json -Raw | ConvertFrom-Json).files.file
   git add -- $files
7. After explicit approval, commit with:
   feat(core): deliver verified auth and seat-map workflows
   Push only codex/delivery-01-verified-core and open a PR with base develop.
   The leader reviews/merges. Do not push develop/main or auto-deploy.
8. PR description must state tested counts, API /performances -> /showtimes
   compatibility decision, source-only migrations, unresolved legacy/SMTP/CI
   prerequisites, secret rotation responsibility and no S-07 implementation.

No branch creation, staging, commit, push, merge, rebase or migration was performed
during GIT-02. Retain the candidate until the reviewed delivery is safely handed off.
