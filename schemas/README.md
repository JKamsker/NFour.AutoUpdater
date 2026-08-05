# AutoUpdater schemas

The repository format is versioned at `schemaVersion: 1`. Trusted documents reject unknown
fields and unknown schema versions; derived documents may ignore unknown fields. The runtime
reader performs duplicate-key, integer, NFC, and null checks before deserialisation. These
schemas are intentionally checked in so a non-.NET implementation can validate the wire form.

Minimal valid and deliberately invalid JSON fixtures for every schema live under
[`fixtures/`](/home/jonas/priv/repos/AutoUpdater/schemas/fixtures/). Runtime tests additionally
exercise duplicate-key, digest-domain, identity, and cross-document invariants that JSON Schema
cannot express.
