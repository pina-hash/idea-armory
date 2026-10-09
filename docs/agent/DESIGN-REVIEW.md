# Agent window design review

The Armory Agent window (`src/Armory.Agent/wwwroot/`) was reviewed against ideabosco.com's shape language, the Plate (idea-app `docs/standards/IDEA_INTERFACE_STANDARDS.md` section 14), in four passes. Every pass worked from the rendered screens (`docs/agent/screens/`, made by `node tools/agent-ui/render-screens.mjs`) and was held to the same proof: `tools/agent-ui/bbox-diff.mjs` (one geometry for both themes) and `tools/agent-ui/check-ui.mjs` (the window's hard rules).

- **Pass 1** compared Home, File detail, Settings and Connect with idea-app's approved page (`docs/feedback/2026-09-25/overnight/shapes-v3-page-idea-1440.png` and `shapes-v3-page-space-white-1440.png`, with the 2x detail and chip crops), measuring surface colors from the PNGs and element boxes from the live page, and listed every way the app looked less finished than the site.
- **Pass 2** fixed those items and recorded what changed for each one.
- **Pass 3** was a fresh subagent that had seen none of the earlier work and was given only the 64 screenshots and the two reference pages. It ranked the problems it saw by how visible they were; this pass then fixed every high and medium item and the cheap low ones, and says below what was done for each, including what could not be done inside the window's hard rules.
- **Pass 4** was an adversarial check of the finished window against every hard rule of the lane and against `docs/agent/BRIDGE.md`. It fixed each breach it found, made the checker able to see each one (with a planted defect for every new check), and says below what each fix was.

Paths under `scratchpad/ui/` in passes 1 and 2 are the working files of those passes (measurement scripts, crops and scrolled renders); they are not in the repository. The quoted script results inside pass 2 are pass 2's own numbers; the current ones are under Proof at the end.

## Pass 1: ways the app looked less finished than the site

Looked at: `docs/agent/screens/home-{synced,syncing,offline,conflict,refused,lockedByOther,releaseNotChecked}-{idea,spaceWhite}-{1280x800,420x720}.png` (plus detail, settings and connect), the scrolled renders in `scratchpad/ui/look/`, 2x crops rendered for this pass (`scratchpad/ui/p1/z-*.png`), and idea-app's `shapes-v3-page-idea-1440.png`, `shapes-v3-page-space-white-1440.png`, `shapes-v3-detail-space-white-2x.png`, `shapes-v3-chips-space-white-2x.png`. Brief: `scratchpad/ui/plate-brief.md` sections 1, 5 and 7.

Measured, not guessed: surface colors were sampled from the PNGs, and element boxes were read from the live page (`scratchpad/ui/p1/measure.mjs`, `px.mjs`).

The site does three things the app mostly doesn't. Its surfaces step up in value, from the column to the card to the key. Its type has three voices, each with one job. And it uses chips and decoration sparingly. Most of the findings below come back to one of those three.

---

### Home: most important first

#### 1. There is no raised card layer: the right column is a well inside a well, so IDEA reads as a black hole outlined by hairlines
- **Where:** every Home shot, right column. Sampled IDEA values: slab `#121413`, recessed column `#0c0e0d`, list wells and rows `#0f1110`. The three surfaces that cover about 90% of the window are within 6 RGB points of each other. The only raised thing is the status display (`#171a18`). In Space White the wells (`#e4e8ee`) sit on a recess (`#dadee6`) of nearly the same value, so they read as outlined boxes, not trays.
- **Site instead:** the recessed column is the darkest surface, and it holds raised cards a clear step lighter. Then keys are lighter still. In the mockup that is recess `#141716`, card `#323839`, key `#3f4641` in IDEA, and recess `#c9ced8` with near-white cards `#eaedea` in Space White. Brief section 5: "a region reads by value before it reads by edge." Production lowered every IDEA step "in lightness only", to card `#1c1f1d`, but kept the ladder. The app uses production's values, which is correct. It just never uses the card step. This is exactly the site's "before" column: flat ground with 1px outlines doing all the work.
- **Side effect, a rule breach:** a status chip (`#121413` to `#171a18`) on a `#0f1110` well is *lighter* than its ground, so it reads as a small raised tile rather than a recessed tag. In Space White the chip and the well are the same value, so the chip reads as an outlined label. On a card face, the chip becomes a shade darker than its ground, which is the recessed look 3.12 asks for.
- **Fix:** give the lists in the recess the panel material (brief 3.9: raised, not pressable; the rows inside are the pressable part). Keep production values. Do not lighten anything.
  ```css
  .plate-recess .list-well {
  	background-color: var(--plate-panel-ground);
  	background-image: linear-gradient(to bottom, var(--plate-panel-top), var(--plate-panel-bot));
  	box-shadow:
  		inset 0 1px 0 0 var(--plate-panel-hi),
  		0 1px 2px 0 var(--plate-drop),
  		0 8px 14px -6px var(--plate-drop),
  		-2px 18px 30px -14px var(--plate-drop-far);
  }
  ```
  The hairline stays `--plate-hair` on the same recess ground, so the 3:1 measurement is unchanged (IDEA 3.7:1, Space White 3.1:1). The ladder then reads recess `#0c0e0d`, card `#1c1f1d`, key `#2f3431` in IDEA, and recess `#dadee6`, card `#f5f7f9` in Space White, which matches the site.

#### 2. The side column scrolls away and leaves a 360px dead strip; the sync status goes out of sight
- **Where:** `look/scrolled-1280-idea.png`. On a 1280x800 window the left column's content ends at y=611, but Home is 1593px tall in `synced` and 1776px in `conflict`. For the remaining 1000+px of scrolling the left third of the window is bare slab, and the status display (the window's main job) is gone.
- **Site instead:** the side column holds a few housings, and nothing is ever an empty third of the page beside a long list.
- **Fix:** wrap `.status-group` and `.account-group` in one `<aside class="side-col">` in app.js (grid-area `side`) and make it sticky above 760px. Sticky alone on `.status-group` won't work, because grid row 2 is `1fr` and it would slide over the account group.
  ```css
  @media (min-width: 761px) {
  	.side-col { position: sticky; top: 0; align-self: start; display: grid; row-gap: var(--space-4); }
  }
  ```

