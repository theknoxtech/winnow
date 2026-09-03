# Code Signing Policy

This page exists to satisfy [SignPath Foundation's](https://signpath.org/) requirements for
projects using their free open-source code signing program, and to be a straightforward,
accurate account of how Winnow releases are built and who can authorize a signature.

**Status: applying, not yet active.** Winnow is not currently signed by SignPath. This page
documents the policy that will govern it if the application is accepted; see
[README § Code signing](README.md#code-signing-and-why-there-isnt-any) for the project's current,
factual signing status.

## Attribution

Once active, every signed release carries:

> Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
> [SignPath Foundation](https://signpath.org/)

## Team and roles

Winnow has one maintainer, who currently holds all three roles SignPath's policy requires to be
named. This will be updated if that changes.

| Role | Person | Responsibility |
|---|---|---|
| Author | Jon Witherspoon ([@theknoxtech](https://github.com/theknoxtech)) | Commits directly to the repository. |
| Reviewer | Jon Witherspoon | Reviews any external contribution before it is merged. |
| Approver | Jon Witherspoon | Authorizes each release's signing request. |

## What gets signed, and from what

Only `Winnow.exe`, built by the [`release.yml`](.github/workflows/release.yml) GitHub Actions
workflow directly from this repository's `main` branch at the commit a version tag points to. No
other artifact is signed, and nothing is signed from a source tree that didn't come from this
repository — there is no separate "release branch" or out-of-band build process. See
[README § Building](README.md#building) for the exact build steps; anyone can reproduce them.

A signing request is submitted only when a `v*.*.*` tag is pushed, and (per SignPath's policy)
requires the Approver's authorization before the signature is issued — pushing a tag alone does
not sign anything unattended.

## Privacy

Winnow does not collect, transmit, or store any user or usage data, anywhere in the application.
The entire codebase makes exactly one outbound network request: on launch, a background check
against `https://api.github.com/repos/theknoxtech/winnow/releases/latest` — GitHub's public,
unauthenticated releases API — to see whether a newer version exists. That request:

- Sends nothing but a `User-Agent: Winnow` header and a standard `Accept` header. No identifier,
  hardware information, telemetry, or usage data of any kind is attached.
- Times out after 4 seconds and fails completely silently — an offline machine, a blocked
  outbound connection, or a GitHub rate limit all just mean the check quietly doesn't happen.
- Never downloads or installs anything. It reports that a newer release exists and links to the
  GitHub release page; the user decides whether to act on that.

Everything else the application does — reading Windows Event Logs, writing an exported CSV,
reading or writing a `presets.json` side-car file, writing a diagnostic `bindings.log` when
explicitly requested with `--trace-bindings` — is local disk and local Windows API activity. None
of it leaves the machine. Source: [`src/Winnow.Core/Update/UpdateChecker.cs`](src/Winnow.Core/Update/UpdateChecker.cs)
is the only file in the repository that opens a network connection, which can be verified directly
rather than taken on faith.

## Multi-factor authentication

MFA is enabled on the GitHub account and organization that own this repository, and will be
enabled on the SignPath account used to manage signing, per SignPath's requirement.

## Conduct

Winnow does not include, and will not include, functionality designed to identify or exploit
security vulnerabilities, bypass security controls, or otherwise circumvent the security of a
system it is not being deliberately used on. It is a read-only log triage tool: every preset
queries the Windows Event Log through the standard `EventLogReader` API and displays what is
already there.
