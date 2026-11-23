# In-memory controller root candidate, profile v1

Uncommitted issue 12 primitive on frozen parent
`5274a128ce7df34529ee32cf47d5dd23da168990`; this is implementation evidence,
not independent QA, issue acceptance, Phase 1 completion, release approval or
completion of Q171. The lead integrates projects on its later issue 12 branch.
No solution/shared files, existing projects, dependencies, database, OS store,
trust settings, host, credential or user data were changed by this patch.

Traceability: Q5–6/Q14 scope; Q145/Q149 direct trusted core; Q153 no storage
change; Q163/Q175 private-material boundary; Q165–168 future owner authorization;
Q171 application-only CA proposal; Q189–193 testing and remaining safety gates.
The primitive does not authenticate an OS owner or grant authority to a fleet.

## Corrected invariant and authority

Original research remains unchanged at
`/private/tmp/chunkmaestro-issue12-research-5274a128-20261123/REPORT.md`, SHA256
`22b8cd94af06bcfd26a75b959ac1dc49a4850a12c222c16aedc090246e5915fa`.
All five manifest entries matched. Independent design report
`/private/tmp/chunkmaestro-issue12-security-design-5274a128-20261123/REPORT.md`
remains **FAIL D1**, against research wording, not a frozen implementation.
The lead adopted this narrow corrected contract during implementation:

**Always validate the complete profile and verify the exact stored root's
self-signature over original encoded TBSCertificate with its own P-256 key and
SHA256.** Both signature algorithm identifiers must be the supported identical
ECDSA-with-SHA256 identifier, with absent parameters. Reject malformed signature
DER, trailing data and unsupported encodings. Key possession is separate and
cannot replace certificate self-signature verification. No chain builder is used.
Future optional chain checks must remain offline/application-scoped and cannot
replace this invariant. The named signature-only corruption regression proves a
still-matching SPKI and independently successful possession proof, then verifies
that both profile validation and internal reopen refuse the damaged certificate.
This addresses the lead's contract; it does not change the review's FAIL verdict.

## Candidate encoding and time policy

All parameters here are engineering proposals pending independent security review:

- X.509 v3, fixed subject/issuer `CN=ChunkMaestro Fleet Controller CA`.
- ECDSA named P-256, SHA256, exact canonical SPKI; signature is DER ECDSA.
- Positive 16-byte encoded INTEGER serial: random bytes with high bit cleared,
  rejection of a zero first octet to retain 16-byte minimal positive encoding.
  No issuer-wide serial uniqueness claim or issuance service exists.
- Exactly four extensions in fixed order: critical CA=true/pathLen=0;
  critical KeyCertSign|CrlSign; noncritical BCL SKI; noncritical SAN.
  SKI's conventional identifier is distinct from SHA256 identity hashes.
- Exactly one URI GeneralName, byte-exact canonical ASCII:
  `https://identity.chunkmaestro.invalid/v1/fleets/{lower-D-guid}/controllers/{lower-D-guid}`.
  Both GUIDs must be nonempty. No resolution, endpoint claim, EKU, AIA or CDP.
  Existing database uppercase-D GUID conventions are untouched.
- TimeProvider captured once, converted to UTC, truncated to whole seconds;
  NotBefore=captured−5 minutes, NotAfter=captured.AddYears(5).
  The validator checks the exact arithmetic and canonical RFC5280 time encoding,
  including 2050 GeneralizedTime transition and leap-day AddYears behavior.
  It enforces `NotBefore <= now < NotAfter`, without another grace interval.
  Unrepresentable issuance bounds throw before key allocation; pre-1950 bounds
  are unsupported. This five-year/five-minute policy is not an owner decision.
- SHA256 over complete exact certificate DER and exact SPKI DER are required
  caller-supplied bindings. Profile version is internal constant 1; no database
  version/state schema is proposed. Certificate input is capped at 4096 bytes.

ASN.1 parsing uses DER mode, consumes complete structures and compares exact
encoded extensions, subject, issuer and canonical reexported SPKI. This rejects
unknown/duplicate/missing/reordered extensions and any extra SAN names or alternate
URI spelling. It intentionally accepts a narrower canonical encoding than general
X.509. Signature verification uses the original TBS bytes, never reconstructed TBS.

