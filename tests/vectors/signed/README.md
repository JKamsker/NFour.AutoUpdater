# Signed-document golden vectors
These fixtures cover duplicate-key rejection (`duplicate-envelope.json`), exact whitespace payload signing (`valid-envelope.json`), rollback chains, overlapping key validity, and never-empty trust-set rejection from docs/Initial/17-signed-documents.md.
The payload members are exact UTF-8 JSON inputs and must not be re-serialised before detached-envelope verification. The signing prefix is `4sup-v1\\0<type>\\0`.
