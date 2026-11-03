# File search and quick filters

Job `codex-armory-search`, 2026-11-03. Branch `lane/codex-armory-search` starts at
main `3da10788b67d8195383306fbacf95003901ca4bc`.

## What the tree showed

- `app.js` had a folder browser and virtualized long lists, including a 5,000-file
  demo, but no search box or file-list filters. The existing virtualization stays.
- Note `417e99fc` appears in `ShellThumbnails.cs` as evidence about missing
  thumbnails. The checked Armory tree does not independently establish that this
  note requested search. The missing search controls were verified directly.
- The three comments in `Session.cs`, `SyncEngine.Actions.cs` and
  `SyncEngine.Batches.cs` named idea-app migration 0316. In idea-app main
  `51b8c50c47d8a67f7c41dcd12503d89dc4448495`,
  [`0319_armory_class_projects.sql`](https://github.com/pina-hash/idea-app/blob/51b8c50c47d8a67f7c41dcd12503d89dc4448495/supabase/migrations/0319_armory_class_projects.sql)
  defines the class-folder write restrictions and PT403 refusal described by those
  comments. `0316_classroom_team_photos_and_names.sql` is unrelated. Only the three
  comment lines were corrected.
- The bridge has no search request. After the scope check, the requester explicitly
  chose a pure JavaScript module beside `app.js`, with no Core or bridge changes.
  A Core implementation plus a bridge call is a possible follow-up, if needed.

## What was built

`wwwroot/file-search.js` is a pure module with no DOM, bridge, disk, network or
SolidWorks access. `app.js` loads it locally before asking for the first view,
carrying the app script's version query to the module URL. Existing packaging
already copies every file under `wwwroot`.

Search waits 180 ms after typing stops. It matches literal substrings anywhere in
the file name or full folder path, ignoring case, normalizing canonical Unicode
equivalents and accepting either path separator. Accents remain significant. IME
composition completes before filtering. Ctrl+F focuses and selects the search box;
Escape clears its words, including while a search tooltip is shown. Dialogs keep
their existing keyboard behavior.

With an active search or filter, the list shows matching files throughout the open
folder and its descendants, with paths on the rows. Without filters it shows the
ordinary folder browser. The count beside the search label says how many files
match out of that subtree's total. An empty folder and a search with no matches
give different guidance.

The checkout choices combine with OR; text, checkout, changed locally and file
type combine with AND. Checked out by me includes the person's other computers;
checked out by others means another person. Changed locally uses the view's
`changed` flag or `changed` status. Part, Assembly and Drawing are `.sldprt`,
`.sldasm` and `.slddrw`; Other includes every other extension and extensionless files.

The module prepares an inventory snapshot once, then returns matching input
indexes without changing inputs. The page caches each folder's index and matches
until the inventory or options change. It keeps the existing virtualized rows.
Select all and Shift selection use matching files while filtering. Changing
filters clears selections; inventory updates drop selected files that stop
matching. The existing folder action buttons still operate on the whole folder,
as the search help states. Switching students or signing out resets the search.

## Verification

Run from the repository root:

```sh
dotnet restore --locked-mode
dotnet build --no-restore -warnaserror
dotnet test --no-build
node --test tests/Armory.Agent.Tests/FileSearchTests.mjs
node tests/Armory.Agent.Tests/FileSearchPageTests.mjs
```

The browser suite uses the same Playwright setup as `tools/agent-ui/check-ui.mjs`
and serves only `wwwroot` on loopback. `ARMORY_CHROMIUM=/path/to/chromium` selects
an installed browser. In this environment the browser download was blocked by
network policy and installed Chromium disallowed `file://`, so the suite used
Chromium 154 on Linux over loopback. It checks the page under its existing CSP,
with a stand-in WebView host, and checks that no requests leave the local page.

The 38 pure-module tests cover Unicode names and canonical accents, Greek sigma,
case expansions, emoji, supplementary characters, literal punctuation, 150-level
paths, duplicate names, immutable snapshots, empty folders, every combination of
filters, all file types and 5,000 files. The browser suite covers both themes at
1280x800 and 420x720, keyboard behavior, IME, live counts, empty states, matching
selection, inventory and activity updates, 44px controls, horizontal overflow,
virtualization and End reaching matching row 4,999.

Timing uses 5,000 synthetic files, all four filter groups, 500 verified matches,
20 warmup filters and 101 measured filters. Preparation is measured separately;
filter times exclude the intentional 180 ms debounce and rendering. The browser
test asserts p95 below 100 ms at four times CPU slowdown.

Measured on Linux x64 with Node 24.19.0 and Chromium 154:

| Test | Preparation | Filter median | Filter p95 | Filter max |
| --- | ---: | ---: | ---: | ---: |
| Node, 5,000 files | 22.670 ms | 0.371 ms | 0.471 ms | 1.302 ms |
| Chromium, normal CPU | 5.8 ms | 0.2 ms | 0.3 ms | 3.8 ms |
| Chromium, 4x CPU slowdown | 26.7 ms | 1.1 ms | 2.0 ms | 4.8 ms |

The actual input-to-painted-results path at 4x CPU slowdown took 241.0 ms,
including the intentional 180 ms debounce. This is checked against a 1 second
budget. The timing numbers are bounded samples on a shared host.

Locked restore succeeded. `dotnet build --no-restore -warnaserror` passed with
zero warnings and errors. With a throwaway PostgreSQL 16 cluster configured through
`ARMORY_TEST_POSTGRES`, the complete .NET suite passed 1,431 tests with 116
Windows-only skips and zero failures, including all 241 end-to-end tests and the
10,000-seed Core simulation. All 38 pure-module tests and all four theme/size
browser search flows passed. Existing guard tests were preserved.

## What remains unverified

The actual Windows WebView2 window, its native rendering and keyboard handling,
and a physical old lab PC were not exercised. Chromium CPU throttling is a
synthetic performance check, not a measurement of that hardware. The full legacy
`check-ui.mjs` matrix was not run because its file-URL harness is blocked here;
the new search browser suite ran over loopback instead. No release was published.
The documented replacement concurrent-writer limitation remains unchanged.