#### 3. Chips on every row, saying what the row already says
- **Where:** in `home-synced-*-1280x800`, 13 of the 16 chips on the page say SAVED. Each is right next to "Saved by Alex Kim · 2 days ago". Other rows say the same thing twice: "YOU'RE EDITING" plus "You're editing this.", "CAN'T SEND" plus "Can't send this one. ...", "WAITING TO SEND" plus "Saved on this computer. Waiting to send." In IDEA the chip ink is full `--plate-ink` (`#e7eae8`) in caps, so a column of white SAVED tags is the brightest repeated mark on the page. The three that matter (BEING EDITED, NEWER VERSION, CAN'T SEND) get lost among them.
- **Site instead:** a chip appears only where it marks a state worth seeing (NOT STARTED) and is never repeated by the text beside it. "The only color in the list is the accent."
- **Fix:** don't render a chip for `synced`, since the meta line already says "Saved by". For the other statuses, drop the first sentence of `note` when it just restates the chip word, so the chip is the word and the note adds the next step ("It saves to Armory each time you save in SolidWorks."). That's an app.js change, and it needs no new copy.

#### 4. One voice everywhere: title, sentence and meta are the same face at nearly the same size, so rows read like paragraphs
- **Where:** a Needs-you row is chip, title (14.4px/700), detail (13.6px/400 in full ink) and meta (13.6px/400 in ink-2): four lines, three of them the same size in the same face. File rows: name (14.4/700), meta (13.6), note (13.6). The status display: headline 16/600, detail 14.4. Project pads, folder heads, the email and the detail h1 are all 600 to 700 too, and with no 600 weight installed on the render host, everything renders bold.
- **Site instead (brief section 5, "three voices"):** reading-face titles at 600, body at 400, and metadata in the *mono label voice* ("ASSIGNMENT · DUE SEP 23, 5:00 PM · 20 PTS"). The brief's own list recipe (3.11) has `.row .row-meta { font-family: var(--font-mono); font-size: var(--plate-label-size); letter-spacing: 0.04em; ... }`. The app replaced it with the reading face at 0.85rem.
- **Fix:** use a three-step scale with distinct voices. Keep file names and people's names as typed (no uppercase), so 7.8's rule holds.
  ```css
  .row .row-name { font-size: 1rem; font-weight: 600; }
  .row-detail { font-size: 0.875rem; }
  .row-meta { font-family: var(--font-mono); font-size: 0.75rem; letter-spacing: 0.02em; color: var(--plate-ink-2); }
  .sync-line, .row-name, .detail-title h1 { text-wrap: balance; }
  .row-detail, .sync-detail, .row-note { text-wrap: pretty; }
  ```
  Use 600, not 700, for `.row-name`, `.pad.project-pad`, `.folder-head`, `.acct-email` and `.sheet-title`.

#### 5. The face itself: the screenshots render DejaVu Sans, which is wider and heavier than either Segoe UI or the site's Rajdhani
- **Where:** every shot. `--font-display` is `'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif`. On this Linux host `system-ui` resolves to DejaVu Sans, about 12% wider than Segoe with a huge x-height and only 400/700 weights. That's why every bold line looks blunt, and why "Everything is saved to / Armory." wraps. The review images undersell the Windows result and oversell the clunkiness.
- **Site instead:** Rajdhani, a condensed technical sans, carries most of the site's character.
- **Fix:** (a) for review fidelity, add metric-closer fallbacks before `system-ui`: `'Segoe UI Variable Text', 'Segoe UI', 'Liberation Sans', Arial, system-ui, sans-serif`. Liberation Sans is installed here, and it changes nothing on Windows. (b) Raise with Mr. Pina, don't just decide: Bahnschrift ships with Windows 10 and later, is the closest system match to Rajdhani, and is the brief's own suggestion (7.3). The lane prompt names Segoe UI, so this needs his say before anyone puts it first in `--font-display` for titles.

#### 6. Every folder is its own box with a floating bold heading between boxes: a stack of wells instead of one list
- **Where:** below Projects in every Home shot: "Main folder 1 FILE" plus a well holding 1 row, "Drivetrain 5 FILES" plus a well, "Intake", "Elevator", "COTS"... Five wells, five headings, five gaps.
- **Site instead:** one list well per group, with the group's header as an etched row *inside* the well ("▼ UNIT 3 · MATERIALS AND TESTING  0 OF 3 DONE | 5 ITEMS"), cut off from the rows by a groove.
- **Fix:** render one list (now a card, per item 1) per project. Each folder starts with a header row: the folder name in the reading face at 600 (as typed), and the count in the label voice pushed right.
  ```css
  .folder-row { display: flex; align-items: baseline; justify-content: space-between; min-height: 36px; padding: 0.55rem 0.2rem 0.4rem;
  	box-shadow: inset 0 -1px 0 0 var(--plate-groove-dk), inset 0 -2px 0 0 var(--plate-groove-lt); }
  .folder-row .folder-count { margin-left: auto; letter-spacing: var(--plate-label-track); }
  ```
  That removes four borders, four gaps and the 8/15/17px spacing jumble (item 11).

#### 7. Brackets on every group inside the recess: three nested frames around every list
- **Where:** right column, all shots. The recess edge, then the L ticks and mid-edge ticks of `.group` (Needs you, My files, Projects), then the well hairline. Five bracketed groups on one Home screen, and at 420 the brackets sit 6px from the recess edge.
- **Site instead:** brackets mark a *region*. There is one set inside the recessed column's corners around the whole card field (with "+" ticks in the gutters), plus one set around each side-column item. Nothing is bracketed inside the column.
- **Fix:** `.plate-recess .group::before { content: none; }` (paint only, no box moves). Keep the brackets on the side column's Status and This computer groups. If the column wants its own corners, put one `.group`-style bracket set on a wrapper inside the recess.

#### 8. Empty states drawn as empty boxes, in the most prominent spot
- **Where:** in `synced`, `syncing`, `offline` and `lockedByOther` (both themes, both sizes), the first thing in the main column is a full-width, 59px-tall bordered well holding one gray sentence: "Nothing needs you right now." In `releaseNotChecked`, "My files" is a paragraph of help text in a well.
- **Site instead:** no empty containers. A region holds content or isn't drawn.
- **Fix:** when `needsMe` is empty, don't render the Needs-you group. The status display already says ALL SAVED, and the group appears the moment something needs the student. When `myFiles` is empty, render its help as a `.group-help` line on the recess with no well (or as a `.notice` printed line, brief 3.5). The section order then leads with content in every state.

#### 9. Row anatomy misaligns across groups and inside rows
- **Where:** `home-conflict-*-1280x800` and `home-refused-*`. Needs-you rows have no glyph, so their text starts at x=444, while My files and folder rows start at x=475. Two adjacent lists have different left text edges, 31px apart. Inside a file row the kind glyph is pinned to the top (`align-self: start; margin-top: 2px`) while the chip and chevron are vertically centered, so on a three-line row the glyph sits on line 1 and the chip floats between lines 2 and 3. The glyphs are `--plate-ink-2` gray 20px outlines, and part and assembly are hard to tell apart at that size.
- **Site instead:** every row has the same columns (triangle, glyph, words, chip). The glyph is centered on the row, the chip is top-right on the title line, and glyphs are drawn in the row's ink.
- **Fix:** give `.row-main.attn` the file-row grid (`20px minmax(0,1fr) auto 16px`) with a kind glyph: refused or name taken get a "can't send" glyph, newerWaiting a down arrow, sideVersion two sheets, releaseNotChecked a question mark. That also satisfies "a glyph, a shape or a word" (rule 30) and lines the text up at 475. Then set `.row-icon { align-self: center; margin-top: 0; color: var(--plate-ink); }`, `.row-main > .chip { align-self: start; margin-top: 1px; }` (on the title line) and `.row-go { align-self: center; }`.

#### 10. At 420 every chip drops to its own line, adding 26px to every row
- **Where:** `home-*-420x720`, `look/scrolled-420-sw.png`. Folder rows are 54px at 1280 but 80px at 420 because of the `'icon chip go'` second grid row. Home in `synced` is 2438px tall at 420, and "This computer" starts at y=2377. In Needs-you rows the meta line also orphans "· 12 minutes ago" or "· 9 minutes ago" onto its own line.
- **Site instead:** the chip stays top-right on the title line at 375px too, and the name wraps beside it.
- **Fix:**
  ```css
  @media (max-width: 560px) {
  	.row-main.file { grid-template-columns: 20px minmax(0,1fr) auto 16px;
  		grid-template-areas: 'icon body chip go'; }
  }
  ```
  With item 3 (no SAVED chips), most rows then have no chip at all. For the meta line, `text-wrap: pretty` (item 4) keeps the time with its dot.

#### 11. The vertical rhythm has no rule, and the two columns start 4px apart
- **Where:** `home-synced-*-1280x800`, measured: label to label between groups 46px, pads to folder head 15px, folder head to its well 8px, well to next folder head 17px. Side column: STATUS label at y=156 against NEEDS YOU at y=152, and display top at 183 against first well at 179. The two columns' first lines miss each other by 4px, which is the kind of near-miss that reads as sloppy.
- **Site instead:** "Gutters between cards 0.75rem, region padding and column gaps 1rem, card padding 1.5rem". Two or three values, used everywhere, and the side column's first housing shares its top line with the first card row.
- **Fix:** `.status-group { margin-top: 22px; }` (was 26px) so both columns share their label line and box tops. Use one between-groups gap (`.main-col { gap: var(--space-3); }`, which with the group's 14px foot and 8px head makes 34px) and one label-to-content gap (0.6rem) everywhere. Item 6 removes the folder gaps entirely.

#### 12. Side-column controls are left-aligned, and the display carries a key
- **Where:** Pause is left-aligned inside the status housing, and Sign out is left-aligned under This computer (every 1280 shot).
- **Site instead:** "Controls in a side column are centered under the group above them" (brief section 5). OPEN TO-DO is centered under the ring, and the display housing holds only readout, label and text.
- **Fix:** take Pause out of the display and set it centered directly under the housing (`justify-self: center`), as the site does with OPEN TO-DO. Do the same for Sign out (`.account-group .key { justify-self: center; }`). If Pause stays inside, use `.display-foot { justify-content: center; }`.

#### 13. "This computer" is the only text on the window sitting on bare plate
- **Where:** left column, below Status. "Signed in as / jordan.reyes@boscotech.edu / LAB-PC-14 · Files in C:\IDEA\Armory" is printed straight on the slab with a key under it. It looks unfinished beside the housed display above it.
- **Site instead:** on the plate itself, only labels are printed. Content sits in a housing, card or well.
- **Fix:** put the account lines in a `.panel` (same recipe as item 1, `padding: 1rem 1.25rem`). Set them as label-and-value pairs in the label voice: `SIGNED IN AS` over the email, `COMPUTER` over LAB-PC-14. Drop "Files in C:\IDEA\Armory", which Settings already owns and Open folder already serves.

#### 14. The top of the window is two near-empty strips
- **Where:** 1280 shots. A 28px screw band above the header holds two screws and nothing else, which under the Windows title bar reads as a second empty title bar. Below it, the header holds a 32px emblem, a small "ARMORY" in bold mono, and about 780px of empty plate before Open folder and Settings.
- **Site instead:** the header is dense: the full IDEA mark at about 100x40, the class pads right after it, then a row of keys. The title-bar voice is "mono caps, regular weight, never bold" (brief section 5).
- **Fix (builder's choice, flagged):** (a) set `.brand-word` to `font-weight: 400; letter-spacing: var(--plate-title-track)` and grow `.emblem` to 40px. (b) Above 760px, consider moving the project pads into the header after the brand, where the site keeps its class pads. That fills the strip and frees a row in the main column. The pads would sit far from the folders they switch, though, so only do it if the Projects list is reworked to follow them. (c) Leave the band; it's the slab's screw band in the recipe, and shrinking it is a geometry call, not paint.

#### 15. Project pads are chunky, bold, and detached from the list they switch
- **Where:** "Robot 2027" and "IDEA 209 Bridge" pads, every Home shot. 44px-tall pads in the reading face at 600 (rendering 700) hug the left of a 798px column. In IDEA the selected pad's green ring and glow is the loudest object on the page, louder than anything in Needs you. Nothing connects the pad strip to the folders below; a 15px gap and a bold folder heading sit between them.
- **Site instead:** pads are compact, the lit one is a light face with a thin accent edge, and the list they control is directly attached.
- **Fix:** put the pads on the same line as the PROJECTS label (`display: flex; justify-content: space-between; align-items: end` on a label row), or seat them on the top edge of the project's card from item 6 with `margin-bottom: -1px`. Use `font-weight: 600; font-size: 0.875rem`. Leave the lit recipe alone: it's production's, and it's correct as "current".

#### 16. Needs-you sentences run 110 characters wide at 1280
- **Where:** `home-conflict-idea-1280x800`, "Maria saved Plate-Left.SLDPRT while you were offline, so your changes were kept as your own copy." is one line about 1000px wide.
- **Site instead:** card copy runs 30 to 45 characters a line.
- **Fix:** `.row-detail, .row-note { max-width: 62ch; }`.

#### 17. Counts are glued to their labels
- **Where:** "NEEDS YOU 2", "MY FILES 1", and on detail "HISTORY 5 SAVES" read as one phrase ("my files one").
- **Site instead:** "0 OF 3 DONE | 5 ITEMS" is set apart at the right of the header row.
- **Fix:** in app.js render counts as words ("1 file", "2 things", "5 saves") and push them right: `.section-label { display: flex; } .section-label .count { margin-left: auto; }`. Or separate them with " · ".

#### 18. Scrolling cuts the column off hard, and there is no position bar
- **Where:** `look/scrolled-1280-idea.png`. Rows slide under the header groove at the top, and the recess runs off under the slab's foot band at the bottom with no edge or tab. The scrollbar is a thin `--plate-hair` gray, and invisible in the renders.
- **Site instead:** a list well that scrolls keeps its frame and shows an accent position bar in its right gutter ("List well, the selected row and the position bar", `shapes-v3-detail-space-white-2x.png`).
- **Fix:** `.scroller { scrollbar-color: var(--plate-accent) transparent; }` to match the site's green bar (paint only). Better still, make `.main-col.plate-recess` the scrolling element at 761px and up (`overflow-y: auto; max-height: 100%`), so its edges, tabs and foot stay fixed and only the cards move. That pairs with the sticky side column in item 2.

#### 19. The narrow header is squeezed
- **Where:** `home-*-420x720`. Emblem, "ARMORY", OPEN FOLDER and SETTINGS fill the 420px row with 8px gaps, and the key tracking drops from 0.08em to 0.04em, so the caps look compressed next to the 1280 version.
- **Fix:** at 480px and under, hide `.brand-word` (keep the emblem with `aria-label="Armory"`) and restore the keys' tracking and padding. The keys keep their words.

#### 20. At 420 the status display takes 40% of the first screen
- **Where:** `home-conflict-*-420x720`. The header (77px) plus the STATUS label plus the display (204px) push the first Needs-you item to y=424 of 720.
- **Fix:** at 480px and under, put Pause on the same row as the headline (`display: grid; grid-template-columns: 1fr auto`), and trim `.display` padding to `0.9rem 1rem`. That should bring the first Needs-you item up by roughly the height of one key row.

#### 21. The display headline orphans its last word at 1280
- **Where:** `home-synced-*-1280x800` reads "Everything is saved to / Armory.", and `home-syncing` reads "Sending 2 changes to / Armory.".
- **Fix:** `text-wrap: balance` on `.sync-line` (item 4). Or widen `--side-w` to `minmax(18rem, 22.5rem)`; check the result on Windows, since the width depends on the face.

---

### The other screens (same review, shorter)

#### 22. File detail: the history's titles are the least useful words
- **Where:** `detail-lockedByOther-*`. The history reads "Saved / Saved / Saved / Saved / Added to Armory" in bold 15px, while who and when (the part a student scans for) is 13.6px gray beneath. Dividers are flat full-bleed rules (`.history-list > li + li { border-top }`), unlike the list's etched dividers that stop short of the edge.
- **Fix:** make author plus time the line in the reading face at 600, and put the event word and size in the mono meta voice. Keep the event word as the title only when it isn't "Saved", such as "Kept as your own copy: Maria saved first". Reuse the `.row` etched-divider background on `.history-list > li`.

#### 23. File detail says the state three times
- **Where:** `detail-lockedByOther-*`. A BEING EDITED chip beside the path, then a BEING EDITED readout, then "Maria Lopez is editing this".
- **Fix:** drop the chip beside the path on detail, since the display is the status.

#### 24. Connect stacks three headings and says "Connect this computer" three times
- **Where:** `connect-signedOut-*`. The header brand, then the title bar CONNECT THIS COMPUTER, then the card's own emblem plus ARMORY. Step 1 "Click Connect this computer." and the key repeat the title. The primary key is stretched to the card's full width, which reads like a web form; site keys are content-width and centered.
- **Fix:** remove the card's brand row (the title bar is the heading). Change step 1 to "Click the green button below." Set `.connect-actions { justify-content: center; } .connect-actions .key.primary { width: auto; padding-inline: 2rem; }`.

#### 25. Settings mixes control widths
- **Where:** `settings-synced-*`. The ON switch is an 88px key at the left, the three theme keys stretch full width, and the path well plus CHANGE fill the row. Three widths in three rows. The heading "Settings" is a 1.4rem bold web heading, while the site labels a housing in the label voice.
- **Fix:** give the theme keys content width (`grid-template-columns: repeat(3, auto); justify-content: start`), keeping the single column at 480px. Set `.sheet-title` to 600, or to the label voice in `--plate-caption`.

---

### Checked and already right (don't chase these)
- **Outer hairlines:** IDEA list-well hair `#656f68` on the recess `#0c0e0d` is 3.7:1. Space White well hair `#757c86` on the recess `#dadee6` is 3.1:1. Space White key edge on the slab is 3.3:1. All of them hold if item 1 changes the well's fill, because the hairline token and ground stay the same.
- **Chips:** no drop shadow, `cursor: default`, no hover. They are recessed by recipe. Item 1 fixes their *ground*, not their recipe.
- **Decoration:** no grid or ruled fill anywhere. Screws, hatch stacks and engraving are on the main slab only, and none sit on rows.
- **Controls:** every key, pad and row is 44px or taller in both sizes. The smallest visible control on Home measures 44px, and the shortest file row is 54px. The 41px row in the empty Needs-you well is plain text, not a control.
- **Production values:** the IDEA faces are darker than the mockup PNGs because idea-app production moved them down for ink contrast (brief 7.1). That is correct. Items 1 and 6 restore the mockup's depth by structure, not by lightening.

## Pass 2: fixes

Files changed: `src/Armory.Agent/wwwroot/app.css`, `app.js`, `index.html` (glyphs only). `bridge.js`, `demo/states.js` and `tools/agent-ui/` were not touched. Screens were re-rendered into `docs/agent/screens/` (64 images). Pass 1 copies of the three changed files are kept in `scratchpad/ui/app.pass1.{js,css}` and `index.pass1.html`. Scrolled review renders are in `scratchpad/ui/p2/`.

Every change is either paint or a length that is the same in both themes. No theme block gained a length, and no `[data-theme]` selector was added to a rule.

### Home

1. **No raised card layer.** Fixed. `.plate-recess .list-well` now uses the panel material (panel ground and gradient, the panel's top light line, the panel's three drop shadows), copied from plate.css 1093-1124 with production values. Nothing was lightened. The ladder now reads recess, then card, then key in both themes (the IDEA recess `#0c0e0d` holds cards `#1c1f1d`; the Space White recess `#dadee6` holds cards `#f5f7f9` to `#e2e6ec`). Chips on a card are now a shade darker than their ground, so they read as recessed tags. The card hairline still sits on the same recess ground, so its 3:1 is unchanged. The one number that moved: the Space White row divider measured against the card's darker stop is 3.02:1 (it was 3.07:1 against the old well). It still passes.

2. **The side column scrolled away.** Fixed differently than suggested, because it also fixes item 18. At 761px and wider, Home fills the window and only the recessed column scrolls, inside its own frame (`.main-col.plate-recess` is the frame, and the new `#recess-scroll` inside it is the scroller). The status display and This computer never move, so no bare strip is left beside a long list. A window too short for the side column still scrolls as a whole (the frame uses `contain: size`, so Home's height is the larger of the window and the side column). The DOM order stays status, lists, account, so Tab order matches what you see at every width. app.js keeps the recess scroll position across view updates, and also when opening a file, going Back and opening Settings, so a host update never throws the student back to the top.

3. **Chips that repeat the row.** Fixed. A saved file has no chip (the meta line already says "Saved by"). A new `noteBesideChip()` drops the sentences of a note that only restate the chip, using a per-status list in `STATUS[...].same`. "You're editing this." goes when the chip says YOU'RE EDITING, "Waiting to send." and "Kept as your own copy." and "Can't send this one." go the same way, leaving "It saves to Armory each time you save in SolidWorks.", "Saved on this computer.", "Nothing was lost.", "It was saved in SolidWorks 2026.". No new copy was written. A project row held by me no longer says "You're editing this" beside its YOU'RE EDITING chip; it shows its last save instead. The "SolidWorks version not checked" meta text was also dropped where the VERSION NOT CHECKED chip says it. Needs-you rows no longer carry chips at all: every title already says the state ("Kept as your own copy", "Can't send Bracket.SLDPRT"), so each row gets a kind glyph instead (item 9). `home-synced` goes from 16 chips (13 SAVED) to 3 on the whole page.

4. **One voice everywhere.** Fixed with three voices. Titles are the reading face at 1rem and 600 (`.row .row-name`, folder rows, project name, account email, sheet title, detail h1, holder line, history titles). Sentences are the reading face at 0.875rem and 400 (`.row-detail`, `.row-note`) in full ink. Meta lines are the mono label voice at 0.75rem with 0.02em tracking in ink-2 (`.row-meta`, `.hist-meta`, `.detail-path`), kept as typed, never uppercased, because they carry file and people's names (brief 7.8). Titles and the status line use `text-wrap: balance`; sentences use `text-wrap: pretty`. Every 700 became 600. The render host has only 400 and 700 faces, so 600 still renders bold in the PNGs; on Windows, Segoe UI has a real Semibold.

5. **The face.** (a) Done: `--font-display` is now `'Segoe UI Variable Text', 'Segoe UI', 'Liberation Sans', Arial, system-ui, sans-serif`, with a comment saying the two additions only serve review renders and that Windows never reaches them. The renders now use Liberation Sans instead of DejaVu Sans, which is closer to Segoe's width. (b) Not done, by design: Bahnschrift first for titles is a question for Mr. Pina, since the lane prompt names Segoe UI. Raise it with him; the change would be one token.

6. **A stack of wells instead of one list.** Fixed. Each project is now one card (`.list-well.project-card`, the tabpanel). Each folder starts with a header row inside the card (`h3.folder-row`): a folder glyph in the icon column, the name as typed at 600, the count pushed right in the label voice ("5 FILES"). The header is cut off from its rows by the same etched line the rows use (divider plus lip), drawn full width. Folders are spaced by one 0.75rem gap. That removes four outlines and the 8/15/17px spacing jumble.

7. **Brackets inside the recess.** Fixed: `.plate-recess .group::before { content: none; }` (paint only). The side column keeps its brackets on Status and This computer. I did not add a bracket set around the column's interior: the frame, its groove and its two tabs already mark the region, and brackets painted on a scroller would be covered by cards scrolling under them.

8. **Empty states drawn as boxes.** Fixed. When `needsMe` is empty, the Needs-you group is not rendered (the status display already says ALL SAVED). When `myFiles` is empty, its help is a `.group-help` line printed on the recess with no box (`home-releaseNotChecked`). No projects is a help line too. The empty-row markup and the `.empty` style are gone.

9. **Row anatomy misaligned.** Fixed. Every row now has the same grid (`20px minmax(0,1fr) 16px`), so Needs you, My files and the folder rows all start their names at one x (475 at 1280, 79 at 420, measured). Needs-you rows have kind glyphs, added to index.html: newer version (an arrow down into a tray), own copy (two sheets), can't send and name taken (a circle with a slash), version not checked (a question mark in a circle). Glyphs are centered on the row and drawn in the row's ink (`--plate-ink`). The assembly glyph was redrawn as two separate mated cubes, so it no longer reads as the one-cube part at 20px. The chip now sits at the right end of the title line (a `.row-head` flex line), and the chevron is centered.

10. **At 420 every chip got its own line.** Fixed. The chip stays on the title line at every width, and the note and meta lines run under it at the row's full width. The one exception is deliberate: when a file name is too long to sit beside its chip (`Plate-Left.SLDPRT` beside WAITING TO SEND at 420), the name keeps its line and the chip wraps under it at the left (flex-wrap with `space-between`). Before this, the name was broken into "Plate- / Left.SLDPRT". Most rows have no chip now (item 3), so this is rare. Time phrases never break ("20 minutes ago" is glued with no-break spaces), and each meta dot travels with the fact after it, so a wrapped meta line starts "· 20 minutes ago" and never ends on a dot. Home in `synced` at 420 is now 1926px tall (pass 1: 2438px).

11. **No vertical rhythm; columns 4px apart.** Fixed. `.status-group { margin-top: 22px }` equals the recess frame (3px) plus its scroll padding (19px), so at 1280 STATUS and the first recess label share their top (y=152), and the display and the first card share theirs (y=179), measured from the live page in `synced` and `conflict`. There is one between-groups gap everywhere (`.recess-scroll` gap 0.75rem and `.home` row-gap 0.75rem, so a group's 14px foot, the gap and the next group's 8px head make 34px in both columns), plus one label-to-content gap (the group's 0.6rem).

12. **Side-column controls left-aligned; a key inside the display.** Fixed. Pause (or Resume) is out of the display and sits centered under the housing (`.status-key`, `justify-self: center`). Sign out is centered under the account card. The display now holds only the readout and its words. On File detail, Show in folder moved out of the display in the same way and is centered under it.

13. **This computer on bare plate.** Fixed. The account is a `.panel` (1rem 1.25rem padding) holding a `<dl>`: SIGNED IN AS over the email, COMPUTER over LAB-PC-14, with the labels in the label voice. "Files in C:\IDEA\Armory" was dropped, because Settings owns it and Open folder serves it.

14. **Two near-empty strips at the top.** (a) Done: `.brand-word` is regular weight with `--plate-title-track`, and the emblem is 40px. Header height is unchanged, since the 44px keys set it. (b) Not done: moving the project pads into the header would put them far from the folders they switch, and item 15 attaches them to their list instead. (c) Kept: the screw band is the slab recipe's.

15. **Project pads chunky and detached.** Fixed. The pads sit on the PROJECTS label row, at its right end, directly above the card they switch (`.group-head`). They are the reading face at 0.875rem: 400 when unlit, and 600 for the lit (current) pad, so the current tab also has the heavier word that plate.css asks for. The lit recipe is untouched. With one project, its name sits on that row instead. All pads stay in the Tab order, and the arrow keys still move between them.

16. **110-character sentences.** Fixed: `.row-detail, .row-note { max-width: 62ch }`, and `.group-help` too.

17. **Counts glued to labels.** Fixed. Counts are words in the label voice, pushed to the right end of the label row: "NEEDS YOU ... 2 THINGS", "MY FILES ... 1 FILE", "HISTORY ... 5 SAVES" (`.section-label:has(.count)` and `.history-head` are flex rows with `.count { margin-left: auto }`).

18. **Hard cut on scroll, no position bar.** Fixed with item 2: in a wide window the recessed column scrolls inside its frame, its edges and both tabs hold still (the tabs are raised to z-index 1, so rows pass under them), and cards are clipped 3px inside the groove. `.scroller` and `.recess-scroll` both use `scrollbar-color: var(--plate-accent) transparent; scrollbar-width: thin`, which is the site's green position bar. Headless Chromium hides scrollbars, so the bar does not show in the PNGs; it shows in WebView2. `scrollbar-gutter: stable` keeps the cards from shifting when the bar appears.

19. **The narrow header was squeezed.** Fixed. At 480px and under, the brand word is visually hidden (it stays for screen readers), and the keys keep their full 0.08em tracking and 1.3rem padding (the narrow overrides were removed). The Connect screen has no header keys, so it keeps the word at every width.

20. **At 420 the status display took 40% of the screen.** Fixed. At 480px and under, Pause moves up into the STATUS label's row (right end), the display's padding is trimmed to 0.9rem 1rem, and the screen inset follows. In `home-conflict` at 420 the first Needs-you card now starts at y=383 (was 424). In `home-synced` the first card (My files) also starts at y=383, because the empty Needs-you box is gone (item 8).

21. **The headline orphaned "Armory."** Fixed: `text-wrap: balance` on `.sync-line`, with the narrower face from item 5. A number is also glued to the word after it (`glue()`), because balance alone had produced "Saved to Armory. 1 / thing needs a look." It now reads "Saved to Armory. / 1 thing needs a look.".

### The other screens

22. **History titles were the least useful words.** Fixed. A plain save ("Saved" or "Added to Armory") is titled with who and when at 600 ("Maria Lopez · 1 hour ago"), and the event word and size move to the mono meta line ("Saved · 598 KB"). A kept copy keeps its own sentence as the title ("Kept as your own copy: Maria saved first"), with who, when and size in the meta line, and it drops its now-redundant OWN COPY chip. CURRENT and VERSION NOT CHECKED chips sit at the right end of the title line. The flat full-width rules were replaced by the rows' etched divider (it fades in over 12px and stops 2px short of the edge). The history head is now an `h2` with its count on the right.

23. **Detail said the state three times.** Fixed: the chip beside the path is gone. The display's readout and its line remain (the state word, then who). The path line is now in the meta voice.

24. **Connect repeated itself.** Fixed. The card's emblem and ARMORY row are gone, so the title bar is the only heading. Step 1 now reads "Click the button below." (or "Click Try again below." after a failed sign-in). I avoided "the green button", because an instruction should not depend on color alone. The lead was reworded to "You only do this once. After that, ..." so the title's words are not repeated. The primary key is content-width and centered, with 2rem side padding.

25. **Settings mixed control widths.** Fixed. The theme keys are content-width and start on the left edge (`repeat(3, auto); justify-content: start`), and they keep a single full-width column at 480px and under. The sheet title is 600 at 1.2rem. The switch and the path row were already content-width and full-row; only the path field fills its row, as a field should.

### Not chased (pass 1 said they were already right)
Outer hairlines, chip recipe, decoration placement, 44px controls and production values were left alone. All still hold per check-ui.

### For Mr. Pina
- Bahnschrift (Windows 10 and later) as the title face, as the closest local match to Rajdhani (item 5b, brief 7.3).
- Optionally, the project pads in the header as idea-app does with class pads (item 14b). I kept them on the Projects label row instead.

### Script results
- `node tools/agent-ui/render-screens.mjs`: `SCREENS rendered=64 removed_stale=64 dir=docs/agent/screens index=docs/agent/screens/README.md`
- `node tools/agent-ui/bbox-diff.mjs`: `BBOX states=12 comparisons=32 differing=0 missing=0 elements=7814` (planted control: differing=235)
- `node tools/agent-ui/check-ui.mjs`: `CHECK-UI pages=64 controls=924 under44=0 network=0 grids=0 rowGrids=0 plantedGridLayersFound=3/3 rowDecoration=0 chipsLikeButtons=0 overflow=0 hairlines=572 hairlineMin=3.02 hairlineUnder3=0 tabStops=820 focusMissed=0 ringMin=4.69 ringFailures=0 jargon=0 flows=12 flowFailures=0 plantedDefectsCaught=6/6 emDash=0 files=10` and `CHECK-UI PASS`

## Pass 3: fresh review ranking

The ranking as the fresh reviewer wrote it, most visible first.

1. **Wrong typeface everywhere, so it doesn't look like the same product**
   - **Screenshots:** all, for example home-synced-idea/spaceWhite-1280x800, detail-*, settings-*.
   - **Problem:** File names, headings, body text, project tabs and the "Settings" title use a generic Arial-style sans. Labels use a generic monospace. The website uses a squared technical sans for titles and body (as in "Bracket redesign, test report") and a squared technical mono for labels and the LCD. This is the biggest single sign that the app and the site are different products.
   - **Visibility:** high.
   - **Fix:** Bundle the website's two font families in the app. Use the sans for all titles and body text, and the mono for uppercase labels, chips, buttons, tabs and the LCD.

2. **The brand is missing from the header**
   - **Screenshots:** every screen. It is worst at 420x720, where only a bare gear is left.
   - **Problem:** The header shows a small gear plus "ARMORY" in plain mono. The website leads with the large green IDEA gear-and-wordmark logo. A student won't connect this window to IDEA.
   - **Visibility:** high.
   - **Fix:** Use the website's IDEA wordmark at the same height (about 44px), then a thin divider and an "ARMORY" mono label. At 420 wide, keep the wordmark and shrink Open folder and Settings to icon buttons.

3. **There is no way to open a file**
   - **Screenshots:** home-* rows, detail-lockedByOther-*, detail-releaseNotChecked-*, detail-conflict-*.
   - **Problem:** The text keeps telling students to "Open it in SolidWorks to start" and "You can open it to look". The only button on every detail page is SHOW IN FOLDER, and file rows only have a chevron. The main thing a student wants to do has no control.
   - **Visibility:** high.
   - **Fix:** Copy the website's per-card OPEN pill. Put an OPEN button on each file row, and make "OPEN IN SOLIDWORKS" the green primary on the detail page ("OPEN TO LOOK" when someone else is editing). Make SHOW IN FOLDER a secondary button.

4. **"Needs you" items look like normal files**
   - **Screenshots:** home-conflict-*, home-refused-*, home-releaseNotChecked-*, in both themes and both sizes.
   - **Problem:** Problem items use the same neutral card, gray outline icon and chevron as healthy files. Only the small LCD on the left is amber. A first-time student won't see that something needs action, or know what to click.
   - **Visibility:** high.
   - **Fix:** Give each problem card an amber frame or left edge (red for "can't send") and a colored icon. Add an explicit labeled action on every card, such as "SEE BOTH COPIES", "SHOW IN FOLDER" or "HOW TO FIX". Pin the section under its own small amber LCD strip.

5. **The Connect screens are a small card floating in an empty window, with no welcome**
   - **Screenshots:** connect-signedOut/connecting/connectFailed/vaultOwnedByOther, at 1280x800 in both themes.
   - **Problem:** The card sits at the top and about 45% of the window below it is empty. This is the first thing a student ever sees. There is no logo moment and no plain sentence about what Armory is. The connecting state has no spinner or progress, so it looks frozen. The failure is a single line of red text.
   - **Visibility:** high.
   - **Fix:**
     - Center the content vertically inside the website's recessed tray with corner brackets.
     - Add the large IDEA Armory logo and one line: "Armory is your team's shared SolidWorks folder."
     - At 1280 wide, lay the three steps out as three website-style tiles.
     - While connecting, show an animated LCD plate ("WAITING FOR GOOGLE") and an "Open the browser again" link.
     - Show the failure in an amber or red LCD plate with an icon, not plain red text.

6. **Home, Detail and Settings have no title bar**
   - **Screenshots:** home-*, detail-*, settings-*.
   - **Problem:** The website's signature engraved title bar (hatched rails, spaced mono title) appears only on Connect. Detail uses a plain bold "Plate-Left.SLDPRT" heading and Settings a plain "Settings". The screens don't look like one family.
   - **Visibility:** medium-high.
   - **Fix:** Add the hatched-rail title bar under the header on every screen:
     - Home: "ROBOT 2027 · TEAM FILES"
     - Detail: "PLATE-LEFT.SLDPRT", with the project path under it
     - Settings sheet: "SETTINGS"

7. **The Detail page is flat, and "your copy" can't be found**
   - **Screenshots:** detail-conflict-*, detail-lockedByOther-*, detail-releaseNotChecked-*.
   - **Problem:** History is a plain bordered table with no recessed tray or brackets. About 120px is left empty at the bottom at 1280. The text says "Your copy is listed in the history", but the "Kept as your own copy" row is not highlighted. History rows have no actions, and "598 KB" means nothing to a student.
   - **Visibility:** medium-high.
   - **Fix:** Put history inside the recessed tray with brackets and notches. Give the student's copy an amber "YOUR COPY" chip and a tinted row. Add an OPEN button to each version. Drop the file sizes, or move them into a tooltip.

8. **The status column looks cheap next to the website's side column**
   - **Screenshots:** home-* at 1280, both themes.
   - **Problem:** The LCD text ("ALL SAVED") is thin and small. The website's "RETURNED: 18 / 20 PTS" is bold and glowing, and its plate has screws and a speaker grille. The column is text-only, with dead space under SIGN OUT.
   - **Visibility:** medium.
   - **Fix:** Make the LCD text bold with a soft glow, and add the two screws and dot grille to the plate. Fill the column with a ring gauge in the style of the website's "90% GRADE" ring, for example "3 of 3 saved", or a send progress ring while updating.

9. **PAUSE and SIGN OUT are too prominent and unclear**
   - **Screenshots:** home-* (all states), home-paused-*.
   - **Problem:** PAUSE sits right under the status and carries the same weight as the status. It doesn't say what it pauses, and it stays offered even while offline. When paused, RESUME is the same gray secondary button, so the one action that matters doesn't stand out. A large SIGN OUT on the main screen invites accidental clicks.
   - **Visibility:** medium.
   - **Fix:** Rename it "PAUSE SENDING". Make RESUME the green primary button while paused. Hide or disable Pause while offline. Move SIGN OUT into Settings or make it a small text link in the "This computer" card.

10. **The sections are unclear and the empty state looks unfinished**
    - **Screenshots:** home-releaseNotChecked-* (empty My Files), all home-*.
    - **Problem:** A student can't tell what "MY FILES" means compared with "PROJECTS". The same file shows in both. The empty state is a bare sentence with "C:\IDEA\Armory" as plain body text.
    - **Visibility:** medium.
    - **Fix:** Rename the sections to "FILES YOU'RE WORKING ON" and "TEAM FILES". Make the empty state a dashed recessed tile with an icon, the path in a mono chip, and an "OPEN ARMORY FOLDER" button.

11. **"YOU'RE EDITING" and "BEING EDITED" chips look the same**
    - **Screenshots:** home-synced-* vs home-lockedByOther-*.
    - **Problem:** Both are amber-dot chips. A student can't tell at a glance whether it's them or someone else, and the second case blocks saving.
    - **Visibility:** medium.
    - **Fix:** Show "YOU'RE EDITING" in green with the student's initials avatar, matching the website's "AN" circle. Show the other case as an amber lock chip, "MARIA IS EDITING", with her initials.

12. **The 420-wide layout is crowded and ragged**
    - **Screenshots:** home-*-420x720, detail-*-420x720.
    - **Problems:**
      - The status card plus PAUSE takes about 45% of the height, so only one or two files show.
      - Chip position jumps between rows, inline on Gearbox and wrapped under the title on Plate-Left and Intake-Gearbox (home-offline-420).
      - Text is cut mid-line at the bottom edge with no fade (home-lockedByOther-420).
      - Meta lines break inside the path, as in "Robot 2027 › / Drivetrain".
      - On detail-releaseNotChecked-420, History is pushed fully below the fold.
    - **Visibility:** medium.
    - **Fix:** Collapse the status into a one-line LCD strip with a small pause icon. Always put chips on a fixed line under the title. Add a bottom fade or scroll shadow. Keep the path on one line and truncate with an ellipsis.

13. **The 1280 file list hides overflow with no cue**
    - **Screenshots:** home-refused-*-1280 (only the top edge of the Projects card shows), home-conflict-*, home-lockedByOther-*.
    - **Problem:** Content is cut at the tray edge with no scrollbar or hint, so students won't know more files are below.
    - **Visibility:** medium.
    - **Fix:** Add a styled scroll rail inside the tray, matching the engraved side grips, plus a bottom fade or a "4 more files" hint.

14. **Rows and severity colors are inconsistent**
    - **Screenshots:** home-refused-*, detail-releaseNotChecked-*.
    - **Problems:**
      - One Needs-you row has a chevron and the next has SHOW IN FOLDER instead.
      - The second My Files row (Gearbox, can't send) has no chevron.
      - The LCD says amber "NEEDS YOU" while the chips say red "CAN'T SEND".
      - On detail, the LCD says green "FREE TO EDIT" while a gray warning box sits below the primary button.
    - **Visibility:** medium-low.
    - **Fix:**
      - Give every row a chevron, with action buttons as extras.
      - Use one severity scale (green ok, amber look, red blocked) for both the LCD and the chips.
      - Give the warning box an amber edge and place it above the button.

15. **The Settings sheet blends into the page**
    - **Screenshots:** settings-synced-* (all four).
    - **Problems:**
      - The dark scrim is weak, so the green project tab and chips glow through. The light scrim washes the page, so the dialog barely separates from it.
      - The title is plain "Settings" text.
      - The selected theme's green ring has broken dash notches that look like a rendering glitch.
      - "IDEA" and "SPACE WHITE" mean nothing to a student. At 420 they stack as three full-width buttons that look like actions, not a choice.
      - The path field looks editable.
    - **Visibility:** medium.
    - **Fix:**
      - Strengthen the scrim (dark at about 65% plus a blur; light with a darker tint) and use the engraved title bar for the sheet.
      - Make the theme a segmented control with dark and light swatches and "(dark)" or "(light)" sublabels.
      - Mark the selection like the website's selected tab: a clean green ring and a lit LED.
      - Show the path as a read-only mono plate.

16. **The "Choose a folder" screen is jargon for students**
    - **Screenshots:** connect-vaultOwnedByOther-*.
    - **Problem:** "Pick a different folder for your files" gives a student no idea which folder to pick. There is no warning visual. The two emails run into each other in the body text, and "sign out." is orphaned on its own line at 1280.
    - **Visibility:** medium.
    - **Fix:** Use an amber LCD headline: "THIS FOLDER BELONGS TO ALEX KIM". Offer a one-click suggested folder, for example "USE C:\IDEA\Armory-jordan". Show both accounts in labeled mono chips. Add "Not sure? Ask your teacher."

17. **Project tabs don't match the website's class tabs**
    - **Screenshots:** home-* (both themes).
    - **Problem:** The tabs are single-line bold sans, pushed to the far right away from the list they control. The website uses compact two-line mono tabs ("209H / P1·P1") with an LED.
    - **Visibility:** low-medium.
    - **Fix:** Use the website's tab style: mono, two lines (project name, then "6 FILES"), a green LED and ring when selected. Left-align them at the top of the tray.

18. **"OPEN FOLDER" doesn't say which folder**
    - **Screenshots:** header on home-*, detail-*.
    - **Problem:** The label is ambiguous for a student.
    - **Visibility:** low-medium.
    - **Fix:** Rename it "OPEN ARMORY FOLDER", with C:\IDEA\Armory as a tooltip.

19. **File types are unreadable**
    - **Screenshots:** home-* rows.
    - **Problem:** Students don't know that SLDASM is an assembly and SLDPRT is a part. The tiny cube and two-cube glyphs are hard to tell apart.
    - **Visibility:** low-medium.
    - **Fix:** Add a small "ASSEMBLY" or "PART" chip next to OPEN, copying the website's "ASSIGNMENT" and "MATERIAL" chips.

20. **The list styling is heavy and busy**
    - **Screenshots:** home-*-idea-1280 especially.
    - **Problem:** Dividers between rows are thick beveled double lines, and the "Main folder" and "Drivetrain" headers have even heavier underlines. Names and times are in sentence-case mono ("Saved by Alex Kim · 2 days ago"); the website keeps mono for small uppercase labels only.
    - **Visibility:** low.
    - **Fix:** Use single hairline dividers, or separate rows as spaced tiles like the website's cards. Set the meta line in the sans at a muted color, and keep mono for uppercase labels.

21. **Detail-page leftovers**
    - **Screenshots:** detail-*-1280.
    - **Problem:** The BACK button is oversized and sits alone on its own row. The left column ends early, leaving dead space.
    - **Visibility:** low.
    - **Fix:** Move Back into the title bar as a small chevron button. Let the left column hold the warning box and a "Who's editing" or version-check plate, so both columns end at the same height.

### Pass 3 fixes

Files changed in this pass: `src/Armory.Agent/wwwroot/app.css`, `app.js`, `index.html` and `bridge.js` (demo transport only), plus `tools/agent-ui/check-ui.mjs` (flows follow the new markup; two flows added). `demo/states.js` was not touched. Every change is paint or a length that is the same in both themes: no theme block gained a length, and no rule has a `[data-theme]` selector. The new severity names (`data-tone="ok | look | bad | off"`) and the lockup are colors and images only.

1. **Typeface. Partly fixed, inside the hard rule.** The site's two families (Rajdhani and Share Tech Mono) are web fonts idea-app loads from Google Fonts, and this window ships the system font stack only, so they can't be bundled. Each voice now takes the closest face Windows already has: `--font-display` leads with Bahnschrift (the squared DIN face in Windows 10 and 11, the nearest local match to Rajdhani), then Segoe UI; `--font-mono` is Cascadia Mono, then Consolas. The voices are now split the way the site splits them: the sans for every title and sentence, meta lines included (item 20), and mono only for uppercase labels, chips, keys, tabs and readouts (the project tabs moved to mono, item 17). **Still visible in the PNGs:** the review renders come from a Linux host that has neither Windows face, so they show Liberation Sans and DejaVu Sans Mono; the change shows only on Windows. If Mr. Pina wants the site's own faces, the system-fonts-only rule has to change to allow bundled font files.

2. **Brand. Fixed.** The header now leads with the IDEA lockup (the gear behind the green plate) at 44px tall, drawn from idea-app's own vector source (`tools/idea_logo_vector.py`), with the DARK palette in IDEA and the LIGHT (slate) palette in Space White, as `AnimatedLogo.svelte` switches them. (Correction, v2 pass: only the site's light PNGs are rendered from that tool; its dark PNGs are the original raster art, which the v2 pass now embeds in IDEA.) It is a data: URI in one token, `--brand-lockup`, so only the art changes with the theme and the box never does. Then a thin engraved rule and ARMORY in spaced mono. At 560px and under, Open Armory folder and Settings become 44 by 44 icon keys (the words stay for screen readers, and a tooltip says where each goes); the lockup and the name stay.

3. **Opening a file. Partly fixed.** BRIDGE.md has no message that opens a file in SolidWorks (its page-to-host list is fixed, and `AgentViewContractTests` holds the page to it), so an OPEN IN SOLIDWORKS key would promise something the host can't do. Within the contract: Show in folder (which opens File Explorer with the file selected) is the green primary on File detail, with the next step under it, "Double-click it there to open it in SolidWorks." When someone else is editing it says "Double-click it there to open it and look. To make changes, wait until Maria closes it, or ask Maria." Every row under Files you're working on carries its own SHOW IN FOLDER key (an icon key at 480px and under). The copy that promised a missing control ("Open it in SolidWorks to start", "You can open it to look") is gone. Team file rows open the file's page, where the key is. **Left:** a real Open in SolidWorks (and Open to look) needs a new bridge message, which is a host and contract change outside this lane.

4. **Needs you. Fixed.** Each item is a card with a 5px colored left edge (amber to look at, red when it can't be sent) and a faint tint of the same color, its glyph in a recessed disc of that color, the title, the sentence, a meta line starting with the kind tag, and labeled keys for the next step: SEE BOTH COPIES and SHOW IN FOLDER for a kept copy, SEE THE FILE for a newer version, SHOW IN FOLDER and SEE THE FILE for a file that can't be sent, SHOW IN FOLDER for a name that is taken, SEE THE FILE and SHOW IN FOLDER for a version that couldn't be checked. The section sits under its own small LCD strip: "2 THINGS TO LOOK AT" in amber, or "2 THINGS TO FIX" in red. (The strip does not repeat "Needs you", which the status display already says.)

5. **Connect. Fixed.** Under the title bar, a recessed tray fills the window and centers a bracketed working area: the large IDEA lockup over ARMORY, "Armory is your team's shared SolidWorks folder.", the one-time sentence, then the three steps as three tiles side by side at 1280 (stacked when narrow) with the current one lit, and the one key. While waiting there is an amber LCD plate, WAITING FOR GOOGLE, whose glyph breathes (only when motion is allowed; the renders use reduced motion, so it holds still in the PNGs), the host's sentence under it, and OPEN THE BROWSER AGAIN (it sends `connect` again) beside Cancel. Finishing shows a green GETTING YOUR FILES plate. A failure shows a red plate with the can't glyph, SIGN-IN DIDN'T FINISH, the host's sentence and TRY AGAIN. Once something is happening, the one-time sentence gives way (and, at 480px and under, the large lockup too, since the header shows it right above), so the plate and its key stay on the first screen. Signed out still shows exactly one key.

6. **Title bars. Fixed.** Every screen has the engraved title bar with hatched rails: HOME on Home, the file's name on File detail (Back sits in the bar; the kind tag and the folder path sit under it), SETTINGS on the sheet (Done sits in the bar), CONNECT THIS COMPUTER and CHOOSE YOUR FOLDER on Connect. The two ends of a bar are the same width, so the title stays centered. Home's title is HOME rather than "ROBOT 2027 · TEAM FILES", because Home shows every project at once and the project is picked lower down, by the tabs over the list it switches.

7. **File detail. Mostly fixed.** The history sits in a recessed tray with its two notches and corner brackets, and the tray runs as tall as the column beside it, so both columns end on one line. A kept copy gets an amber tag and a tinted row: YOUR COPY when it is the student's (matched by name), "Maria's copy" when it is someone else's. File sizes are gone from the lines; each entry's tooltip has its size. **Not done:** an OPEN key on each version, because the bridge has no message for opening a past version.

8. **Status column. Partly fixed.** The readout is bold mono with a two-layer glow, after RETURNED: 18 / 20 PTS. A ring gauge in the style of the site's GRADE ring (a raised bezel, a recessed track, the arc in the severity color, a dark glass center whose count glows) heads the This computer card: "18 of 20 team files are up to date on LAB-PC-14." It fills green when everything is current, and amber otherwise (green while updating). The column now ends with that card and no dead space. **Not done:** screws and a dot grille on the status display, because this window keeps screws and other decoration on its main plate only, and a dot grille is a repeating pattern the no-grid rule forbids.

9. **Pause and Sign out. Fixed.** The key says PAUSE SENDING and sits centered under the display. While paused, RESUME SENDING is the screen's green primary. While offline there is nothing to pause, so no key is shown. Sign out is a quiet underlined link, "Sign out of Armory", inside the This computer card (Settings may hold only its three settings, so it could not move there). It is still a 44px target with the focus ring.

10. **Sections and the empty state. Fixed.** The sections are FILES YOU'RE WORKING ON and TEAM FILES. An empty list is a dashed recessed tile with a glyph, one sentence ("Nothing right now. Files you open and change in SolidWorks show up here until they're saved to Armory."), the folder in a mono plate, and an OPEN ARMORY FOLDER key.

11. **Editing tags. Fixed.** YOU'RE EDITING carries the student's initials in a green disc (JR); someone else's reads MARIA IS EDITING with her initials in an amber disc (ML), after the site's AN avatar. No padlock, and never the word "lock". The discs are flat and the tags stay recessed, so they still don't look like keys.

12. **The 420 layout. Fixed.** At 760px and under, the status collapses to one lit strip with a 44px pause icon key beside it and the sentence under it (the display's housing is not drawn there). The tags always start the line under the name, on every row. A fade and a "15 more files below" tag sit at the foot of whichever region has more to scroll. Meta lines stay on one line and end in an ellipsis, and a folder path is one unbreakable piece, so "Robot 2027 › / Drivetrain" can't happen. On File detail the display tightens and Who's editing moves under the history, so the history starts on the first screen (detail-releaseNotChecked-420 shows its first entry).

13. **Overflow at 1280. Fixed with a cue, not a rail.** The tray's foot fades and a recessed tag counts what is out of sight ("12 more files below"); both go when the end is reached. The tray keeps its thin green position bar, which WebView2 shows (headless Chromium hides scrollbars, so it is not in the PNGs). **Not done:** a second, drawn scroll rail, which would duplicate the real one.

14. **Consistency. Fixed.** Every file row ends in one glyph that says where a click goes: a chevron to the file's page, or a folder-out glyph for a file that isn't in Armory yet (that row shows it in its folder). Extra keys sit before the glyph. Every Needs-you card uses labeled keys and no chevron. One severity scale runs everywhere (green ok, amber look, red blocked, gray off): the status readout turns red CAN'T SEND when everything waiting is blocked, matching the red tags; a file whose version wasn't checked reads amber VERSION NOT CHECKED, never green FREE TO EDIT; the warning box has an amber edge and sits above Show in folder.

15. **Settings. Fixed.** The scrim is 66% black plus a 3px blur in IDEA, and a slate veil plus the blur in Space White, where the sheet's edge darkens with it (`--sheet-hair`) to hold 3:1 against the dimmed page. The sheet has the engraved title bar with Done. The theme is one choice of three pads, each with a swatch of its plate and a word under its name (Follows Windows, Dark, Light); the picked one is lit like the site's selected tab, with the clean glowing ring and the green LED, plus aria-pressed. The broken, ticked ring recipe is gone. The folder shows as a read-only mono plate with a folder glyph, not a field.

16. **Choose a folder. Fixed.** An amber LCD headline, THIS FOLDER BELONGS TO ALEX KIM (the name is read from the owner's address in the host's sentence; without one it says "someone else"), one plain sentence about why, both accounts in labeled mono plates (THIS FOLDER BELONGS TO, YOU'RE SIGNED IN AS), a one-click USE C:\IDEA\Armory-jordan primary (it sends `saveSettings` with that folder, the same message Settings uses; the demo then goes on to Home), CHOOSE ANOTHER FOLDER, and "Not sure? Ask your teacher. Not you? Sign out".

17. **Project tabs. Fixed.** Mono, two lines (ROBOT 2027, then 15 FILES), the LED and the lit ring on the current one, which also has the heavier word; left-aligned under the TEAM FILES label, right over the card they switch.

18. **Open folder. Fixed.** OPEN ARMORY FOLDER, with the tooltip "Open C:\IDEA\Armory in File Explorer".

19. **File types. Fixed.** Every file row, Needs-you card and file page carries a PART, ASSEMBLY or DRAWING tag at the start of the line under the name, after the site's ASSIGNMENT and MATERIAL tags.

20. **List weight. Fixed.** Rows are divided by one hairline (still the 3:1 load-bearing divider, drawn once instead of doubled). Folder headers are small mono caps labels with no rule under them. Meta lines ("Saved by Alex Kim · 2 days ago") are in the sans, in the muted ink.

21. **Detail leftovers. Fixed.** Back is a 44px chevron key inside the title bar. The left column holds the display, any warning, the action and a Who's editing card (the person's initials, name, computer and how long, or "Nobody right now"), and the history tray stretches to end with it.

#### Left for later

- The site's own typefaces (item 1): needs the system-fonts-only rule changed to allow bundled font files. Bahnschrift and Cascadia Mono stand in on Windows; the Linux review renders can't show either.
- Open in SolidWorks, Open to look, and Open on a past version (items 3 and 7): need new page-to-host messages in BRIDGE.md and the host. Show in folder is the closest action the contract has.
- Screws and a dot grille on the status display (item 8): kept off by the window's decoration and no-grid rules.
- A drawn scroll rail (item 13): the real position bar shows in WebView2; the PNGs get the fade and the count instead.

## Pass 4: adversarial check against the hard rules

Files changed in this pass: `src/Armory.Agent/wwwroot/app.css`, `app.js`, `demo/states.js` (one label), `tools/agent-ui/check-ui.mjs`, the screens (re-rendered) and this file. `index.html` and `bridge.js` were read and needed nothing. Every change is paint, stacking, words or a length that is the same in both themes; no theme block gained a length.

### Breaches found and fixed

1. **The reading face was not the system stack the lane names.** Pass 3 led `--font-display` with Bahnschrift. The lane allows the system stack only, Segoe UI Variable then Segoe UI on Windows, unless idea-app's tokens name a local stack, and they don't (idea-app `src/lib/design-system/typography.css` 12-17 names Rajdhani and Share Tech Mono, both web fonts). Pass 1 (item 5b) had said this needed Mr. Pina's say before anyone did it. Fixed: `--font-display` is now `'Segoe UI Variable Text', 'Segoe UI Variable', 'Segoe UI'`, then the review-render fallbacks. The label voice stays Cascadia Mono, then Consolas, both shipped with Windows. If Mr. Pina wants Bahnschrift for titles, it is a one-token change once he says so.
2. **Lists in the recessed column were raised.** Pass 1 (item 1) gave `.plate-recess .list-well` the panel material with three drop shadows. Standards 14(b) and plate.css 42-47 name "a chip, a well, a list and the recessed column" as set into the surface, never raised, and the brief's own list recipe (3.11) is the inset well. A raised list reads as one big key; only its rows are pressable. Fixed: the override is gone, so My files, Team files and History take the inset well recipe (plate.css 1234-1246) wherever they sit. Cards that hold words and keys (Needs-you cards, the status display, This computer, Who's editing, the Connect steps) stay raised panels, as plate.css 1093-1111 raises the site's cards; none of them is a list. The lowest load-bearing hairline moved from 3.02:1 to 3.07:1.
3. **A chip in a row showed the pointer hand.** The row's invisible key (`.row-hit`) lies over the whole row, so the mouse met the key, not the chip, and got its hand cursor, though the chip's own style said arrow. check-ui read only the chip's own style, so it passed. Fixed: a chip in a row sits above the row's key (`position: relative; z-index: 2`), keeps the arrow, and a click on it is handed to the row's key in app.js, so the whole row still opens the file. check-ui now asks `elementFromPoint` what is under each chip's center and fails a pointer cursor from anything laid over it; a planted chip dropped back under its row key is caught; and a new flow (both sizes) clicks a chip and expects the file's page, scrolled to the top, heading focused. Each row now isolates its own stacking (`isolation: isolate`), so the raised chips and the row's SHOW IN FOLDER key stay under the recessed column's tabs and the "more below" fade; before this pass that key (already `z-index: 2`) painted over both, crisp on top of the fade at the foot of the column.
4. **Hatch on the Settings sheet.** The sheet's title bar carried the hazard hatch, and decoration belongs on the window's main plate only. Fixed: `dialog .plate-title::before/::after` draw nothing (they were absolutely placed, so no box moved). check-ui now fails screws, hatch, rails or engraving anywhere inside the open sheet, a card or a list, with a planted rail on a card to prove it looks.
5. **"My files" was called "Files you're working on".** The lane and BRIDGE.md name the section My files. Fixed: the label is MY FILES.
6. **Two AgentView fields went unread.** `sync.pendingCount`: the status display now says "3 changes are waiting to send." when the host sends a count and no detail sentence, so a view with no sentence still says what is waiting. `Holder.email`: Who's editing on File detail now shows the other person's school address under their computer, so "ask Maria" comes with how to reach her (never for the student's own holds).
7. **The jargon sweep had gaps.** It checked "sync conflict" but not "conflict" or "sync" alone, and not "upload" or "download". Fixed: the list is lock, unlock, conflict, sync, journal, side version, intent, RPC, hash, vault, upload, download, each with a self-test that it catches its word. No page uses any of them. One demo label, "Uploads Armory can't take", only ever shown in `docs/agent/screens/README.md`, now reads "Files Armory can't send, with the reason".
8. **Nothing proved the shipped files name no web address.** check-ui now reads every html, css and js file in wwwroot and fails any web address, `@font-face`, `@import` or `url()` that is not `data:` or an in-page `#id`. The one http string in the folder is the SVG namespace inside the data: URI images (the lockup, the hatch, the switch glyphs): an identifier a data: SVG must carry to parse, which no browser fetches. A planted import, font and address are all caught. The em dash sweep now covers this file too.
9. **Nothing drove the bridge as WebView2 would.** The flows all ran on the demo transport. check-ui now loads the page under a stand-in `window.chrome.webview` and checks: `ready` is the first message, the demo states never load, Home, File detail and Connect render from host messages alone, `effectiveTheme` is worn, a `fileDetail` for another file and an unknown message type are ignored, and each of the 11 page-to-host types (`ready`, `connect`, `cancelConnect`, `signOut`, `pause`, `resume`, `openVault`, `openFile`, `showInFolder`, `saveSettings`, `chooseVaultRoot`) is sent by the control that should send it with exactly the fields BRIDGE.md lists. It also checks the plain-browser theme pick: `?theme=idea`, `spaceWhite` or `space-white`, else `prefers-color-scheme`. All pass.

### Checked and already right

One geometry (zero differing boxes, and no length in either theme block); the 44px rule on every control, row and setting; 3:1 hairlines; Tab reaching every control with a ring of 4.69:1 or more; no sideways scroll or spilled words at 420; no grid; nothing from the network; Connect with exactly one key before the first click; the Settings sheet with exactly the folder and its Change key, Start at sign-in (on) and the three-way theme, plus Done; File detail with its history, who is editing and Show in folder; every required state present in `demo/states.js` and rendered on the screen that shows it best; a row click moving to the file's page at the top with its heading focused.

### Judgment calls left for Mr. Pina

- At 560px and under, Open Armory folder and Settings are icon keys (a folder and sliders), with their words kept for screen readers and in a tooltip. A student who has never seen the window has to guess them at the narrow size.
- Home exists only after a computer is connected, so `signedOut`, `connecting`, `connectFailed` and `vaultOwnedByOther` render on Connect, not Home; the eight signed-in states (`synced`, `syncing`, `offline`, `paused`, `conflict`, `refused`, `lockedByOther`, `releaseNotChecked`) all render Home, and three of them File detail too.
- Raised cards that are not themselves pressable (a Needs-you card, a Connect step) follow the site's panel rule. If "raised means pressable" is meant to cover cards as well, every card would become an inset panel, which the site does not do either.

## Proof

Run on this pass's final files, in this order, from the repository root:

```
node tools/agent-ui/render-screens.mjs
node tools/agent-ui/bbox-diff.mjs
node tools/agent-ui/check-ui.mjs
```

```
SCREENS rendered=64 removed_stale=64 dir=docs/agent/screens index=docs/agent/screens/README.md
BBOX planted control (home-synced-1280x800, one length in Space White): differing=288 (must be above 0)
BBOX states=12 comparisons=32 differing=0 missing=0 elements=9096
CHECK-UI pages=64 controls=992 under44=0 network=0 grids=0 rowGrids=0 plantedGridLayersFound=3/3 rowDecoration=0 chipsLikeButtons=0 overflow=0 hairlines=618 hairlineMin=3.07 hairlineUnder3=0 tabStops=884 focusMissed=0 ringMin=4.69 ringFailures=0 jargon=0 offline=0 plantedOfflineFound=5/5 flows=18 flowFailures=0 bridgeTypes=11/11 bridgeFailures=0 plantedDefectsCaught=8/8 emDash=0 files=11
CHECK-UI PASS
```

What the lines say: all 64 screens were rendered fresh; across 32 comparisons of 12 states (every screen, both window sizes) no element box differs between IDEA and Space White and none is missing, while one length planted in Space White moves 288 boxes, so the diff can see a difference; and every hard rule holds on all 64 pages: no control under 44px, nothing from the network and no web address, font or import in the shipped files, no grid, no decoration on a row or off the main plate, no chip that looks like a key or shows a pointer hand, nothing scrolling sideways or spilling, every load-bearing hairline at 3:1 or more (lowest 3.07), every control reached by Tab with a ring of at least 4.69:1, no jargon, no em dash, all 18 flows passing, all 11 page-to-host message types sent correctly to a stand-in WebView2 host, and all eight planted defects caught.

Changes to the checker in pass 4, none of which loosen a check: the jargon list gained conflict, sync, upload and download, with a self-test per word; a static offline sweep of wwwroot (web addresses, `@font-face`, `@import`, `url()`), with five planted problems; the chip check reads what the mouse meets over each chip, with a planted covered chip; a decoration check off the main plate (the open sheet, cards, lists), with a planted rail; a chip-click flow at both sizes (16 flows became 18), and the row flow also checks the holder's address; the stand-in WebView2 bridge check and the plain-browser theme pick; and the em dash sweep now covers this file. Pass 3's checker changes are described in pass 3.

## v2 pass

Mr. Pina's feedback of 2026-10-06 (two students testing) turned the window from a status
page into the place where files are opened, checked out, checked in, moved and added. This
pass built that window to the v2 design (`v2-design.md` sections 4.4, 4.5 and 4.6, decisions
D5, D12, D13 and D14) inside the same Plate language: both themes, one geometry, 44px
targets, no new fonts and nothing from the network. The v2 screens are in
`docs/agent/screens/v2/` (132 images, listed in its README); the 64 v1 images one folder up
were not touched.

Files changed: `src/Armory.Agent/wwwroot/` (all five files), the `BridgeMessages` lists in
`src/Armory.Agent.Engine/View/AgentView.cs` (constants and lists only), `tools/agent-ui/`
(all four), `docs/agent/BRIDGE.md` (rewritten for v2) and this file.

### What changed

1. **The IDEA mark turns, as the site's does (D12).** The lockup is now built the way
   `AnimatedLogo.svelte` builds it: a box with the emblem's 2560 by 1204 proportions, the
   gear layer behind the plate layer, the gear at left 0, top -1.2%, 46.95% of the width,
   turning about its own center once every 24 seconds, linear, forever. It turns only under
   `prefers-reduced-motion: no-preference`; with reduced motion it is the still gear, and
   nothing is ever hidden or faded in the base state. Both layers are pseudo-elements, so
   the span keeps its one box and bbox-diff still sees no difference between the themes.
   IDEA shows the site's own dark rasters (`idea-gear-120.png` and `idea-logo-text-256.png`
   in the header, the 240 and 512 copies in the Connect hero), embedded as data: URIs;
   Space White shows the vector SLATE art, split into its gear and plate. The duration lives
   in app.css because the page's CSP refuses inline style (the site sets it inline), and
   the gear stops while the window is hidden. This is the one shape in the window that
   moves; the rule that only color and shadow ease still holds everywhere else, and app.css
   says so where the rule is written. Pass 3 item 2 above said the site's PNGs are rendered
   from `tools/idea_logo_vector.py`; only the light pair is (the dark pair is the original
   painted art), and both that sentence and the old app.css comment are corrected.
2. **Check out, in the words both lanes use (D5).** Check out, Check in, Undo check out,
   Take back, Open, Show in folder. Every file row says who has it, always: "Checked out by
   you" (a green tag with the student's initials), "Checked out by Maria Lopez on
   LAB-PC-07" (amber, her initials), or plain "Available" (never a chip, as pass 2 item 3
   asked: a state worth a tag gets one, the ordinary state does not). File detail leads
   with a green Open, then Check out and Check out and open, or Check in and Undo check
   out, or Take back for a mentor or CAD lead (after a small dialog that says the holder's
   changes are kept in the history), with Show in folder as a quiet link. Every "Double-click
   it there..." sentence is gone, and so is "The first person to open it in SolidWorks gets
   to edit it", which v2 made false.
3. **The quiet check-out question (D13).** When SolidWorks opens a file this computer has
   not checked out, one slim card heads the recessed column: "Check out Plate-Left.SLDPRT
   to edit it?" with Check out and Not now. When someone else has it, it says who:
   "Plate-Left.SLDPRT is checked out by Maria Lopez on LAB-PC-07. You can look, but you
   can't save changes." It never takes focus from SolidWorks; the tray balloon when the
   window is hidden is the host's part.
4. **Right now.** What is moving is its own panel at the top of the recessed column, never
   rows: each direction (Uploading, Downloading, Moving) with its count ("412 of 1,280
   files"), what is left, the speed and the time left, and a progress track; each file
   moving now with its own track; and how many files wait ("3 files are waiting to upload.
   They upload when this computer is back online."). While files move, the status display's
   line is the engine's activity line ("Downloading 412 of 1,280 files, 2.1 GB left, about
   3 min"). The host's `activity` message (four a second at most) patches only this panel
   and that line, so focus, scroll and typed words never move. The progress track is a new
   recipe: an inset groove on `--plate-track` with the 3:1 hairline, filled in the ok lamp's
   color, no stripes and no easing, its width set through CSSOM.
5. **Notices, one card per kind.** "14 files share a name with other files in this project"
   is one card with one action and a key that opens the list of those files inside the
   card, never 14 rows. The strip over them counts cards. "SolidWorks year not checked" is
   never a notice: it is a small tag on File detail only. An unzip or a Pack and Go is one
   import summary card ("Added 4,987 of 5,000 files to Robot 2027 > CopyDesignTemp").
6. **The file browser.** Team files is now a browser: the project tabs, then where you are
   (Robot 2027 › Drivetrain › Gearbox, the earlier steps quiet links), the folder's keys
   (New folder, Add files, Rename folder, Delete folder, Check out all, Check in all; icon
   keys with their words for screen readers at 560px and under), its folders (with how many
   files and folders are under each), then its files. Each file row has a 44px select key
   (`role="checkbox"`); while files are selected a bar stays at the top of the column with
   the count and Check out, Check in, Undo check out, Take back (mentors and CAD leads) and
   Clear; Shift selects a range and Escape lets go. Files dragged from File Explorer wear
   the focus ring and say "Drop to add to Robot 2027 › Intake". An archived project is
   listed and says so, with no keys.
7. **Long lists.** A folder, My files and a notice's list draw only the rows near the view,
   at one fixed row height (64px; 84px at 560px and under, where the line under a name may
   take two lines), with spacers whose heights are set through CSSOM, about 30 rows each
   side, the focused row and the row Back returns to always drawn. Tab enters a list on one
   row; the arrow keys, Home, End and the page keys move between rows. "4,995 more files
   below" is counted from the data. The 5,000-file demo folder keeps 37 rows in the page at
   1280x800.
8. **The small dialog.** New folder, Rename folder, Delete folder and Take back ask in a
   second housing that is filled once and never redrawn by a host message. Its name field
   is a new recipe, the inset field: 44px, the 3:1 hairline, the focus ring. A bad name is
   refused in plain words before anything is sent. Delete folder says how many files go and
   that their history is kept. A question that removes something starts on Cancel.
9. **The host's answer to an action** shows as a quiet tag at the window's foot ("Checked
   out 2 files."), read out by a screen reader, fading after 8 seconds; never an alert and
   never a focus change.

### Checker changes (none loosen a rule, except the jargon list as D5 decided)

- The jargon list no longer bans upload and download (D5: "Uploading", "Downloading" and
  "Moving" are Mr. Pina's own words). Lock, unlock, conflict, sync, journal, side version,
  intent, RPC, hash and vault stay banned, each with its self-test, and a new self-test
  proves the list lets the v2 sentences through.
- The hairline check covers the text field and the progress track.
- The em dash sweep covers `docs/agent/screens/v2/README.md`, `docs/agent/BRIDGE.md` and
  `docs/overnight/`, and fails if the v2 index or BRIDGE.md is missing.
- New flows (both sizes): every file row says who has it; a notice's list opens and closes
  and no kind has two cards; folders in, deeper and back out by the crumbs; select (with
  Shift) and Check out, the result line, Escape; the folder dialog refuses a slash, renames
  on Enter, follows the folder, and Delete names its 8 files and the kept history; the
  5,000-file folder keeps under 150 rows, reaches its last file by scrolling and by End, and
  has one row in the Tab order. The logo probe: idea-gear-spin, 24s, linear, infinite under
  no-preference, none under reduce, both layers painted, the gear 46.95% of the width, with
  a planted 3s gear caught.
- The stand-in WebView2 bridge check sends all 22 page-to-host types with exactly their
  fields (an action's `requestId` included), sends a drop through
  `postMessageWithAdditionalObjects` with its files, checks that an `activity` message keeps
  focus and the focused row's place on screen and patches its numbers, that an
  `actionResult` is a quiet line, and that a host view arriving while the folder dialog is
  open keeps the typed name. The summary counts types from the contract, not a fixed 11.
- render-screens writes only into `docs/agent/screens/v2/` (it refuses any other folder) and
  each state can name a page-only place (folder, selected files, an open list, a dialog,
  files held over the list, Home scrolled to Team files) that bridge.js reads from the
  query string.

### Decisions taken in this pass

- **"Archived. It no longer updates."** D8 gives the archived line as "Archived. It no
  longer syncs.", but the same brief keeps "sync" on the banned list (D5). The window says
  "updates"; the meaning is the same and the checker stays as strict as it was.
- **Right now sits at the top of the recessed column**, not in the side column the audit
  suggested: three directions and eight file tracks do not fit beside the status display in
  an 800px window without pushing This computer off it. The status line, which stays in
  sight, carries the activity line.
- **A file that isn't in Armory** shows "Not in Armory" where the check out would be, and
  has no select key: nothing can be checked out or in until it is added, and "Available"
  would promise a check out the host would refuse.
- **Notice tones** map `info` to the green (ok) edge, so the scale stays the four it was.
- **Not now** sends nothing (there is no message for it in the contract); the page hides
  that question until the host asks about another file. With someone else holding the file
  the key says OK.
- **Take back asks first**, in the small dialog, before it sends `takeBack`.
- The demo's subfolder is "Drivetrain/Gearbox", so "Gearbox was put back" and "Moving 120
  files to Gearbox" refer to a folder the browser shows.

### Not done

- The window was checked against the demo transport and a stand-in WebView2 only. The real
  host's side of the new messages (`Bridge.cs`, `MainWindow` reading `AdditionalObjects`,
  the tray balloon for the question) is being built in another lane against the same
  contract; nothing here proves the two meet until the engine sends real views.
- The 420x720 renders show a layout the real window cannot reach (its minimum size is
  720x520). They are kept because the brief asks for them.
- The demo's sentences follow v2-design.md where it gives the engine's words; where it
  does not (a check-out result for several files, a folder made), the demo's words are this
  pass's guess at the engine's, and the engine lane may word them differently.

### Proof

```
node tools/agent-ui/render-screens.mjs
node tools/agent-ui/bbox-diff.mjs
node tools/agent-ui/check-ui.mjs
```

```
SCREENS rendered=132 removed_stale=132 dir=docs/agent/screens/v2 index=docs/agent/screens/v2/README.md
BBOX planted control (home-synced-1280x800, one length in Space White): differing=179 (must be above 0)
BBOX states=27 comparisons=66 differing=0 missing=0 elements=23188
CHECK-UI pages=132 controls=3376 under44=0 network=0 grids=0 rowGrids=0 plantedGridLayersFound=3/3 rowDecoration=0 chipsLikeButtons=0 overflow=0 hairlines=2982 hairlineMin=3.07 hairlineUnder3=0 tabStops=1476 focusMissed=0 ringMin=4.69 ringFailures=0 jargon=0 offline=0 plantedOfflineFound=5/5 flows=28 flowFailures=0 logo=4 logoFailures=0 plantedLogoFound=1/1 bridgeTypes=22/22 bridgeFailures=0 plantedDefectsCaught=8/8 emDash=0 files=16
CHECK-UI PASS
```

## v2 review pass

A fresh-eyes reviewer looked at the v2 window (the 132 renders, the page's code and the
host's side) and listed 21 problems, 3 high and 6 medium. The design was then settled
for each in `v2-design.md` section 7. This pass fixed every high and medium item and
the cheap low ones, applied section 7, rendered every screen again (168 images now, 42
screens and states) and looked at them. Each item says what was done. Where this pass
differs from the v2 pass above (Pause sending, Not now sending nothing, My files holding
files that aren't in Armory, the import title's ">"), this pass is current.

Files changed: `src/Armory.Agent/wwwroot/` (app.js, app.css, bridge.js,
demo/states.js), `src/Armory.Agent.Engine/View/AgentView.cs` (`PromptView.Key`, the
`renameFile` message), `src/Armory.Agent/Bridge.cs` and `AgentHost.cs` (the
`renameFile` case and call), `tests/Armory.Agent.Engine.Tests/EngineUnitTests.cs`,
`tools/agent-ui/check-ui.mjs` and `render-screens.mjs`, `docs/agent/BRIDGE.md`, this
file and the renders.

### High

1. **The page was built to the v2 view, the host side still v1.** Partly this lane's
   to fix, partly the engine lane's, as expected. Done here: `Bridge.cs` has one case
   and one message record per page-to-host type, `renameFile` included, each record
   reading exactly the fields bridge.js sends, and every action is answered with one
   `actionResult` (committed just before this pass, with four guards in
   `tests/GUARDS.txt`). `PromptView` gained `Key` on both sides. What was missing was
   the link from the demo to the host: check-ui now holds every demo view and file
   detail to the JSDoc typedefs in bridge.js (exact fields, and only the words a union
   such as `FileStatus` allows; a planted extra field, missing field and v1 status word
   are caught), and `AgentViewContractTests` holds the C# records to the same typedefs,
   so a field the demo invents fails one side or the other.
   `EngineUnitTests.The_view_serializes_with_the_bridge_field_names` was changed (not
   removed) to check the question's serialized names too. The page also no longer
   throws on a view that leaves `checkout` out: a row without one reads as Available,
   so a v1 view from the current engine draws Home (checked with a v1-shaped view
   through a stand-in WebView2) instead of a blank window. **Left to the engine lane:**
   `AgentView`'s own v1 records (NeedsMe, `Holder`, the v1 `FileStatuses` names); the
   guard `The_host_view_records_have_the_fields_bridge_js_documents` names exactly the
   eight records allowed to differ until then and fails once they no longer need to.
   Reading `AdditionalObjects` in `MainWindow` is the Windows lane's.
2. **Check out all took a whole project in one click.** Fixed. It asks first, in the
   small dialog, with the count and what it means: "Check out 16 files in Robot 2027
   and its folders? Nobody else can save them until you check them in. 1 other file is
   checked out by someone else, and stays with them." The question starts on Cancel
   (Enter cancels) and its key is not the green primary. A project's top folder is
   allowed only through it (section 7). New render: `home-checkOutAll`.
3. **Per-file noise in My files.** Fixed as section 7 settles it: My files is the files
   this computer has checked out, in every project, an archived one too, and nothing
   else. A file that isn't in Armory is never a row there; the notices say what became
   of it, and it sits in its own folder in Team files ("Not in Armory", or "New, not
   uploaded yet" for a new file that is only waiting). There is no Waiting to upload
   tag on any row: how many wait is said once, by Right now, and the status display no
   longer repeats it while Right now does. The import summary lists no files (the name
   card lists the 13), so no file is named twice.

### Medium

4. **No recognizable way to check out.** Fixed. Every file row has one state key: Check
   out when nobody has it, Check in when it is checked out here, nothing when someone
   else has it (the row says who). It stands before Open, at one width, so Open keeps
   one column down a list; at 560px and under both are icon keys with their words for
   screen readers and in the tooltip. The select key always draws its box: an empty
   recessed square, ticked and filled when picked.
5. **The question's words were not true of SolidWorks.** Fixed with section 7's words:
   "SolidWorks opened it read-only. Check it out, then close it in SolidWorks and open
   it again here to save changes." Its key is Check out and reopen (`checkOut` with
   `open: true`); the host asks for the file to be closed first while SolidWorks still
   has it. File detail keeps Check out and open. **Not measured:** whether SolidWorks
   saves once the read-only bit is cleared under an open document needs the Windows
   machine; the words hold either way.
6. **The check out line could be cut.** Fixed. Who has it comes first on the line, then
   the state tag, the kind tag and the last check in. When the line is short of room
   the last check in goes first, then the computer's name (ending in an ellipsis), and
   the person's name last; the whole label is the tag's tooltip. Two sub-pixel cuts
   ("Checked out by y...") were found and closed while doing it. The reviewer's names
   ("Alexandra Montgomery-Whitfield on ENGINEERING-LAB-PC-27" beside Your copy kept,
   "Maria Lopez on DESKTOP-7F3K2LQ" beside Newer version waiting) were drawn through a
   stand-in host at both sizes: the person and the state tag stay, the computer gives
   way.
7. **The demo contradicted itself.** Fixed; every number on a screen now agrees.
   `transferring`: a 1,276-file folder plus four changed team files make the 1,280
   being downloaded, 412 of them here, so the ring reads 431 of 1,299; three files go
   up, one done, too soon for a time left. `importSummary`: 4,987 added files and the
   13 that share a name make 5,000 in the folder, and the ring reads all 5,010.
   `offlineWaiting`: three files wait (two check outs with saves and a new file);
   `pausedWaiting`: two.
8. **The newer version card said "close it" and offered Open it.** Fixed. It has no
   action now, and a card about one file gets See the file (its File detail), never a
   list of one.
9. **Fixing a shared name still went through File Explorer.** Fixed. Each file in the
   name card has Rename, which asks in the small dialog with the name picked up to its
   extension, refuses a name the project already has, a lost extension and a character
   Windows forbids, then sends the new `renameFile` message. The row itself opens the
   page of the file that already has the name ("See the Bracket.SLDPRT in Robot 2027 ›
   Intake"), and the item's line says where that file is first ("The other one is in
   Robot 2027 › Intake.") and may take two lines at 560px and under.

### Low

10. The Moving meta is the engine's line as given; the page never picks an engine
    sentence apart (section 7).
11. A save made while checked out reads like any save in the history (no amber, no
    tag). My other computer is amber on rows and on File detail alike, the Checked out
    card's disc included.
12. Pause and Resume, in the window and in BRIDGE.md; the paused line is "Paused.
    Nothing uploads or downloads until you resume." The tray already says Pause and
    Resume on the Windows lane's branch, so `TrayApp.cs` was left alone here.
13. The folder sign in engine sentences is " › ", as in the crumbs.
14. The waiting count is said once (item 3).
15. Seven new states and one more screen: `checkOutAll`, `renameFile`,
    `partialCheckOut` (the answer at the foot: "Checked out 12 of 14 files. Maria Lopez
    has 2 of them checked out."), `myOtherComputer` (row and detail), `emptyFolder`,
    `moreNotices` (taken back, part checked in, can't read), `notHereYet` (detail), and
    `transferring` on File detail (a file downloading).
16. The drop cue lies over the list only; where you are and the folder's keys stay in
    sight.
17. The selection bar sits on the cards' own line.
18. Check out all and Check in all keep their words at 560px and under; a long name
    gives way in the middle, so its extension stays.
19. An archived project's check outs stay in My files with Check in, and the archived
    panel says how many and where.
20. **Not done:** a dropped folder. Whether WebView2 hands the host a usable path for a
    dropped directory needs a real WebView2; the contract's `dropFiles` carries
    `projectId` and `folder` only, so walking the entries and sending their paths would
    be a contract change. Left for the Windows lane's test on the machine.
21. Not now sends the question's own key; it hides that one question, and the next
    open of the file asks again.

No finding was judged wrong. Two were true when written and fixed just before this
pass (`Bridge.cs` dropping the new types, nothing serializing `activity` or
`actionResult`).

### Checker changes (none loosen a rule)

- A demo shape check (above), with its planted control.
- New flows at both sizes: a row's Check out and Check in, Open in one column and the
  empty pick box; Check out all asks first, starts on Cancel and Cancel sends nothing;
  My files is my check outs and "waiting to upload" is said once; one import summary
  that agrees with the ring; Rename refuses a taken name and a lost extension, then
  renames and the card counts one fewer; the question's words, keys and answer.
- The stand-in WebView2 check sends all 23 page-to-host types, `renameFile` included;
  Check out all goes through the dialog; Not now sends the question's key, the same
  question stays hidden and a new open asks again; Right now and the status never both
  say how many wait.

### Proof

```
node tools/agent-ui/render-screens.mjs
node tools/agent-ui/bbox-diff.mjs
node tools/agent-ui/check-ui.mjs
```

```
SCREENS rendered=168 removed_stale=132 dir=docs/agent/screens/v2 index=docs/agent/screens/v2/README.md
BBOX planted control (home-synced-1280x800, one length in Space White): differing=190 (must be above 0)
BBOX states=34 comparisons=84 differing=0 missing=0 elements=32984
CHECK-UI pages=168 controls=5140 under44=0 network=0 grids=0 rowGrids=0 plantedGridLayersFound=3/3 rowDecoration=0 chipsLikeButtons=0 overflow=0 hairlines=4562 hairlineMin=3.07 hairlineUnder3=0 tabStops=1844 focusMissed=0 ringMin=4.69 ringFailures=0 jargon=0 offline=0 plantedOfflineFound=5/5 flows=40 flowFailures=0 logo=4 logoFailures=0 plantedLogoFound=1/1 bridgeTypes=23/23 bridgeFailures=0 plantedDefectsCaught=8/8 shapes=34 shapeFailures=0 plantedShapesFound=2/2 emDash=0 files=16
CHECK-UI PASS
```

`AgentViewContractTests` (9 tests) and the view serialization test pass; each new
contract field was shown to fail its test when planted wrong in bridge.js.

## With the engine (merge of a2/ui into a2/engine)

The engine's stage E1 built the v2 `AgentView` and, separately, some of the same
features as the review pass (`renameFile`, the question's `key`, Not now sending
`dismissNotice`, Check out and reopen). The merge kept one of each:

- The C# records are the engine's (they are what it serializes). The eight records the
  review left to the engine lane are gone: `The_host_view_records_have_the_fields_bridge_js_documents`
  now holds every view record equal to bridge.js. One `renameFile` everywhere: one
  `BridgeMessages` entry, one `Bridge.cs` case and one `AgentHost.RenameFileAsync`, which
  goes to `SyncEngine.RenameFileAsync`.
- `HistoryEntryView.routine` (the engine marks a kept copy saved while checked out, or an
  earlier save, as routine) is what the page reads for a save's tone; it no longer
  matches "Saved while checked out" in the note. The demo's history entries carry it.
- File detail for a kept copy says "Your change is kept in its history. Nothing was
  lost.", since a kept copy is not always someone else's check in. The demo's kept copy
  note is the engine's own ("Kept as Jordan Reyes's own copy: someone else checked in
  first"), and the demo's answers to Check out name who has the rest the way the engine
  does ("Maria Lopez and Sam Patel have ...").
- check-ui gains one check, with the stand-in WebView2: a kept copy marked routine with
  any note reads as a plain save, and one not marked routine is a kept copy even when
  its note says "Saved while checked out". Reading the note instead turns CHECK-UI red.

Proof after the merge (every screen rendered again; the ones whose state changed,
`groupedNotices` on Home and File detail, `groupedNoticesExpanded` and `renameFile`, were
looked at):

```
SCREENS rendered=168 removed_stale=168 dir=docs/agent/screens/v2 index=docs/agent/screens/v2/README.md
BBOX planted control (home-synced-1280x800, one length in Space White): differing=190 (must be above 0)
BBOX states=34 comparisons=84 differing=0 missing=0 elements=32990
CHECK-UI pages=168 controls=5152 under44=0 network=0 grids=0 rowGrids=0 plantedGridLayersFound=3/3 rowDecoration=0 chipsLikeButtons=0 overflow=0 hairlines=4574 hairlineMin=3.07 hairlineUnder3=0 tabStops=1852 focusMissed=0 ringMin=4.69 ringFailures=0 jargon=0 offline=0 plantedOfflineFound=5/5 flows=40 flowFailures=0 logo=4 logoFailures=0 plantedLogoFound=1/1 bridgeTypes=23/23 bridgeFailures=0 plantedDefectsCaught=8/8 shapes=34 shapeFailures=0 plantedShapesFound=2/2 emDash=0 files=16
CHECK-UI PASS
```

## 0.3.3 pass: every control says what it does, and only what changed is drawn

Feedback N1, N7, N8, N9 and N15, and the review's X-full-render, X-pending-count and
X-sticky-focus, with the page's half of the 0.3.3 host work. docs/agent/BRIDGE.md
("Tooltips", "Drawing", "Thumbnails") has the behavior; this is what changed and how it is
held.

### What changed

- **Tooltips (N1).** One card for the whole window (`#tip`, `role="tooltip"`), in each theme's
  own tokens (`--tip-top`, `--tip-bot`, `--tip-ink`, `--tip-edge`, `--tip-hi`, `--tip-drop`),
  after 750 ms of hover or 300 ms after Tab, under its control or over it near the foot,
  inside the window, at most 280px wide, gone on leave, press, key, scroll, redraw and
  Escape, still under reduced motion. Every control's sentence comes from one table
  (`TIPS` in app.js) and names what it is about ("Check in the 3 files you have checked out
  in Gearbox and the folders in it."). A key that is off is `aria-disabled` with its reason
  ("Nothing here is checked out by you"), never `disabled`, so the mouse reaches it. The tray
  menu's items and the "Get WebView2" key have tooltips of their own (`HostTips`).
- **One draw per change (N7, X-full-render).** The host posts no view twice; the page draws
  nothing for a view it has, only the theme and Settings for a view whose settings alone
  changed, and lays every other view over what is on the page (keyed by `data-key`, `id` or
  `data-part`) instead of replacing Home. Rows are kept by file, and a row's picture stays
  (the host serves pictures with a year's `max-age`, `immutable`). A theme picked paints in
  the next frame with transitions off, and a view that arrives meanwhile waits for it.
- **The newest running line in sight (N8).** Under the status line in a wide window, in a
  slim strip under the header in a narrow one. The running lines' box only adds at its foot
  and drops at its top; it follows the newest line only when it was at its foot.
- **Many files (N9, X-pending-count, X-sticky-focus).** Only the files an action can change
  say "Checking in..."; the working line counts them. My files' key says how many ("Check in
  my 1,401 files"), the folder's keys say "this folder" with the count in their tooltip and
  question. The list's head has "Select all in this folder" (checked, mixed or empty), and
  the selection bar "Select all 5,000" once some are picked. Force check in names two people
  and how many others ("Alex, Maria and 17 others"). The answer to an action on many files
  stays as Last action until OK. Focus reached with Tab is never under the pinned keys
  (`scroll-padding-top` from their height).
- **The rest of the 0.3.3 list.** Put back on this computer on a kept copy; the chips
  "Checks in when closed" and "No first version" (no Open or state key on the latter);
  "Saved in SolidWorks 2026" on rows and File detail, with the year on File detail; the
  `newerRelease` card; Settings' SolidWorks row drawn when the view carries one
  (`settingsSolidWorksHtml(v)`); demo states `checkingOut`, `forcingIn`, `forceManyConfirm`,
  `checkInWaits` and `newerRelease`.
- **Thumbnails that never hold the window (N15).** 5 s to answer, a stuck handler left
  behind after 20 s (written to agent.log and counted), at most 64 waiting (the newest), one
  picture for two asks, no picture asked again after 60 s, the key read before the picture
  is made. Six tests with a stand-in handler, all in tests/GUARDS.txt.
- **Words.** Dialog titles and keys say what they do ("Check out this folder", "Check in
  this folder", "Force check in this folder"); no em dash, no jargon, American spelling.

### Checker changes (none loosen a rule)

- check-ui's page sweep requires a tooltip on every visible key, link, tab, checkbox and
  switch (`tips`, `tipsMissing`), and holds every tooltip to the copy rule; two planted
  controls (one with no tooltip, one with a jargon tooltip) must be caught
  (`plantedDefectsCaught` 10/10).
- New flows at both sizes: tooltip by mouse (not before about 750 ms, describes its control,
  goes on leave and on a click, a key that is off says why and does nothing, inside the
  Settings sheet above its scrim) and by keyboard (Tab, Escape closes the card only); the
  running lines in `checkingOut` and `forcingIn` (`#act-log` has every line, the newest in
  sight in the status or the strip); keys in sight at open (bounding boxes); Select all in
  this folder; focus stays clear of the pinned keys; Force check in names a few; Checks in
  when closed and No first version; Saved in a newer SolidWorks.
- The stand-in WebView2 host: only touched rows say they are working, the working line and
  the question agree on the count, Last action stays until OK, Put back sends
  `putBackKeptCopy {fileId, versionId}`, and every page-to-host type is still sent (44/44).
- A new Drawing block: an identical view changes nothing in `#main` or the header, a
  settings-only view changes only the theme, a theme pick with 1,401 files and a 4x slower
  CPU paints within 250 ms with no change to `#main` and the waiting view follows, ten
  near-identical views of 1,401 files stay under 3 s with their rows kept, and the running
  lines are added and dropped without redrawing the rest. The thumbnail block checks that
  drawing Home again keeps every picture and asks for none.

### Not done

- Settings' SolidWorks row and a notice's `keepLocal` and `saveDown` keys are drawn from a
  field and commands (`view.solidWorks`, `keepLocal`, `saveDown`) that the bridge lists in
  this base do not have yet; the page draws them when they come and shows nothing until
  then.
- A picture is kept for a year under its address, which carries the file's last check in
  and whether it has changes: a file saved again while it already has changes keeps the
  picture of its first change until it is checked in. The view carries nothing finer.
- Not run on Windows here: the tray tooltips, the WebView2 key's tooltip and the shell
  thumbnails' STA threads are covered by tests and reading, not by a look at a real window.
- The screens in docs/agent/screens were not drawn again.

### Proof

```
node tools/agent-ui/bbox-diff.mjs
node tools/agent-ui/check-ui.mjs
```

```
BBOX planted control (home-synced-1280x800, one length in Space White): differing=211 (must be above 0)
BBOX states=65 comparisons=148 differing=0 missing=0 elements=65649
CHECK-UI pages=296 controls=10904 under44=0 network=0 grids=0 rowGrids=0 plantedGridLayersFound=3/3 rowDecoration=0 chipsLikeButtons=0 overflow=0 hairlines=9248 hairlineMin=3.07 hairlineUnder3=0 tabStops=3524 focusMissed=0 ringMin=4.69 ringFailures=0 jargon=0 offline=0 plantedOfflineFound=5/5 flows=86 flowFailures=0 logo=4 logoFailures=0 plantedLogoFound=1/1 bridgeTypes=44/44 bridgeFailures=0 tips=10720 tipsMissing=0 drawing=5 drawMs=1734 plantedDefectsCaught=10/10 shapes=66 shapeFailures=0 plantedShapesFound=2/2 emDash=0 files=18
CHECK-UI PASS
```
