# Controller identity qualification checkpoint

Source: Q163–175, Q189–191. The measured module is the in-memory
`ChunkMaestro.ControllerIdentity` library, with all three source files included.
This report does not qualify native credential storage, owner reauthentication,
UI initialization or the complete issue #12.

The [2026-11-23 owner testing policy](../product/requirements-source.md#owner-testing-policy--2026-11-23)
now treats coverage and mutation scores as diagnostics throughout the application.
The earlier numeric qualification outcomes below remain historical observations;
meaningful behavioral/security tests and independent QA govern acceptance.

The original frozen `f21d684` measurement retained 34 passing identities before
and after collection. It measured 205/210 lines (97.619%) and 73/86 branches
(84.884%). Branch coverage failed the 85% floor; it was not rounded up. Its one
Stryker invocation rejected four CLI-only options placed in JSON before discovery
or instrumentation. No initial mutation tests, terminal report or score existed.
The immutable report and 2,255 verified artifact hashes remain under
`/tmp/controller-identity-f21-evidence/`.

The lead added real validity-boundary contracts: generation refuses a NotBefore
earlier than 1950 but accepts the earliest valid instant; a correctly signed
certificate with an overflowing required five-year lifetime refuses as a
cryptographic error. Production implementation is unchanged. No clock, trust
store or native credential is modified by these tests.

The test project now uses the repository's existing pinned coverlet.collector,
with its direct lock entry and positive OpenCover module include. No package
version or central dependency changed, and no source exclusion was introduced.
The ordinary bounded CI profile step retains its 150-second command/three-minute
step limits and now retains raw coverage alongside its TRX.

Local macOS arm64 results: 36 profile/envelope cases pass, zero failed/skipped.
Coverage is 208/210 lines (99.048%) and 74/86 branches (86.047%), keyed by unique
source lines and method/offset/path branch identities. Full repository verify
passes 490 tests: desktop 28, profile 36, credentials 74, persistence 352. Locked
restore, Release build, syntax/style, generated-inclusive format and actionlint
pass. Raw proof is under `.artifacts/identity-12-integration/profile-boundaries/`,
with the point union in `profile-boundary-coverage.json` and root commands in
`profile-boundary-verify.log`. This is Mac-only coverage, not a combined-platform
or final independent acceptance claim.

The four unsupported JSON keys (`log-to-file`, `diag`, `skip-version-check`,
`output`) were moved to the documented CLI argument list in an artifact-local
template. Remaining keys match the allowed-key list reported by pinned Stryker
5.0.0. No framework, source selection, tests or thresholds were changed to bypass
the failure. The corrected template has not yet established a mutation score.
If a diagnostic remeasurement is useful, it may use that concrete correction, retain
all identities/statuses, credit actual Killed only and stop within its fixed
180-second whole-command budget. Missing initial identities or terminal report
remains UNRESOLVED, with no automatic retry or budget enlargement.

Windows/Intel reporting and final independent QA remain open. Native mutation
isolation is not established; direct native lifecycle, denial, collision,
recovery and failure-injection tests provide narrower alternative verification.
No mutation score or full native mutation proof is claimed or required for a
numeric gate. Historical bounded functional/security preflights remain immutable
and ineligible for final aggregation. Rollback removes only these test/collector
changes; it does not reset native identities, metadata, backups or schema.
