# Staging operator approval — NOT deployed

FINAL-FIX adds a disabled-by-default, same-commit quality → immutable image →
health-gated blue/green workflow. This is implementation and offline simulation,
not evidence of live staging acceptance, SMTP, rollback or a <5-minute pipeline.

## Required decisions before enabling anything

1. Team leader designates the source branch (workbook says main branch; local work
   targets develop). Do not deploy main or change branch policy implicitly.
2. Repository administrator protects develop with `Required quality gate` and a
   teammate review. Test a deliberately bad lint PR on an approved temporary branch;
   save hosted checks proving merge is blocked. Never merge that probe.
3. Provision and protect GitHub Environment `staging`, with required reviewers and
   deployment branch restrictions. Provision a trusted Linux self-hosted runner with
   label `staging`, Node24, Docker, nginx, HTTPS, GHCR pull access and least privilege.
   PR code must never execute on this runner. This batch provisions none of them.
4. Operator approves an **isolated staging database**, required schema including
   sales_transitions and safe cache partition/prefix. No Neon/protected/local
   PRODUCT-01 connections. Never run historical migrations, seeds or DB-03B.
5. Operator creates a private 0600 host env file (NOT in Git), a scoped pre-existing
   Docker network and a nginx location include file currently routing to healthy
   old containers. Register only the env-file path as `STAGING_ENV_FILE` secret.
   Database/JWT/SMTP values remain in the private file, not workflow logs.
6. Set approved `STAGING_SOURCE_BRANCH`, `STAGING_PROJECT`, `STAGING_ROUTE_FILE`,
   `STAGING_HOME_URL`. Only after all approvals, set
   `STAGING_DEPLOYMENT_APPROVED=true`. Unset/false means no image publication/deploy.
   Manual workflows require an explicit earlier ancestor `coverage_base_sha`.
   A first branch push has an all-zero GitHub `before` SHA: repository admin must
   explicitly set `COVERAGE_INITIAL_BASE_SHA` to the reviewed integration base before
   that push. Without it, CI fails closed (or run manual CI with the earlier SHA).
   The approved initial base must exist, be an ancestor and differ from the new HEAD;
   there is no fallback comparing a new commit with itself. No variable was set here.

## Failure and recovery rehearsal (separate authorization)

Candidate API/web containers are added, never old-container deletion/recreation.
Home and /health/ready must both return 200 before traffic activation; test unhealthy
images, image pull failure, activation failure and live readiness failure. Verify old
containers and Home remain healthy, and record exact timings/requests. Candidate
cleanup is intentionally not automated; inspect retained evidence before approving it.

Before activation the runner writes an exclusive private journal alongside the route
file. Any pending journal blocks another release, including a different SHA. If the
runner is interrupted/killed/power fails, a human inspects that journal and approves
`node scripts/staging-blue-green.mjs --execute --recover` with the journal's exact
GITHUB_SHA and approved existing settings. Do not run this command in FINAL-FIX.
It restores the previous route and checks live health; no database rollback is claimed.

Old containers MUST remain running. The host is responsible for compatible nginx
include syntax, route-file owner/permissions, container port/network capacity and
credentials. Local simulated tests do not prove host permissions or signal recovery.
New API cleanup is forced off during this deployment mechanism to avoid two writers;
the operator must approve a separate single-owner cleanup activation after data/schema
and old-version compatibility checks. No automatic activation is implemented.

The automated rollback covers command exceptions; a pending journal needs explicit
recovery after abrupt termination. A failed recovery is an operator incident, not a
silently successful deployment. No migrations or destructive recovery are permitted.

## Other external acceptance evidence

- SMTP: approved sandbox credentials, dedicated inbox, register, receive within SLA,
  activate once/replay/24h expiry; redact links/tokens from evidence and logs.
- Physical phone: 100/2000-seat maps, pinch/pan/tap hit accuracy, holds/releases and
  readability; browser emulation alone is insufficient for S-09.5.
- S-14.2: actual login, hold, close browser, log back in on another real device before
  the original deadline; recover only that user, never renew; test expired/401/403.
- Staging S-13: 200 requests / 100 seats, ten repetitions, exactly 100 successes and
  100 conflicts per run; verify no double/partial holds. Local runs are not staging.
- Independent review and changed JSX/C# baseline coverage are still required. The
  implemented JS gate reports unmeasured JSX explicitly; C# current coverage alone
  cannot prove non-regression against an earlier source tree.
- T-08 requires PO approval for secure private pickup instead of dev activation-link
  logging: current no-secret-log rule takes precedence, no token-log workaround.
- T-15 requires explicit enum/EF-history approval; see NATIVE-ENUM-PROPOSAL.md.
- S-07.3/S-08.3/S-12.4 remain blocked on the responsible S-15/S-16 owners.

No live staging, GitHub protection changes, credential rotation or migrations were
performed in this batch. Historical credential exposure requires owner rotation;
removing defaults does not erase Git history or revoke previously exposed secrets.
