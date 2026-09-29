# Publish amd64 and arm64 images (#995)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md).

**Status:** accepted

**Date:** 2026-09-29

**No incident** — arm64 was unsupported; the prior image was an amd64 manifest.

## Decision

Run `image` as a two-leg matrix on native amd64 and arm64 GitHub runners. Each
leg builds one local image, scans those bytes with Trivy, and boots the app on
its native runner until `/health/ready` and the image's health check pass.
The smoke test's existing database image already supports both runners. Each
leg saves its image and local image Id in a distinct artifact. `publish` loads
both artifacts, checks each Id and architecture, pushes
two platform manifests, then assembles the `:sha-<commit>` index. It records and
attests the index digest. `release-please.yml` retags that digest to the version
without rebuilding.

The amd64 cache prefix remains `image-layers-`, which matches
`e2e-smoke.yml`. The arm64 prefix is `image-layers-arm64-`; its restore keys
cannot import amd64 layers. The #315 lock-drift check runs on amd64 only. It
changes a NuGet pin and asks whether locked restore fails, which has the same
answer on either architecture. The #782 job condition stays
`!cancelled() && needs.changes.outputs.docs_only != 'true'`. GitHub waits for
every successful matrix leg before satisfying `publish.needs: image`.

## Evidence

GitHub [documents `ubuntu-24.04-arm`](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
as an arm64 runner for public repositories. Its [workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)
says a dependent job waits for every matrix leg and warns
that colliding matrix outputs have no guaranteed winner. The image Id therefore
travels inside each leg's artifact, beside the image it identifies.

I pushed two single-platform manifests and an index to a local `registry:2`.
The index digest was
`sha256:10b8bde9d6f8f815ee07a096ff0654a0147187d54c4006e57f3302e2967fa812`.
Retagging that digest with both the default `imagetools create` setting and
`--prefer-index=false` returned the same digest. The existing flag remains for
older single-manifest releases. In the same registry, the default changed a
single manifest's digest from `sha256:b7f3d86d6e84fc17718c48bcde1450807faa2d56704205c697b4bd5df7b9e29f`
to `sha256:25ffb45ca14c6c27357848f581af91665a7b953ebe5a5a9f41edfe12306ddfe2`,
while `--prefer-index=false` preserved the source digest. The
post-retag digest comparison remains the release gate.

I also saved an arm64 image to a tarball, removed its local tag, and loaded the
tarball on an amd64 Docker daemon. Its image Id and `arm64` architecture were
unchanged. Pushing that loaded image to the local registry produced the same
arm64 manifest digest.

## Alternatives and accepted costs

QEMU builds were rejected. Native runners let Trivy examine each architecture's
locally loaded bytes and let the arm64 smoke test execute on arm64. Scanning
only amd64 would miss arm64-specific packages; scanning after a push would let
unscanned bytes reach the registry. The two native legs run concurrently, but
each consumes runner time, cache storage, and an image artifact. Build timings
from this branch are recorded below.

The per-architecture `:sha-<commit>-<arch>` tags remain in GHCR. The index
references their manifests; keeping tags avoids relying on retention of
untagged manifests. These tags are registry implementation details. Operators
use the attested index reference from `image.json`.

The workflow attests the index digest once. Docker selects a platform child
when pulling, so the child's digest differs from the attestation subject.
With `gh` 2.101.0, I ran `gh attestation verify` with `--repo soit-ai/soit`
and `--bundle-from-oci` against the public index-only attestation at
`oci://ghcr.io/soit-ai/soit/server@sha256:96b80ae141000adde27cf3dedb27935b5f5d0085d69025cffb4bbb63276d5929`.
It exited 0. The same command against its amd64 child
(`sha256:84e7f539421515654b83f1d2c8fe00eaa3f291bc86a476d93e35e52409c1d32f`)
exited 1 with `no attestations found in the OCI registry`. This tests the CLI's
digest-specific behavior on the same attestation shape the new workflow uses.
Deployers verify the index reference, compare the version tag to that index,
and deploy the index reference. This does not add a separate attestation to
each child. The scan and smoke gates cover both children before publication.

No required configuration key changes. The #370 simulation harness and #565
AppHost config do not need changes. Base pins stay multi-platform indexes; the
runtime remains the full glibc image with tzdata and ICU (#264, #267).

## Build time

The `Build runtime image` step includes the build and local cache rotation.
The [cold run](https://github.com/mforce/cluckwork/actions/runs/36510334345) used
`--no-cache` on both native runners. The
[warm run](https://github.com/mforce/cluckwork/actions/runs/36510573616) removed
that temporary flag and restored the previous run's architecture-specific
cache through the shared dependency-hash prefix. All four builds passed.

| Runner | Cold | Warm |
| --- | ---: | ---: |
| amd64 | 63 s | 33 s |
| arm64 | 66 s | 32 s |
