# Agent window screens

Rendered by `node tools/agent-ui/render-screens.mjs` from the demo states in
`src/Armory.Agent/wwwroot/demo/states.js`, in both themes at 1280x800 and 420x720.
Each image is the window as it first opens (the viewport, not the whole scrolled page).
File names are `<screen>-<state>-<theme>-<width>x<height>.png`. The demo clock is fixed at
`2026-10-01T15:30:00-07:00`, so the relative times hold still between runs.

Home exists only after a computer is connected, so `signedOut` and `connecting` (and the
other not-yet-connected states) appear on the Connect screen. File detail is shown where a
file's own page tells the story best: `lockedByOther`, `releaseNotChecked` and `conflict`.
The Settings sheet is shown once per theme, over Home in the `synced` state.

64 images.

## Connect (first run)

| File | State | What it shows | Theme | Size |
|---|---|---|---|---|
| [connect-signedOut-idea-1280x800.png](connect-signedOut-idea-1280x800.png) | `signedOut` | First run, before connecting | IDEA | 1280x800 |
| [connect-signedOut-spaceWhite-1280x800.png](connect-signedOut-spaceWhite-1280x800.png) | `signedOut` | First run, before connecting | Space White | 1280x800 |
| [connect-signedOut-idea-420x720.png](connect-signedOut-idea-420x720.png) | `signedOut` | First run, before connecting | IDEA | 420x720 |
| [connect-signedOut-spaceWhite-420x720.png](connect-signedOut-spaceWhite-420x720.png) | `signedOut` | First run, before connecting | Space White | 420x720 |
| [connect-connecting-idea-1280x800.png](connect-connecting-idea-1280x800.png) | `connecting` | Waiting for the browser sign-in | IDEA | 1280x800 |
| [connect-connecting-spaceWhite-1280x800.png](connect-connecting-spaceWhite-1280x800.png) | `connecting` | Waiting for the browser sign-in | Space White | 1280x800 |
| [connect-connecting-idea-420x720.png](connect-connecting-idea-420x720.png) | `connecting` | Waiting for the browser sign-in | IDEA | 420x720 |
| [connect-connecting-spaceWhite-420x720.png](connect-connecting-spaceWhite-420x720.png) | `connecting` | Waiting for the browser sign-in | Space White | 420x720 |
| [connect-connectFailed-idea-1280x800.png](connect-connectFailed-idea-1280x800.png) | `connectFailed` | The browser sign-in didn't finish | IDEA | 1280x800 |
| [connect-connectFailed-spaceWhite-1280x800.png](connect-connectFailed-spaceWhite-1280x800.png) | `connectFailed` | The browser sign-in didn't finish | Space White | 1280x800 |
| [connect-connectFailed-idea-420x720.png](connect-connectFailed-idea-420x720.png) | `connectFailed` | The browser sign-in didn't finish | IDEA | 420x720 |
| [connect-connectFailed-spaceWhite-420x720.png](connect-connectFailed-spaceWhite-420x720.png) | `connectFailed` | The browser sign-in didn't finish | Space White | 420x720 |
| [connect-vaultOwnedByOther-idea-1280x800.png](connect-vaultOwnedByOther-idea-1280x800.png) | `vaultOwnedByOther` | The Armory folder belongs to another account | IDEA | 1280x800 |
| [connect-vaultOwnedByOther-spaceWhite-1280x800.png](connect-vaultOwnedByOther-spaceWhite-1280x800.png) | `vaultOwnedByOther` | The Armory folder belongs to another account | Space White | 1280x800 |
| [connect-vaultOwnedByOther-idea-420x720.png](connect-vaultOwnedByOther-idea-420x720.png) | `vaultOwnedByOther` | The Armory folder belongs to another account | IDEA | 420x720 |
| [connect-vaultOwnedByOther-spaceWhite-420x720.png](connect-vaultOwnedByOther-spaceWhite-420x720.png) | `vaultOwnedByOther` | The Armory folder belongs to another account | Space White | 420x720 |

## Home

