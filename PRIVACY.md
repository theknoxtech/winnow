# Privacy and provenance

A plain account of what Winnow does with data, and how a release is produced.

Because Winnow ships as a readable script rather than a compiled binary, none of this has to be
taken on trust — [`Winnow.ps1`](Winnow.ps1) is the whole application, and every claim below can be
checked against it directly.

## Privacy

**Winnow does not collect, transmit, or store any user or usage data.** There is no telemetry, no
analytics, and no crash reporting.

The entire script makes exactly one outbound network request: a few seconds after launch it checks
`https://api.github.com/repos/theknoxtech/winnow/releases/latest` — GitHub's public,
unauthenticated releases API — to see whether a newer version exists. That request:

- Sends nothing but a `User-Agent: Winnow` header. No identifier, no machine information, no usage
  data of any kind is attached.
- Times out after 4 seconds and fails completely silently. An offline machine, a blocked outbound
  connection, or a GitHub rate limit all just mean the check quietly doesn't happen.
- Never downloads or installs anything. It reports that a newer release exists and offers the link;
  what to do about that is the user's decision.

Everything else Winnow does is local: reading Windows Event Logs through the standard `Get-WinEvent`
API, writing a CSV where you ask it to, and rendering the results. None of it leaves the machine.

`Test-ForUpdate` is the only function in the script that opens a network connection, and it is a
few dozen lines — short enough to read in a minute rather than believe on assertion.

## Provenance

Releases are published by the [`release.yml`](.github/workflows/release.yml) GitHub Actions
workflow, triggered by a version tag, directly from this repository. There is no separate release
branch and no out-of-band build.

Two files are published. `Winnow.ps1` is the script exactly as it exists in the tagged commit —
nothing transforms it. `Winnow.exe` is that same script compiled by
[ps2exe](https://github.com/MScholtes/PS2EXE), which embeds it and hosts the PowerShell runtime;
the exe is therefore the same application as the script by construction, and the script is
readable if you would rather audit it than trust the binary.

Every release publishes a SHA-256 for each file, in both the release notes and `.sha256` assets.
Since neither is signed, those hashes are what confirm the file you downloaded is the file that
was published. See [README § Verifying a download](README.md#verifying-a-download).

The `irm | iex` one-liner fetches `Winnow.ps1` from the latest release over HTTPS, so it runs that
same published script and nothing else. Two things are worth being clear about, though. Piping
straight into `iex` executes the script as it arrives, with no opportunity to check a hash first.
And that command shape is the one used by the *ClickFix* class of social-engineering attack, so
endpoint security products watch for it — which is a reason to prefer fetching the script to a
file on a machine that is not yours. See [README § Which one to use](README.md#which-one-to-use).

## Code signing

Winnow is unsigned. Signing certificates are a recurring cost with a hardware-key requirement
since the June 2023 CA/Browser Forum baseline; Azure Artifact Signing's individual tier requires
personal identity verification; and an application to
[SignPath Foundation](https://signpath.org/)'s free open-source program **was not approved**.

The practical consequence is that a customer's security team cannot write an AppLocker or WDAC
*publisher* rule against Winnow. They need a hash rule, which is what the published SHA-256 is
for — and which has to be updated on each release.

## Maintainer

Winnow is maintained by Jon Witherspoon ([@theknoxtech](https://github.com/theknoxtech)), who
authors changes, reviews any external contribution, and authorises releases.
