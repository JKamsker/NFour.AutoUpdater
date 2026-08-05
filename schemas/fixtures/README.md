# Schema fixtures

Each document schema has a `valid-basic.json` and an `invalid-unknown-field.json` fixture.
The valid files are deliberately small but complete at the schema level; the invalid files
exercise `additionalProperties: false`. Runtime readers add digest, identity, and cross-object
validation beyond JSON Schema.
