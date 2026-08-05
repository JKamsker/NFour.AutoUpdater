# AutoUpdater schemas

The repository format is versioned at `schemaVersion: 1`. Trusted documents reject unknown
fields and unknown schema versions; derived documents may ignore unknown fields. The runtime
reader performs duplicate-key, integer, NFC, and null checks before deserialisation. These
schemas are intentionally checked in so a non-.NET implementation can validate the wire form.
