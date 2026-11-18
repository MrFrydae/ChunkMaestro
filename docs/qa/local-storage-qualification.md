# Local storage qualification

Owner decision, 2026-11-17: stop using Storage safety GitHub Actions. All four
dedicated storage workflows are disabled and removed, including both hosted
pilots and the duplicate coverage workflow. Ordinary managed build/style/product
tests and secret scanning remain in CI. Do not re-enable a campaign with labels,
dispatch, scheduled execution or another hosted workflow.

The replacement is an explicit local command on real Windows and macOS machines.
It removes hosted runner queues and lets the operator inspect/stop processes and
raw files directly. Mutation computation still takes time; no speedup or current
qualification PASS is claimed. The existing 90% line / 85% branch / 80% actual
mutation-kill thresholds and exact-head premerge requirement remain in force.

Commit changes first, use Python 3.13+ and the pinned SDK, and use a new ignored
output directory. Run only one collector per checkout. Private tool collection
checks the owner's authenticated `gh api user` identity before building/running
it; credentials are removed from the child process environment. Local mode is
explicit and refuses execution inside GitHub Actions.

Quick runtime/coverage feedback, without mutation:

```sh
python3 tooling/quality/storage/local.py check .artifacts/storage-local/check-01
```

Full collection, once on each supported operating system at the same commit:

```sh
python3 tooling/quality/storage/local.py collect .artifacts/storage-local/windows-01
# On macOS, use a different destination:
python3 tooling/quality/storage/local.py collect .artifacts/storage-local/macos-01
```

The collector runs coverage and Domain/Application mutations, the pinned-source
official/private Persistence pair with complete baseline/restoration controls,
full-volume tests and (on macOS) native C qualification. Existing command deadlines
and 30-second heartbeats remain. `local-session.json` identifies the failing
phase; individual raw logs retain command exit, timeout and progress details.
Failures stop the pipeline without automatic retry. COLLECTED is not PASS.
Cancellation or interrupted evidence never earns kill credit.

Copy both complete evidence directories without changing their bytes to the
machine where the same exact checkout will validate them. Preserve originals.
Then prepare a receipt in a new directory outside those platform directories:

```sh
python3 tooling/quality/storage/local.py receipt \
  --windows /retained/windows-01 --macos /retained/macos-01 \
  --output .artifacts/storage-local/receipt-01
```

This reruns the existing raw-evidence aggregate validator: both platforms,
coverage, full mutation catalogues, original eligibility, actual attributed kills,
native C, full-volume, populated upgrade/recovery and exact source/head binding.
It emits a failure gate report rather than a PASS receipt if any requirement fails.
Successful preparation writes inventories, their SHA256 digests, `receipt.json`
and `attestation.md`. Inventory digests bind the retained files, not archive ZIPs.

The lead inspects the raw evidence and retention locations, then publishes the
prepared storage attestation in a PR comment using the repository-owner account.
Do not publish a synthetic receipt or claim measurements not executed. The fast
`qa-evidence` metadata check authenticates the latest owner-attested receipt,
exact commit, platform references and thresholds; it does not download or rerun
the raw evidence. This deliberately uses the same accountable lead-attestation
boundary as independent QA. Independent functional/security/UX product verdicts
remain a separate requirement. A storage receipt alone never approves a merge.

Historical hosted pilot artifacts and FAIL/UNRESOLVED reports remain unchanged.
They do not become local PASS evidence. The earlier sharding/pilot documents are
historical mechanism research, not instructions to restart GitHub Actions.
