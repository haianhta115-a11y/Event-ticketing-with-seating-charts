# Future Neon compatibility — preparation only

No Neon connection, query, restore, migration or deployment performed. Do not run
LocalSetup, DB-03B, EnsureCreated or historical EF chain against shared databases.
LocalSetup rejects remote hosts and EVERY existing target before writes.

## Configurable deployment contract

ConnectionStrings__DefaultConnection, Redis__ConnectionString/KeyPrefix, Jwt__Key,
Cors__AllowedOrigins__0 (subsequent indexes), email credentials/activation URL and
frontend destination are configurable. Development/Testing reject remote dependencies;
approved deployment environments keep Npgsql remote support. Never tunnel a remote
DB into loopback or change environment simply to bypass the guard.

Future leader-approved secrets belong in secret management, never Git or VITE_*.
Neon uses PostgreSQL connectivity. Review approved pooling and TLS; prefer Npgsql
SSL Mode=VerifyFull with certificate/hostname validation, never disable validation.
Use approved direct/pooled endpoints appropriately; schema/session-level tools need
separate direct-connect review, not implicit migration approval.

Primary documentation: [Npgsql SSL modes](https://www.npgsql.org/doc/api/Npgsql.SslMode.html),
[Neon secure connections](https://neon.com/docs/connect/connect-securely),
[Neon connection guidance](https://neon.com/docs/get-started/connect-neon).

## Exact runtime schema areas to reconcile

- Users/Roles/UserRoles: unique email, password-hash capacity, legitimate role FKs.
- account_activations: unique TokenHash and UserId, expiry/used timestamps and user FK.
- events: DRAFT/PUBLISHED/ENDED, real owner FK, UTC timestamps. API-created events use
  authenticated owner; legacy NULL owners must not be fabricated.
- showtimes: event FK, UTC start/end, DRAFT/ONSALE/CLOSED, status/start index.
- seat_categories: showtime FK, normalized name uniqueness per showtime and alternate
  `(showtime_id,id)` key.
- seats: actual showtime/category/row/number/x/y; AVAILABLE/HELD/SOLD; owner/deadline
  for HELD, unique showtime/row/number, expiry index and composite category FK.
- sales_transitions: audit ID, event/showtime/actor references, from/to status, UTC
  timestamp, restrictive FKs and event/time index. Required for sales and public
  cache revision; absent schema yields SALES_SCHEMA_NOT_READY.

Exact definitions: current AppDbContext/entities. Fresh-local generator is NOT an
approved legacy migration. docs/fix01/sales-audit-schema.sql remains definition-only.

Known blockers: legacy performance_id NOT NULL in seats/categories; unresolved
mappings 337/385; missing historical coordinates; NULL owners; six historical EF
history rows and model/snapshot drift. Never fabricate identities, mark migrations
applied without reviewed effects, or execute destructive Down/renames. LocalSetup
does not create/edit __EFMigrationsHistory. Shared legacy schemas may also lack
activation, normalized association, status constraints and audit definitions.

## Leader approval checklist

1. Approve a separate Neon DEVELOPMENT branch/database, intended environment,
   roles/credentials and prohibition of changes to shared production data.
2. Independently verify backup/restore and recovery for that approved branch.
3. Read-only inventory schema/data/history against current exact runtime contract;
   resolve real mappings/geometry/owners with responsible people.
4. Review each schema/sequence/writer/history change, locks, downtime, permissions
   and rollback. DB-03B is not executed or implicitly approved by this batch.
5. Rehearse and test auth/ownership/import/audit/feed/concurrency/cleanup ONLY on
   the independently approved isolated Neon development branch.
6. Approve separate shared rollout, CI secrets, SMTP/HTTPS activation, public API,
   CORS/reverse proxy and Redis namespace.
7. S-07.3/S-08.3/S-12.4 stay Blocked pending S-15/S-16 owners' contracts.
