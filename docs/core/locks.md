# Locks

`LockMachine` is a pure state machine. States carry holder identity, device, timestamp,
and dirty/synced information. `Apply` returns a success/new-state result or the unchanged
state with a readable reason. Time and break authority are supplied by the caller.
No role names or privilege defaults are embedded in the library.

A holder can edit, acknowledge sync, and release only when clean. Other people/devices
cannot impersonate the holder. An authorized actor requests, cancels, or confirms a
break. Confirmation emits a durable recovery obligation for the previous holder even
when the server thought the holder was clean, since that holder may have offline saves.

Broken locks can be acquired by a new device while the old device is offline. The
transition carries the old recovery owner; the server must retain that obligation
separately from the newly active lock. On reconnect the old local snapshot is sent as
a side version. Acknowledging that obligation requires durable preservation, not merely
reconnecting. Lock epochs/fencing and persistence belong to the server adapter.

`LockTests` enumerates all 56 combinations of seven state variants and eight events,
including rejection checks. Additional tests cover dirty release, unauthorized breaks,
other-device impersonation, cancellation, reconnect, clean-looking offline holders,
and a new holder acquiring a revoked lock without losing the recovery obligation.
