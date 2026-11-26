# H1 build and native profile preparation

Issue14, Q145/151/155/162 and Q20/31/168. The later owner testing policy
requires meaningful failure tests and independent QA; diagnostic coverage does
not replace native evidence. This collector changes no product authority.
`runtime_pin` stays null and H1 runtime readiness stays REFUSED.

`python3 tooling/ci/h1_profile.py --output ABSENT_NAMESPACE --architecture x86_64 --sdk-root /usr/share/dotnet`
collects a bounded installed Linux profile as an unprivileged user. Use `aarch64`
on native ARM64. It refuses non-Linux (77, NOTRUN), root, wrong architecture,
missing tools/files, mutable installed paths, unexpected loader fragments,
unowned tool/library package associations, limits and command failures. SDK roots are a fixed
allowlist, never a PATH or home-cache search. SDK and generated configuration/cache origins remain explicitly unresolved even
when installed bytes have trusted ownership; hosted setup-dotnet origins require
a separate reviewed authentication design. No runtime or downloaded tool
is executed. Installed readelf, cc, ld and dpkg-query receive fixed version or
static inventory arguments. No ldd, system service, network, credential lookup,
environment dump, proc maps, process probes or kernel feature tests occur.

The JSON contains requested/canonical identities, SHA256 and byte counts,
installed tool versions, kernel identity, dpkg inventory and tool/library associations, static
ELF headers/dynamic/version requirements for a fixed loader/library set, loader
configuration identities and selected SDK/ref/compiler metadata identities.
This identifies installed bytes; it does not authenticate package origins or
reconstruct complete compiler, sysroot, OS or CLR dependencies. Config files are
hashed without treating their contents as instructions to follow more paths.
Installed root-owned system inputs are trusted against the unprivileged collector,
not against a malicious root or a simultaneous administrator update. The latter
can invalidate a profile; use immutable disposable images and retain image origins.

Each invocation exclusively creates its evidence directory under existing
non-symlink parents, and creates profile.json exclusively. Never reuse an output
namespace. First collection failure remains separate from command cleanup and
publication uncertainty. JSON write failure is reported on stdout and makes a
successful collection nonzero; a killed process can leave incomplete JSON, which
is never evidence of success. SIGTERM/interrupt, deadline and partial inventory
cannot qualify a profile. Preserve failed output, fix the source of the refusal,
and retry in a new namespace. No automatic deletion or recovery mutation occurs.
External command output is bounded to 2 MiB per command; files to 128 MiB, total
reads to 384 MiB and 128 inputs, collection to 90 seconds and each command to
10 seconds. The parent CI supervisor supplies an additional process-group deadline.
Command records retain the bounded bytes as base64 alongside readable output,
observed exit status, decoding failure and cleanup uncertainty. Invalid UTF-8
refuses collection without discarding captured bytes. The package inventory is
recorded before its row-count check, so an oversized inventory remains evidence
of the refusal. Base64 is an encoding of the same bounded bytes, not another read.

Success requires a complete JSON report, exit zero and confirmed supervisor cleanup.
The only success label is COLLECTED_UNQUALIFIED_PROFILE. Even that label leaves
H1-loaded modules, actual dynamic ICU/OpenSSL/NSS use, servicing paths, clean
pre-exec environment, runtime lifecycle, complete source/build mapping and signed
OS image origins unproven. No future host/CLR graph can be inferred from Python
collector mappings. There is no H1 app/host, Agent, guard or installed qualifier.

Run the inert reader/command tests with:
`python3 -B -m unittest discover -s tooling/ci -p test_h1_profile.py -v`.
They execute existing local Python only and test real bounded reads, symlink/FIFO
refusal, count/byte/deadline limits, output exclusion, command failure/timeout,
cancellation, cleanup uncertainty and secret-environment exclusion. Mocked Linux
predicates test denial ordering only. They do not simulate a successful Linux
profile. Real non-Linux refusal tests skip on Linux, where separate actual native
x64/ARM collection is still required. No numeric coverage gate is introduced.

The hosted native inspector matrix runs the bounded collector and retains its
profile and supervisor log using the existing always-upload step. Architecture,
SDK root and output are explicit CLI arguments; the command supervisor strips
unknown environment keys.
Do not relax existing check names, assertions, budgets, exports, locks or pins.
Collector shipping and H1 preparation require new independent artifact/design
review. F1 source/security work remains separate.
