# Lane C1 audit

Audited on 2026-09-27, America/Los_Angeles.

- The first `dotnet --list-sdks` returned no SDKs. Work stopped as instructed. Mr. Pina
  then authorized installation and continuation. WinGet installed Microsoft.DotNet.SDK.10,
  version 10.0.401; the command now lists it under `C:\Program Files\dotnet\sdk`.
  Microsoft's [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
  identifies .NET 10 as LTS. The installation used Microsoft's
  [Windows instructions](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
- Git 2.55.0.windows.5 is available. Both Git author identity fields have values.
- GitHub CLI authentication reports the active account `pina-hash`, with `repo` and
  `workflow` scopes. `gh repo view pina-hash/idea-armory` initially found no repository.
  Repository creation succeeded, and a subsequent check confirmed private and empty.
- The workspace had an empty, initialized Git repository on `master`. It was renamed
  to `main`, and its remote set to the new private repository.
- Read the entire public [scope](https://github.com/pina-hash/idea-app/blob/03f2c69194ab7d8c1eff248954eccd0daf63f6fe/docs/ARMORY.md).
  Scope revision: `03f2c69194ab7d8c1eff248954eccd0daf63f6fe`.
  The scope explicitly assigns the pure library and simulation to this repository and
  says that this lane depends on no spike result. No idea-app files were changed.
- The two-release back-save claim is confirmed by
  [SOLIDWORKS 2026 help](https://help.solidworks.com/2026/English/WhatsNew/c_wn2026_fundamentals_saving_previous_versions.htm).
  It also describes a native default back-save setting added in 2026 SP3/FD03. Actual
  installed service packs, licensing, file-format detection, and API behavior remain
  spike items, not assumptions in this library.
- Global and local `dotnet tool list` results are empty. Stryker.NET was not available
  and was not installed. Four targeted source mutations were run instead.

Scope reconciliation and clarifications:

1. The prompt says a conflict emits a side version then a download. Scope rule 2 says
   an open file is never overwritten. Open conflicts therefore emit a side version
   followed by a waiting notification; a new plan downloads after the file closes.
   The same open-file protection applies to moving a tombstoned file to recovery.
2. The prompt lists no action for its required local-deletion behavior. Added
   `ProposeTombstone`, requiring a lock and a conditional version append, never a purge.
3. The scope warns about release gaps a season ahead. The helper warns at a gap of two
   and separately marks gaps greater than two as outside the back-save range.
4. The broad phase-0-before-product-code wording is qualified by the scope's explicit
   C1 exception in "Where the code lives". The core uses interfaces for all unknown I/O.
5. A last-local-hash snapshot alone cannot preserve multiple offline saves. `SaveRecorder`
   captures immutable bytes and metadata for each save before acknowledging it, then
   journals that snapshot. Orphan captures are re-journaled after a torn write.

R2, Supabase, the website, real CAD files, licensing, and the school network are not used
by C1. Product-scope claims about their runtime behavior are not asserted as measured
facts here. No external integration was provisioned for a library that performs no I/O.
