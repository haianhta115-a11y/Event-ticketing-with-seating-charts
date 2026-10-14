# Reproducible CI coverage baseline and Redis fixture startup

## Existing measured coverage baseline (not a new/fabricated baseline)

`b41f67def33d78356c05b8ab9d3acb3b3f82aeaf` is the develop merge of Delivery 02
(PR24), and the direct parent of reviewed delivery commit
`92c9d21cf9067c015535997ca0f7d9b12c823f15`. It predates all FINAL-FIX delivery
changes and contains 95 passing frontend tests and comparable executable JS helpers.
Export that exact commit's frontend, install its lockfile, run Node24 tests with
LCOV line/function reporting, then independently measure current code and compare.
No coverage percentages are invented or copied from the current commit.

Historical measured line/function pairs include axiosClient 95.7447%/75%,
session 100%/100%, holdRecovery 99.3789%/92.3077%, management and seatPresentation
100%/100%. The runner remeasures both trees every time; these figures document
provenance, not hardcoded thresholds. New modules retain the existing 90% floor.
JSX and baseline C# coverage are outside this gate and remain a DoD evidence gap.

`tests/ci/coverage-baseline.json` records that real earlier SHA and methodology.
For an all-zero initial-push `COVERAGE_BASE_SHA`, the runner uses an explicitly
configured repository `COVERAGE_INITIAL_BASE_SHA` if nonempty, otherwise this
version-controlled reviewed metadata. Missing/malformed metadata or a bad override
fails. No arbitrary parent/HEAD inference or coverage disablement is permitted.
Normal pushes/PR/manual runs keep their explicit event/input baseline; an absent
requested baseline still fails. Every selected SHA must exist, be an ancestor and
differ from HEAD. Current code must actually meet measured coverage, or CI fails.

Replacing this baseline later requires review and actual measurement of that
earlier code. A repository-admin override is optional, not required for this known
initial baseline; this change does not mutate repository variables. If no
comparable earlier measurement can be established, stop and obtain user approval
for a separately measured first-baseline procedure; never use HEAD as its baseline.

## Redis failure evidence and scoped correction

PR25 CI failed in concurrent registration while DI lazily called production
`ConnectionMultiplexer.Connect` from a request, not during fixture initialization.
Redis logged readiness about 90 seconds earlier. The prior probe connection was
disposed, so its readiness did not prove the later request-owned connection ready.
This establishes a cold connection/lifecycle window during an 8-request burst;
the logs do not prove packet loss, OOM, exact thread starvation or configuration
mismatch. Do not claim one of those unmeasured causes as established.

Test fixtures now open and PING **real Redis DB15 asynchronously before creating
their new PostgreSQL target**, retain one multiplexer for that fixture and inject
that ready instance into its API hosts. The fixture owns disposal; disposing one
host cannot close Redis for another host/restart of the same fixture. Separate
fixtures retain separate multiplexers and per-database key prefixes; no flush.

Only connection startup is serialized across fixtures, with a 15-second queue
timeout. At most three startup attempts retry only Redis connection/timeout
exceptions, with 100/200ms delays, connectRetry=0 and 2-second connect/async-command
timeouts. Each failed probe disposes its connection. Queue/connection/probe failure
still stops the test before application requests/DB creation; there is no retry of
HTTP registration, business operations, assertions or entire tests to hide errors.
These are bounded attempts/configured timeouts, not a hard scheduler/wall-clock SLA.

Production Program.cs and abortConnect fail-closed behavior are unchanged. Tests
require abortConnect=true; they do not substitute a fake Redis implementation.
Unit-only delegate fault injection checks gate/retry limits, while integration
tests use actual Redis and PostgreSQL, preserving the original eight concurrent
registration requests and one-account/role/activation/email assertions.

Windows local repetitions cannot replicate the exact GitHub Ubuntu24.04 runner
contention. Hosted push/PR checks must be rerun after a separately authorized normal
push; local green results do not claim that the hosted failure is already cleared.
