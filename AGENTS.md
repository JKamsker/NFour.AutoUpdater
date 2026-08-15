# Repository Instructions

- Never ever use magic strings or magic numbers.
- Give protocol values, status values, limits, retry parameters, identifiers, and other
  non-obvious literals descriptive constants or strongly typed names at the narrowest useful
  scope. Reuse an existing domain constant or enum instead of duplicating its literal value.
- Apply the same rule to tests: name scenario values when the literal's meaning is not
  immediately inherent in the assertion or input being demonstrated.
- Keep non-generated C# below 300 lines where practical and never grow a file beyond 500
  lines without a documented exception. Run `scripts/check-file-size.sh`; opt into the
  repository pre-commit hook with `git config core.hooksPath .githooks`.
