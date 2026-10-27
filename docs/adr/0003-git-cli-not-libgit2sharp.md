# ADR-0003: Read Git through the git CLI, not LibGit2Sharp

- **Status:** Proposed
- **Date:** 2026-10-27

## Context

Diff and lineage need to read model files at given refs, find merge bases, list changed paths, and (for lineage) clone or fetch other repos. Options: LibGit2Sharp (0.32.0, MIT, bundles native libgit2), or the git CLI.

## Decision

Use the **git CLI** behind an `IGitReader` interface in Core:
`rev-parse`, `merge-base`, `diff --name-only`, `ls-tree -r -z`, and one long-running `cat-file --batch` per operation to read blobs. Lineage clones and fetches go through `git clone/fetch --filter=blob:none` into a cache directory.

## Consequences

- **Auth just works**: Git Credential Manager on dev machines, and `System.AccessToken` / `http.extraheader` on ADO agents. We never handle Git credentials.
- No native binaries to ship, and full feature parity (partial clone, sparse checkout, worktrees, SSH).
- Requires git on PATH (true on ADO agents and dev machines). A missing git exits with code 4 and a clear message.
- Process spawning is slower than in-process calls. `cat-file --batch` keeps it to one process per side.
- Tests create temporary repos with the same CLI, so fixtures are plain folders (no nested `.git`).

## Alternatives considered

- **LibGit2Sharp:** in-process and fast, but its auth story differs from the user's git setup (no credential manager, limited SSH), native binary packaging is an extra concern, and it lacks partial clone. Rejected.
