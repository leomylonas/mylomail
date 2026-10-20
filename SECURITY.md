# Security policy

Mylo Mail handles mail, calendars and account credentials, so vulnerabilities matter.

## Reporting

Please report vulnerabilities privately through GitHub:
**Security → Report a vulnerability** on
[the repository](https://github.com/leomylonas/mylomail/security/advisories/new).
Do not open a public issue, and do not include real credentials or message content.

Include what you found, how to reproduce it, the version or commit, and your operating
system. You can expect an acknowledgement, and a fix or an explanation, as soon as a
maintainer is able; this is a small project without a service-level promise.

## Scope

In scope: the desktop app, the local backend it starts, and the build and release workflows.
Out of scope: vulnerabilities in a mail provider's own service, and in third-party
dependencies that are not exploitable through Mylo Mail (report those upstream).

## Supported versions

The project is in active development; only the latest release and `main` receive fixes.
Release packages are unsigned: verify downloads against `SHA256SUMS`.
