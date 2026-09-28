# Vault paths

`VaultPath` contains a vault-relative NFC path with forward slashes. Equality, hashing,
and ordering use ordinal case-insensitive comparison, independent of machine culture.
`TryCreate` parses a relative path; `TryFromSegments` validates names supplied separately
by a server, so a literal slash inside a folder name cannot become a silent subfolder.

Validation reports errors for reserved characters, controls, device names (including
extensions and superscript device digits), empty and dot segments, trailing dots/spaces,
unpaired surrogates, and full-path length. The default limit of 240 counts UTF-16 code
units, including the configured root and separators. An emoji uses two units.

`TryToWindowsPath` is the edge conversion and checks the current root and length limit
again. Its root is trusted adapter configuration, not a server-provided relative name.
The default struct value is invalid and returns a problem rather than crashing at the
conversion boundary. No invalid server name is silently renamed.

`NamingTests` covers every reserved character, controls, representative reserved names,
path escapes, malformed Unicode, emoji, decomposed/composed accented names, equality and
hashing, exact length boundaries, and the edge conversion. `AnalyzeImport` reports all
invalid server paths for a lead alongside all duplicate-name groups.
