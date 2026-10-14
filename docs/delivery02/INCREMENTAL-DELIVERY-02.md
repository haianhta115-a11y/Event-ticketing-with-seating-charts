# Delivery 02 — verified code-only candidate

## Scope and source

Base develop `32b0416d1e1052241ba1e78c8a36975b324b705a` verified remotely with read-only ls-remote; source branch `codex/fix-01-s07`, HEAD equals base, no local-ahead commits. Do not import the backup branch/history or modify either existing other worktree.

One cohesive code commit is proposed for FIX01–FIX06: sales switching/audit and hold locks; server session recovery/deadline; atomic batch; expiry cleanup; routed release/map/feed/config; matching tests and CI. Splitting interfaces/models/consumers can break this closure.

Suggested commit: `feat(core): deliver verified sales and seat-hold workflows with CI gates`.
No commit/push/PR/deployment is authorized by this document.

`candidate-files.json` pins83paths (34production,21tests,13CI,8setup,7documentation), raw SHA256, Git clean-filter blob identity and modes. Includes UI-FIX02/UI-FIX03 and TEAM-READY01 source/setup dependencies. Manifest self-entry intentionally has no recursive hash. Copies must match both hashes/modes; changes require re-verification and manifest refresh. No deletions.

Excluded and retained locally: FIX01–FIX06 machine-specific audit reports; original101file work; prior delivery worktree; backups/dumps/VHDX/.env/private config/mail, TRX/logs/bin/obj/node_modules/dist/artifacts, Excel workbook and external coverage report. No blanket git add.

## Required quality checks

Reusable CI covers push and PR to main/develop/staging; backend and frontend are independent required jobs. Aggregate **Required quality gate** runs always but succeeds ONLY when both job results are success; failure/cancellation/skipped/missing results block it.
Delivery artifact workflow calls the same-commit CI definition, packages only after success, and defaults GHCR publicationOFF. Registry publication is explicit workflow_dispatch opt-in restricted to main/staging and depends on both successful quality and packaging. This is NOT a staging/database deployment.

CI uses ephemeral PostgreSQL15/Redis7 services bound loopback; PostgreSQL trust authentication is only for these disposable CI services (no deployment password), all fixtures are new event_ticketing_test_* and Redis15. No persistent volume mounts, shared secrets, remote DB, migrations, application seed or cleanup scripts against existing databases.
.NET10 and Node24; actions pinned to verified official SHAs. YAML validator yaml2.9.1 is a separate locked dev-only tests/ci package, no frontend production dependency upgrades.
Browser runtime Playwright1.62.1/Chrome installed only on ephemeral GitHub runner. Do not execute its global browser install locally to replace a user's Chrome. Local tests may point FIX03_PLAYWRIGHT to an installed trusted module. Browser cases now use ephemeral Kestrel/Vite ports and per-fixture Redis prefixes; latency also uses a dynamic Kestrel port. They do not restart an existing local demo.

Local commands, only on approved isolated test infrastructure:

```text
dotnet build EventTicketing.slnx -c Release
dotnet format EventTicketing.slnx --verify-no-changes --no-restore
./tests/run-local-tests.ps1 -CiSuite -Configuration Release
dotnet run --project tests/EnvironmentSafetyChecks -c Release
npm ci --prefix tests/ci --ignore-scripts
npm run check --prefix tests/ci
npm test --prefix tests/ci
```

