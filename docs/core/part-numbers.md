# Part numbers

`PartNumberPattern` configures the prefix and numeric field widths. The default is
`5669-YY-SSNN`, with two-digit season, subsystem, and part fields. A course uses a prefix
such as `IDEA209H`. Prefixes contain ASCII letters/digits; widths are one through four.

Formatting and parsing use invariant culture and ASCII digits with exact field lengths.
Negative/out-of-range values and malformed configuration fail without allocating a
number. `TryNext` fills the lowest available part slot (00 through 99 by default) for
the requested season and subsystem and reports a full subsystem. Other seasons,
subsystems, prefixes, and invalid input strings do not consume its slots.

`VersionAndPartTests` covers FRC and class prefixes, round trips, configurable widths,
invalid values, non-ASCII digits, hole filling, season/subsystem isolation, and all 100
occupied default slots. Allocation must eventually run inside a server transaction;
two concurrent callers of this pure helper can otherwise choose the same free number.
