All model, toolpath and laser fixtures in this folder are original synthetic test data
created for codex-armory-previews on 2026-11-03. No student or team work was used.
Toolpaths are for preview tests only, not machine operation.
The binary STL deliberately starts with "solid" to exercise format detection.
Both LightBurn dialects describe the same outline. STEP/STP exercise type tiles only.
LargeFixtures.cs generates the 100 MB mesh and larger linework in a temporary directory.
Golden PNGs are generated explicitly by ARMORY_UPDATE_PREVIEW_GOLDENS=1, then compared
by decoded RGBA pixels during normal tests. Review the pictures when changing a golden.
