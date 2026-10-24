# Support

## Where to ask

Open a [GitHub issue on ivanball/MMCA.Common](https://github.com/ivanball/MMCA.Common/issues) for
questions, bug reports and feature requests. Include the package version (the startup
configuration summary a host logs names it), the .NET SDK version, and a minimal reproduction where you
can.

The maintainer is [@ivanball](https://github.com/ivanball) (see [.github/CODEOWNERS](.github/CODEOWNERS)).

## Security reports

Do not open a public issue for a suspected vulnerability. Follow [SECURITY.md](SECURITY.md), which
describes the private reporting channel.

## Supported versions

Fixes land on the latest released version; there is no long-term support branch. The policy is in
[SECURITY.md](SECURITY.md) and in
[the published versioning policy](https://ivanball.github.io/docs/guides/common-VERSIONING.html).

## Commercial support

For commercial support, contact [Cleavestack](https://cleavestack.com/), the commercial home of MMCA.

## Continuity

- The code is licensed under Apache 2.0 ([LICENSE](LICENSE)), so it can always be forked.
- The release pipeline is public: [.github/workflows/release.yml](.github/workflows/release.yml)
  builds, tests, packs and publishes every version from a git tag.
- Published nuget.org versions are immutable, so a project that pins a version keeps restoring it.
