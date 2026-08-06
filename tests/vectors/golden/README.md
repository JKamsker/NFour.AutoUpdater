# Checked-in golden repository

This is a small, deterministic v1 repository tree used by the conformance tests.  It
contains two products, three releases, five variant axes, five package manifests,
file-table shards, content blobs, and one deliberately orphaned blob. The release locks,
bundles, coverage reports, package/release indexes, channel pointers, revocations, and key
manifest are checked-in deterministic detached-envelope artifacts; tests load them through
the real repository-reader APIs.
