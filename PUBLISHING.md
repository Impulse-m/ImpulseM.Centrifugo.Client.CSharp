# Publishing ImpulseM.Centrifuge to nuget.org

The package `ImpulseM.Centrifuge` is published to [nuget.org](https://www.nuget.org/packages/ImpulseM.Centrifuge/) by the workflow `.github/workflows/release.yml`. The code namespace stays `Centrifugal.Centrifuge`. Versions are semantic versions, starting at `1.0.0`.

Nothing is published by hand in the normal flow. The workflow signs in to nuget.org through trusted publishing (OpenID Connect), so no API key is stored in the repository.

## One-time setup (owner)

1. **nuget.org account.** The package owner signs in to nuget.org.
2. **Trusted publishing policy.** On nuget.org, open the package (or the account, for a package that does not exist yet) and add a trusted publishing policy with:
   - repository owner: `Impulse-m`
   - repository: `ImpulseM.Centrifugo.Client.CSharp`
   - workflow file: `release.yml`
   - environment: `release`
3. **First version.** nuget.org may not accept a policy for a package id that has no versions yet. If it does not, publish `1.0.0` once by hand, following [First release by hand](#first-release-by-hand), and create the policy right after. Every later version goes through the workflow. This has not been verified against the live service.
4. **GitHub environment.** In the repository settings, create the environment `release`, and limit its deployment branches and tags to `main` and `v*`.
5. **GitHub variable.** Add the repository variable `NUGET_USER` with the nuget.org profile name that owns the package. The workflow passes it to the `NuGet/login` action.

No `NUGET_API_KEY` secret is needed for the workflow.

## Releasing a version

Run the workflow in one of two ways. The version is never read from the project file.

- **Manual dispatch (recommended).** Open Actions, choose `release`, and enter the version without the leading `v`, for example `1.0.0` or `1.1.0-rc.1`. The run must start on `main`. The workflow creates the tag `v<version>` on the commit of the run at the end, after the package is served.
- **Tag push.** Push a tag `v<version>` on a commit of `main`. The workflow runs the same jobs, but creates no tag, because the tag already exists. The validate job refuses a tag whose commit is not on `main`.

The jobs run in this order:

1. **validate.** The commit must be on `main`. The version must be semver without the leading `v`. For a dispatch the tag must not exist on origin. A GitHub Release `v<version>` must not exist. A stable version must be greater than the latest stable GitHub Release.
2. **test.** The CI workflow `test.yml` runs in full, with the integration tests against Centrifugo. Nothing is published when a test fails.
3. **build.** `dotnet pack` runs with `-p:Version=<version>`, so the version is set without editing the project file. The `.nupkg` must contain `LICENSE` and `THIRD_PARTY_NOTICES.md` at its root. The `.snupkg` symbol package is built too.
4. **publish.** The job stops when nuget.org already lists the version. Then it pushes the `.nupkg` and, as a separate push, the `.snupkg`. Nothing is skipped silently: the push does not use `--skip-duplicate`.
5. **verify.** The job polls the nuget.org package index every 15 seconds for at most 10 minutes, until the version is listed.
6. **tag.** Dispatch only. It creates the lightweight tag `v<version>` on the commit of the run and pushes it.
7. **release.** It creates the GitHub Release `v<version>` with generated notes, marked as a pre-release when the version has a `-`, and as the latest release otherwise. It attaches the `.nupkg` and `.snupkg` files.

### Release gate

The workflow does not check these, so check them before a release starts:

- every feature that `README.md` and the `Unreleased` section of `CHANGELOG.md` describe is in `src/`, and the public API is listed in `PublicAPI.Shipped.txt` or `PublicAPI.Unshipped.txt`;
- the build passes with the public API analyzer, and the test workflow is green on the commit.

### Pre-releases

A version with a `-` suffix, for example `1.1.0-rc.1`, becomes a GitHub pre-release. nuget.org does not install it by default; a consumer asks for it with `dotnet add package ImpulseM.Centrifuge --version 1.1.0-rc.1`.

The `.snupkg` push has not run against nuget.org yet. The first prerelease run after a change to the release workflow checks it, before any stable release.

### Versions are immutable

nuget.org does not replace a version. If a version is wrong, release the next one. A version that was pushed but failed at `verify` stays on nuget.org: the package exists, but no tag and no GitHub Release were created. Re-running the same version then fails at the publish job, and the fix is a new version. nuget.org can unlist a version by hand; an unlisted version is still reserved.

### Breaking changes

A breaking change of the public API is named in the title or the body of the pull request, so that it reaches the release notes. The release notes are generated from the merged pull requests, not from `CHANGELOG.md`.

## First release by hand

Use this only for the first version, when nuget.org has no trusted publishing policy for the package yet. The steps run from a clean checkout of the commit on `main` that the release is for. This path has not been verified against the live service.

1. Pack the version:

```bash
dotnet pack src/Centrifugal.Centrifuge/Centrifugal.Centrifuge.csproj -c Release -p:Version=1.0.0 -o artifacts
```

2. Push the package and the symbol package. Use an API key scoped to the package id `ImpulseM.Centrifuge` and the push glob `ImpulseM.Centrifuge*`, read from the environment variable `NUGET_API_KEY`. Do not write the key to a file in the repository.

```bash
dotnet nuget push artifacts/ImpulseM.Centrifuge.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --no-symbols
dotnet nuget push artifacts/ImpulseM.Centrifuge.1.0.0.snupkg --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY"
```

3. Create the trusted publishing policy (step 2 of the one-time setup), then revoke the API key.

4. Finish the release, because the workflow run for 1.0.0 cannot do it:

```bash
git tag v1.0.0 <commit-on-main>
git push origin refs/tags/v1.0.0
gh release create v1.0.0 --verify-tag --generate-notes --latest artifacts/ImpulseM.Centrifuge.1.0.0.nupkg artifacts/ImpulseM.Centrifuge.1.0.0.snupkg
```

The tag push starts a `release` run. Its publish job refuses 1.0.0 because nuget.org already lists it, so that run stops before the tag and release jobs. A failed run here is expected and changes nothing. The release is created only by the `gh release create` command above.

## Local pack

To check the package locally, pack it with the release version. Do not push it by hand unless the workflow cannot run.

```bash
dotnet pack src/Centrifugal.Centrifuge/Centrifugal.Centrifuge.csproj -c Release -p:Version=1.0.0 -o artifacts
```

Then inspect the package root:

```bash
unzip -Z1 artifacts/ImpulseM.Centrifuge.1.0.0.nupkg
```

## Version in the project file

`src/Centrifugal.Centrifuge/Centrifugal.Centrifuge.csproj` carries a `<Version>` value for local builds. The release workflow passes the version through `-p:Version`, so the project file is not edited for a release. Keep the project value at the last released version when you change it.

## Package metadata

The metadata is defined in `src/Centrifugal.Centrifuge/Centrifugal.Centrifuge.csproj`. The package carries `README.md`, `icon.png`, `LICENSE` and `THIRD_PARTY_NOTICES.md` at its root, and the XML documentation and the symbol package are published with it.

## Troubleshooting

### Version already published

The publish job found the version on nuget.org. Choose a new version and run the workflow again.

### Symbol package push fails after the package

The publish job pushes the `.nupkg` first. If the `.snupkg` push then fails, the version is on nuget.org, the tag and the release are not created, and a rerun of the workflow fails at the publish job. Do not publish a new version for this. Push the symbol package of the same version by hand, with the second command of [First release by hand](#first-release-by-hand) step 2, then create the tag and the GitHub Release with step 4 of that section.

### Sign-in fails

Check, in this order: the `release` environment exists and allows the branch or tag, the variable `NUGET_USER` is set, the trusted publishing policy names this repository, this workflow file and the environment `release`.

### Package not listed after publish

The verify job waits 10 minutes. If it times out, the version is on nuget.org but not listed in the index yet. Check the package page on nuget.org and the job log before you decide anything; do not publish the same version again.