| File | State | What it shows | Theme | Size |
|---|---|---|---|---|
| [home-synced-idea-1280x800.png](home-synced-idea-1280x800.png) | `synced` | Everything saved | IDEA | 1280x800 |
| [home-synced-spaceWhite-1280x800.png](home-synced-spaceWhite-1280x800.png) | `synced` | Everything saved | Space White | 1280x800 |
| [home-synced-idea-420x720.png](home-synced-idea-420x720.png) | `synced` | Everything saved | IDEA | 420x720 |
| [home-synced-spaceWhite-420x720.png](home-synced-spaceWhite-420x720.png) | `synced` | Everything saved | Space White | 420x720 |
| [home-syncing-idea-1280x800.png](home-syncing-idea-1280x800.png) | `syncing` | Sending and getting changes | IDEA | 1280x800 |
| [home-syncing-spaceWhite-1280x800.png](home-syncing-spaceWhite-1280x800.png) | `syncing` | Sending and getting changes | Space White | 1280x800 |
| [home-syncing-idea-420x720.png](home-syncing-idea-420x720.png) | `syncing` | Sending and getting changes | IDEA | 420x720 |
| [home-syncing-spaceWhite-420x720.png](home-syncing-spaceWhite-420x720.png) | `syncing` | Sending and getting changes | Space White | 420x720 |
| [home-offline-idea-1280x800.png](home-offline-idea-1280x800.png) | `offline` | No internet, work waiting to send | IDEA | 1280x800 |
| [home-offline-spaceWhite-1280x800.png](home-offline-spaceWhite-1280x800.png) | `offline` | No internet, work waiting to send | Space White | 1280x800 |
| [home-offline-idea-420x720.png](home-offline-idea-420x720.png) | `offline` | No internet, work waiting to send | IDEA | 420x720 |
| [home-offline-spaceWhite-420x720.png](home-offline-spaceWhite-420x720.png) | `offline` | No internet, work waiting to send | Space White | 420x720 |
| [home-conflict-idea-1280x800.png](home-conflict-idea-1280x800.png) | `conflict` | Kept as your own copy, and a newer version waiting | IDEA | 1280x800 |
| [home-conflict-spaceWhite-1280x800.png](home-conflict-spaceWhite-1280x800.png) | `conflict` | Kept as your own copy, and a newer version waiting | Space White | 1280x800 |
| [home-conflict-idea-420x720.png](home-conflict-idea-420x720.png) | `conflict` | Kept as your own copy, and a newer version waiting | IDEA | 420x720 |
| [home-conflict-spaceWhite-420x720.png](home-conflict-spaceWhite-420x720.png) | `conflict` | Kept as your own copy, and a newer version waiting | Space White | 420x720 |
| [home-refused-idea-1280x800.png](home-refused-idea-1280x800.png) | `refused` | Files Armory can't send, with the reason | IDEA | 1280x800 |
| [home-refused-spaceWhite-1280x800.png](home-refused-spaceWhite-1280x800.png) | `refused` | Files Armory can't send, with the reason | Space White | 1280x800 |
| [home-refused-idea-420x720.png](home-refused-idea-420x720.png) | `refused` | Files Armory can't send, with the reason | IDEA | 420x720 |
| [home-refused-spaceWhite-420x720.png](home-refused-spaceWhite-420x720.png) | `refused` | Files Armory can't send, with the reason | Space White | 420x720 |
| [home-lockedByOther-idea-1280x800.png](home-lockedByOther-idea-1280x800.png) | `lockedByOther` | Someone else is editing a file you opened | IDEA | 1280x800 |
| [home-lockedByOther-spaceWhite-1280x800.png](home-lockedByOther-spaceWhite-1280x800.png) | `lockedByOther` | Someone else is editing a file you opened | Space White | 1280x800 |
| [home-lockedByOther-idea-420x720.png](home-lockedByOther-idea-420x720.png) | `lockedByOther` | Someone else is editing a file you opened | IDEA | 420x720 |
| [home-lockedByOther-spaceWhite-420x720.png](home-lockedByOther-spaceWhite-420x720.png) | `lockedByOther` | Someone else is editing a file you opened | Space White | 420x720 |
| [home-releaseNotChecked-idea-1280x800.png](home-releaseNotChecked-idea-1280x800.png) | `releaseNotChecked` | Saved, but the SolidWorks version couldn't be checked | IDEA | 1280x800 |
| [home-releaseNotChecked-spaceWhite-1280x800.png](home-releaseNotChecked-spaceWhite-1280x800.png) | `releaseNotChecked` | Saved, but the SolidWorks version couldn't be checked | Space White | 1280x800 |
| [home-releaseNotChecked-idea-420x720.png](home-releaseNotChecked-idea-420x720.png) | `releaseNotChecked` | Saved, but the SolidWorks version couldn't be checked | IDEA | 420x720 |
| [home-releaseNotChecked-spaceWhite-420x720.png](home-releaseNotChecked-spaceWhite-420x720.png) | `releaseNotChecked` | Saved, but the SolidWorks version couldn't be checked | Space White | 420x720 |
| [home-paused-idea-1280x800.png](home-paused-idea-1280x800.png) | `paused` | Paused by the student | IDEA | 1280x800 |
| [home-paused-spaceWhite-1280x800.png](home-paused-spaceWhite-1280x800.png) | `paused` | Paused by the student | Space White | 1280x800 |
| [home-paused-idea-420x720.png](home-paused-idea-420x720.png) | `paused` | Paused by the student | IDEA | 420x720 |
| [home-paused-spaceWhite-420x720.png](home-paused-spaceWhite-420x720.png) | `paused` | Paused by the student | Space White | 420x720 |