Frontend: npm ci --ignore-scripts, npm run lint, node --test tests/*.test.mjs, npm run build. CI backend runner enforces327API/unit minimum, both real browser cases, latency, zero failures/skips/aborts and nonempty results. It stops at first failing phase. TRX outputs are ignored and must not be committed.
Workflow source/DAG/local failure injection is verified; no GitHub-hosted workflow run or real staging transport/HTTP200/rollback is claimed. The repository administrator must configure/check required branch protection for the exact gate name after its first approved PR run; YAML cannot by itself prevent a privileged manual merge.

## Database and deployment hard guard

**CODE-ONLY. Never run the historical EF migration chain or DB03B against existing databases. No migration/history edits in this candidate.**

S07 adds `public.sales_transitions` to the active EF model. Publish/sales transitions write audit in the same transaction; public feed reads its maxId revision. Missing table blocks transitions/feed with503. Therefore approval of code does NOT imply a database without the table is deployable.

`docs/fix01/sales-audit-schema.sql` is an unexecuted definition, SHA256 `A69D610B46B907E7A1CE07D6F45F4F37BAE2FF2A7937A03051F63744C662D808`. Needs bigintidentityPK/Id, EventIdFK, nullableShowtimeIdFK, ActorUserIdFK to existingUsers, varchar20 statuses, timestamptz clock, restrictive deletes and EventId/OccurredAt index. Review actual naming/ownership/privileges and EF history first. EF conventions also generate ActorUserId/ShowtimeId FK indexes in fresh test schemas; the definition lists only the explicit composite index, so index parity must be reviewed separately. Do not silently reconcile by editing history or applying this SQL.

Required existing schema: mapped showtime keys, compatible seat/category uniqueness/FKs, nullable geometry, hold status/owner/deadline columns, users/roles/activation and genuine event ownership. Legacy performance_id NOT NULL, unresolved337/385 mappings, unknown coordinates, NULL owners and EF snapshot/history mismatch remain separate deployment blockers. No fabricated mappings/owners/coordinates.
Startup does not migrate/seed. Hold cleanup is disabled by default Development/Testing but enabled outside them; set `BackgroundJobs__SeatHoldCleanupEnabled=false` until schema/data/rollout is separately approved. It now drains immediately on startup when enabled.
`SeatHolds__DurationMinutes` defaults10, validated1..1440; existing deadlines never extend when configuration changes.

Staging prerequisites remain unprovided: approved host/transport/deployment environment, rollback mechanism, real Home URL/health check, frontend API destination/CORS (now configurable, default same-origin /api), secret-managed DB/Redis/JWT settings, SMTP/HTTPS activation URL and trusted proxy rules. No SSH host, deployment endpoint, credential or environment has been invented. Fresh-local-only setup and future Neon review are documented in docs/team-ready; this is not deployment or legacy schema approval.
Current Dockerfile packages the API, not the React Home server; packaging artifacts is not proof of Home HTTP200. Docker build context denies unrelated repository/private/runtime files.
Historical credentials reachable from develop remain responsible-owner rotation/revocation work; source delivery does not erase history.

Ownership blockers unchanged: S07.3 and S12.4 await actual S16pending-order contract, S08.3 awaits S15pricing. Do not implement these members' stories or treat SOLD/checkout placeholders as proof.

## Exact Git actions requiring separate approval

1. Read current remote develop SHA again; STOP/rebase-plan review if it differs (do not rebase original workspace). Verify manifest content/modes with `node tests/ci/delivery-manifest.mjs --verify`.
2. Only after approval, create a NEW delivery worktree/branch `codex/delivery-02-verified-core` based on the verified develop base. Do not switch/reset/stash either original or current feature branch. Copy only manifest paths with exact content/modes.
3. Review added/modified/deleted paths, code-only warnings and secrets; stage ONLY the explicit83allowlisted paths. Verify staged hash/modes and diff check. Rerun gates in exact delivery worktree before commit.
4. Only after local commit approval, create ONE cohesive commit with the suggested message; expected parent equals manifest base. Record SHA/path counts. Do not import backup-only commits or claim blocked criteria done.
5. Pushing only the delivery branch requires separate authorization and normal non-force push; PR creation into develop requires separate approval; attach the created PR if authorized. No direct develop/main push, merge, deploy or database changes.

This batch stops before all these Git write operations. Shared/staging deployment and migration approval remain separate from code integration approval.
