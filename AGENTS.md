# Darkspyre.Common

Shared libraries for Darkspyre applications. Each library is its own project and DLL under `src/`, with its tests under
`tests/`.

Every rule for agents on this workstation lives in `~/.agents/` (`~/.agents/AGENTS.md` and the files it names). This file
adds only what is particular to this repository.

## Design record

The design record (common law L8) is `README.md`: one section per library, with what it does, its seams and how a
consumer starts it. A change to a library's pattern, seam, interface or data shape updates its section in the same
commit.

## Public contracts

None. Every shape in this repository is internal to the Darkspyre repositories that consume it, so a shape change fixes
every caller in the same change (L1).

## Laws

The common laws apply (`~/.agents/CommonLaws.md`). This repository records no law exception.

## Operating-system boundaries

All OS-specific code must live behind an OS-agnostic capability contract, as agent-ds `AGENTS.md`
"Operating-system boundaries" states. Portable consumers call that contract. Platform implementations own OS detection,
native bindings and ABI layouts, native-library names and loading, system commands, platform paths, filesystem
semantics, and platform-specific process behaviour.

## Versions

Each library's version is `<net>.<major>.<minor>.<build>`, set by its property in `Directory.Build.props`
(`DarkspyreCommonGitVersion` for `Darkspyre.Common.Git`). The first part is the .NET major version the libraries target.
The last part goes up by one when the library's code changes. Each library's csproj sets `<Version>` from its property.
