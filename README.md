# ChunkMaestro

Native Windows/macOS Minecraft fleet control plane built with Avalonia, C#/.NET 10, local SQLite, and a restricted Debian agent.

Implementation has begun. No managed-host operations, installers, or v1.0 features are verified yet. This repository is source-available in intent; final license terms remain an owner decision. No license is selected by this bootstrap.

## Authoritative inputs

- [205 source answers](docs/product/requirements-source.md)
- [Orchestration directive](docs/product/orchestration-directive.md)
- [Discovery facts](docs/operations/discovery.md)

The repository starts with a source-preserving bootstrap so isolated implementation branches have a common ancestor. Subsequent implementation goes through issues, PRs, independent QA and tests.

## Build and development

Install the exact .NET 10 SDK in `global.json` and Python 3.9+, then run:

```sh
python3 tooling/build/build.py verify
```

Windows: use `python`. See the [development guide](docs/operations/development.md) for private SDK setup, locked dependency updates, solution integration, native run/test commands, CI and independent QA evidence. The bootstrap builds a dependency/policy probe; no product test suite or native application exists yet. Absent tests are explicitly reported, and `run` fails until a product executable lands.

Contributions follow [CONTRIBUTING.md](CONTRIBUTING.md); vulnerability handling is in [SECURITY.md](SECURITY.md). No release or license approval is implied by a green build.
