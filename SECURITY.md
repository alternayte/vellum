# Security policy

## Report a vulnerability

Do not open a public issue for a vulnerability.

Report it through GitHub private vulnerability reporting:
https://github.com/alternayte/vellum/security/advisories/new

Give the version, the profile (local or hosted), the steps to reproduce and the effect.
You get an answer to each report. A fix goes into a release with an advisory that names
the reporter, unless the reporter asks to stay anonymous.

## Supported versions

Only the latest release gets security fixes. Before version 1.0, a fix is not ported to an
earlier minor version.

## Scope

- The Vellum server, the web client and the `vellum scan` CLI in this repository.
- The image `ghcr.io/alternayte/vellum`.
- The Compose files in this repository, used as their comments describe.

The documentation describes the security model:
https://vellum-arch-docs.pages.dev/concepts/security-model/
