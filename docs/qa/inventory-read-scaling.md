# Inventory endpoint projection measurement

Implementation evidence for PR #6, parent `0cfe19b528b202fcad22fb4adca3b982f56300a6`,
with an uncommitted owned patch. This is not an independent QA verdict or storage
qualification. Requirements: Q6–7 (multiple fleets and measured instance capacity),
Q21 (multiple instances per host), Q45 (Java/Bedrock metadata), Q149/Q153 (SQLite), Q164 (truthful
cached observations), and directive §13 measurement guidance.

`ReadWorkerAsync` previously scanned every materialized endpoint for each instance.
It now builds one `ILookup<Guid, EndpointRow>` and indexes it during projection.
The four database queries, identity checks, gate, error handling and cancellation
flow are unchanged. Lookup groups retain source sequence; the existing edition,
then port ordering remains in each group. Association becomes expected O(E + I)
rather than O(E × I), before unchanged per-instance sorting. The lookup adds O(E)
references and grouping overhead; allocation/peak memory were not measured.

## Workload and method

Measured on macOS 27.2.1 (26B131), Darwin 27.2.0 arm64, .NET SDK 10.0.404,
runtime 10.0.15, Python 3.13.18. The ignored harness and complete raw evidence are
in `.artifacts/inventory-read/` in this checkout. That directory is local evidence,
not a tracked dependency or benchmark framework.

Three disposable SQLite databases under `/private/tmp/cm-inventory-profile-92312163`
were initialized through `LocalFleetStore.OpenAsync`. A deterministic fixture used
real EF/SQLite transactions to insert data, then closed before measurement. Both
implementations reopened the **same files**, without reseeding or recovery copies:

| Instances | Fleets | Hosts | Endpoints |
| ---: | ---: | ---: | ---: |
| 0 | 0 | 0 | 0 |
| 20 | 2 | 10 | 120 |
| 1,000 | 2 | 10 | 6,000 |

IDs are nonempty deterministic GUIDs; each instance has a unique service per host,
six endpoints (three ports for each edition), valid ports, matching host/instance
references and canonical DNS addresses. The 1,000 case is a bounded stress fixture,
above the owner's approximately 20-instance estimate, not an accepted product limit.
Direct fixture seeding does **not** establish public registration acceptance.

Only awaited public `ReadAsync` calls were timed with `Stopwatch`. Open, migration,
seed, restore/build, JSON hashing and validation are outside the timer. Each case
has ten recorded warmup calls (iterations -10 through -1), then thirty recorded
samples (0 through 29). Each call verifies exact fleet/host/instance counts and
instance IDs, host association, addresses, editions, ports, endpoint sequence and
Unknown instance observations. A SHA-256 of the entire serialized inventory with
only `RetrievedAt` normalized verifies all returned metadata and list order against
the baseline. All 120 corresponding digests match in each measurement pair.

## Results

Milliseconds for measured public reads; raw warmups and samples are retained in
`before.jsonl`, `after.jsonl`, `before-isolated.jsonl`, `after-isolated.jsonl`.

| Pair | Instances | Before median (min–max) | After median (min–max) |
| --- | ---: | ---: | ---: |
| Initial | 0 | 0.642 (0.510–4.737) | 0.627 (0.471–3.919) |
| Initial | 20 | 0.903 (0.696–3.393) | 0.912 (0.752–3.268) |
| Initial | 1,000 | 19.781 (16.636–21.121) | 11.006 (10.021–14.339) |
| Isolated repeat | 0 | 0.565 (0.497–3.665) | 0.714 (0.488–1.052) |
| Isolated repeat | 20 | 0.793 (0.742–2.990) | 0.917 (0.802–3.401) |
| Isolated repeat | 1,000 | 36.658 (15.558–40.103) | 8.692 (7.592–17.552) |

The initial baseline process was allowed to finish asynchronously while preparation
for the focused tests continued, so possible build contention cannot be excluded.
The isolated repeat rebuilt and ran each variant sequentially, without concurrent
commands from this implementation agent, restoring the exact patched bytes afterward.
Both pairs support a useful improvement at 1,000 instances. Small/empty results
show no useful improvement and some overhead/noise. Variation between pairs is
substantial: these are local warm-process observations, not an SLA or a stable
speedup ratio. No profiler attribution, cold-cache, multiple-reader, native UI,
allocation, memory-pressure or Windows measurement was performed.

## Validation and limits

New `InventoryProjectionTests.cs` uses **public registration and reads** with six
instances across two hosts/fleets. It verifies distinct endpoint addresses, repeated
port numbers across editions and hosts, edition/port order, names, service names,
parent associations, Unknown observations, registration timestamps, repeated read
and reopen stability. It does not invent a GUID sort contract. A second test covers
empty inventory, pre-canceled read, subsequent successful use and disposed refusal.
The two tests passed on the baseline (2/0/0); they protect behavior, not the performance
implementation. There are no timing assertions. Cancellation during CPU projection
is not measured; the existing projection has no per-endpoint cancellation checks.

Root `verify` passed locked restore, Release build (0 warnings/errors), syntax and
generated-inclusive formatting checks, and 247 tests / 0 failures / 0 skips.
The first root attempt failed with MSB4166 premature child-node exits; its log is
retained. Retry with `MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0`
passed. The diagnostic directory named by MSBuild had no `*.failure.txt` to copy.
The first harness build failed CA1305 for locale-dependent integer formatting/parsing;
explicit invariant culture corrected it before any dataset was seeded or read.
Exact commands, logs, source/test hashes and full patch are in the ignored evidence
bundle; `manifest.json` identifies the measured harness and owned source files.

No mutation or qualification campaign ran. The production read change makes prior
coverage/mutation metrics historical context, not current qualification. Windows
local execution, full Windows/macOS 90/85/80, actual kills, native volume and product
QA remain blocked. Existing backup tests were unchanged. No hosted workflow,
GitHub metadata or production storage was touched; no commit or push was made.

Rollback: remove the lookup declaration and restore the original per-instance
`endpoints.Where(e => e.InstanceId == x.Id)` expression; remove the new tests/report
if reverting this slice. There is no schema/data change or recovery operation.
Disposable profile databases are retained solely as local evidence; no user data
requires rollback.
