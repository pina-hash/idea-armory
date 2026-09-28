# SolidWorks lock-file behavior

Measured: 2026-09-27T18:22:23.6514499-07:00

Windows: Microsoft Windows NT 10.0.26200.0; build 26200.

Application revision: `34.4.1`. Connected to an existing session; only the probe document will be closed.

No existing CAD files were found within the permitted search roots. SolidWorks generated and saved a blank part from its default template, then closed it before the open/close measurement.

Opened: 2026-09-27T18:22:27.7988786-07:00; closed: 2026-09-27T18:22:28.6119210-07:00; folder sampled 500 ms after each transition.

Before open: `generated-empty-2026.SLDPRT`.

While open: `~$generated-empty-2026.SLDPRT, generated-empty-2026.SLDPRT`.

After close: `generated-empty-2026.SLDPRT`.

Observed new `~$` files: 1; removed after close: 1. This is an observation of this release and document, not the open-file detector's authority. `~$*` is always excluded from sync.