## File detail

| File | State | What it shows | Theme | Size |
|---|---|---|---|---|
| [detail-conflict-idea-1280x800.png](detail-conflict-idea-1280x800.png) | `conflict` | Kept as your own copy, and a newer version waiting | IDEA | 1280x800 |
| [detail-conflict-spaceWhite-1280x800.png](detail-conflict-spaceWhite-1280x800.png) | `conflict` | Kept as your own copy, and a newer version waiting | Space White | 1280x800 |
| [detail-conflict-idea-420x720.png](detail-conflict-idea-420x720.png) | `conflict` | Kept as your own copy, and a newer version waiting | IDEA | 420x720 |
| [detail-conflict-spaceWhite-420x720.png](detail-conflict-spaceWhite-420x720.png) | `conflict` | Kept as your own copy, and a newer version waiting | Space White | 420x720 |
| [detail-lockedByOther-idea-1280x800.png](detail-lockedByOther-idea-1280x800.png) | `lockedByOther` | Someone else is editing a file you opened | IDEA | 1280x800 |
| [detail-lockedByOther-spaceWhite-1280x800.png](detail-lockedByOther-spaceWhite-1280x800.png) | `lockedByOther` | Someone else is editing a file you opened | Space White | 1280x800 |
| [detail-lockedByOther-idea-420x720.png](detail-lockedByOther-idea-420x720.png) | `lockedByOther` | Someone else is editing a file you opened | IDEA | 420x720 |
| [detail-lockedByOther-spaceWhite-420x720.png](detail-lockedByOther-spaceWhite-420x720.png) | `lockedByOther` | Someone else is editing a file you opened | Space White | 420x720 |
| [detail-releaseNotChecked-idea-1280x800.png](detail-releaseNotChecked-idea-1280x800.png) | `releaseNotChecked` | Saved, but the SolidWorks version couldn't be checked | IDEA | 1280x800 |
| [detail-releaseNotChecked-spaceWhite-1280x800.png](detail-releaseNotChecked-spaceWhite-1280x800.png) | `releaseNotChecked` | Saved, but the SolidWorks version couldn't be checked | Space White | 1280x800 |
| [detail-releaseNotChecked-idea-420x720.png](detail-releaseNotChecked-idea-420x720.png) | `releaseNotChecked` | Saved, but the SolidWorks version couldn't be checked | IDEA | 420x720 |
| [detail-releaseNotChecked-spaceWhite-420x720.png](detail-releaseNotChecked-spaceWhite-420x720.png) | `releaseNotChecked` | Saved, but the SolidWorks version couldn't be checked | Space White | 420x720 |

## Settings sheet over Home

| File | State | What it shows | Theme | Size |
|---|---|---|---|---|
| [settings-synced-idea-1280x800.png](settings-synced-idea-1280x800.png) | `synced` | Everything saved | IDEA | 1280x800 |
| [settings-synced-spaceWhite-1280x800.png](settings-synced-spaceWhite-1280x800.png) | `synced` | Everything saved | Space White | 1280x800 |
| [settings-synced-idea-420x720.png](settings-synced-idea-420x720.png) | `synced` | Everything saved | IDEA | 420x720 |
| [settings-synced-spaceWhite-420x720.png](settings-synced-spaceWhite-420x720.png) | `synced` | Everything saved | Space White | 420x720 |