Primary references: [RFC5280](https://www.rfc-editor.org/rfc/rfc5280.html),
[.NET10 CertificateRequest.Create](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.certificaterequest.create?view=net-10.0),
[DER reader](https://learn.microsoft.com/en-us/dotnet/api/system.formats.asn1.asnreader?view=net-10.0),
[ECDsa.VerifyData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdsa.verifydata?view=net-10.0).
These describe primitives/encoding, not approval of proposed policy constants.

## API and private lifetime

The assembly exports no public types. `RootProfile` and sealed `RootMaterial` are
internal; only the owned test assembly has friend access. There is no arbitrary
signing function, private-key DTO, certificate issuance endpoint, network surface,
InitializeAsync, persistence service or successful initialization placeholder.
`GetPublicCertificate` returns a fresh nonsecret DER copy.

`Generate` owns its ephemeral ECDsa provider and disposes it on failure or via
idempotent Dispose. `Reopen` borrows a trusted-core PKCS8 input span, imports it
into a new owned provider, verifies complete consumption and byte-exact canonical
PKCS8 reexport, and zeroes that transient reexport in finally. It rejects BER and
trailing private bytes. The caller must zero its own input; nothing exports a
newly generated private key. Vault preparation/export remains future integration.
Returned material must remain within an authorized operation's scope and be
explicitly disposed. Operations and Dispose are single-owner and must not race;
this is not a concurrent signer/session cache.

`ValidatePossession` first revalidates certificate/profile/bindings/time, then
signs only an internally constructed 160-byte challenge: fixed ASCII domain
`ChunkMaestro.RootPossession.v1` with NUL/zero padding through offset 31, fleet
GUID bytes at 32, controller GUID bytes at 48, certificate SHA256 at 64, SPKI
SHA256 at 96, fresh 32-byte nonce at 128. Signature and challenge never escape and
are zeroed in finally. The owned key lives only until material disposal. There is
no attempt/vault locator binding until the lead defines the integration contract.

Tests use ephemeral synthetic providers only. Owned PKCS8 fixture copies are
zeroed in finally and checked for zeros; no private bytes enter logs or artifacts.
Provider/native temporaries, JIT/GC copies and process memory cannot be proved
completely erased. Disposing a provider releases its resources; tests do not
claim to inspect or prove provider-internal key erasure. Native store protection,
nonexportability, owner reauthentication and compromised-user resistance are not
established by these primitives.

## Executed validation and retained failures

Host: macOS 27.2.1 (26B131), arm64; exact SDK 10.0.404; Python 3.13.18.
Initial checkout status was empty, isolated worktree Git directory verified;
configured signing stayed `commit.gpgsign=true`, `gpg.format=ssh`.

Evidence directory: `/private/tmp/chunkmaestro-ca-profile-evidence`.
`command-*.json` records argv, cwd, selected environment, time and exit;
paired `.log` files retain raw combined output. `tests-d1-final` contains the
33-test TRX result before final whitespace-only formatting; final delivery TRX
is in `tests-delivery`. `SHA256SUMS.json` binds evidence files and `patch.diff`
contains the uncommitted owned patch. Initial reads/connector/web responses are
in conversation history; command logs do not pretend to cover every initial read.

Executed: forced owned locked restore, owned Release build with zero warnings or
errors, owned tests, project-scoped generated-inclusive formatter checks, root
solution locked restore/Release build, repository Roslyn syntax guard, specification
check (all 205 sources), and 21 existing Python build/QA regressions.
Root `build.py verify` exited 1 at the deliberate stale-solution-membership guard:
new projects are not integrated because this task forbids editing the solution.
Direct root solution build covers only its existing project membership; the new
projects were built/tested separately. No existing product suites were executed.

First test-source build failed CS1061 from an unavailable SAN URI enumeration
method; corrected to direct DER decoding. A subsequent command against the older
empty test binary exited 0 with zero tests: **not test evidence**. Real suites
subsequently executed 22, then 32, then 33 tests. A later format check exited 2
for missing cast whitespace in the D1 test; the formatter corrected it. All first
failures and the zero-test TRX remain retained; no warnings/analyzers were disabled.

Current tests exercise real generation/signature/profile, serial/SAN encoding,
private-key mismatch and reopen, strict time boundaries/overflow/calendar cases,
metadata substitution, signed unsupported profile/curve/algorithm fixtures,
malformed/trailing encodings, duplicate/extra extensions, inner/outer algorithm
parameters, signature-only corruption despite valid possession, private BER and
trailing key refusal, and disposal. Test assertions never print private bytes.

## Integration and acceptance still pending

Coverage has not been measured: Q190 >=90% line, >=85% branch and applicable >=80%
safety-module mutation obligations remain pending. No large mutation campaign was
run. Retired storage mutation/manual receipts stay retired; they waive nothing
for this new safety module. Fuzzing, cross-platform/provider validation and fresh
independent functional/security/UX role review remain required.

No native Windows/Intel, installer, desktop interaction, OS-vault denial/missing/
deleted-key, process-crash, cross-store recovery, DB migration, scope authorization,
credential secrecy across real DB/backups/logs, enrollment, rotation, revocation,
trust migration or agent integration evidence is provided. No native acceptance
or full issue 12 service exists. The lead must integrate issue 11 and authorized
session/attempt/metadata contracts, add actual solution/discovery/CI membership,
plan safety evidence, and obtain required independent verdicts. Rollback of this
isolated patch is removal of the three owned paths; there are no durable effects.

## Lead integration checkpoint

The lead verified the original delivery and the independent preflight manifest
at `/private/tmp/chunkmaestro-ca-independent-preflight-c05823d4/`. That review
passed the bounded implementation scope, closed D1 for the exact delivered source
hashes, executed all 33 supplied tests and 23 independent reopen controls, and
explicitly remains ineligible for product QA aggregation. The original research
and design FAIL remain unchanged. No full issue acceptance follows from this
preflight.

Both projects are now in the shared solution. Managed Windows/macOS/Linux CI has
a separate 150-second profile-test command and retains its TRX/hang diagnostics.
Local macOS arm64 repository verification passes **349 tests**: 28 desktop,
33 profile, 18 credential and 270 persistence cases, zero failures/skips. Locked
restore, Release build, syntax and generated-inclusive formatting pass. All 46
CI-tooling tests, specification validation and actionlint pass. The reviewed
primitive bytes are unchanged; only solution, CI and this evidence note are added.

Vault export/composition and durable Pending/Ready metadata remain unimplemented.
The assembly still has no public API and grants no production signing authority.
The checkpoint is preparatory code on the issue 12 integration branch, depending
on the unmerged credential foundation; it is not a completed fleet identity service.
