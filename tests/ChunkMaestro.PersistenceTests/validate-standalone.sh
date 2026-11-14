#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
validation="$(mktemp -d "${TMPDIR:-/tmp}/chunkmaestro-storage-validation.XXXXXXXX")"
sdk="${CHUNKMAESTRO_DOTNET:-/Users/derek/.local/share/chunkmaestro/dotnet/dotnet}"
mkdir -p "$validation/src" "$validation/tests"
cp "$repo/.editorconfig" "$validation/.editorconfig"
for project in Domain Application Persistence; do
  rsync -a --exclude bin --exclude obj "$repo/src/ChunkMaestro.$project" "$validation/src/"
done
rsync -a --exclude bin --exclude obj --exclude TestResults "$repo/tests/ChunkMaestro.PersistenceTests" "$validation/tests/"
cat > "$validation/Directory.Packages.props" <<'PROPS'
<Project>
  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.15" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.15" />
    <PackageVersion Include="SQLitePCLRaw.bundle_e_sqlite3" Version="3.0.5" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
</Project>
PROPS
cat > "$validation/Directory.Build.props" <<'PROPS'
<Project><PropertyGroup>
  <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
</PropertyGroup></Project>
PROPS
cd "$validation"
"$sdk" new sln -n StorageValidation
"$sdk" sln StorageValidation.slnx add src/ChunkMaestro.Domain src/ChunkMaestro.Application src/ChunkMaestro.Persistence tests/ChunkMaestro.PersistenceTests
"$sdk" test StorageValidation.slnx --nologo --logger 'trx;LogFileName=storage.trx'
"$sdk" format StorageValidation.slnx --verify-no-changes --no-restore
printf 'Validation and evidence retained at %s\n' "$validation"
