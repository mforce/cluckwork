# Publish amd64 and arm64 images (#995)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md).

**Status:** accepted

**Date:** 2026-09-29

**No incident** — arm64 was unsupported; the prior image was an amd64 manifest.

## Decision

Run `image` as a two-leg matrix on native `ubuntu-26.04` and
`ubuntu-26.04-arm` GitHub runners. Each leg builds one local image, scans those
bytes with Trivy, and boots the app on
its native runner until `/health/ready` and the image's health check pass.
The smoke test's existing database image already supports both runners. Each
leg saves its image and local image Id in a distinct artifact. `publish` loads
both artifacts, checks each Id and architecture, pushes
two platform manifests, and captures their pushed digests. It assembles the
`:sha-<commit>` index from those digest references, then checks that the index
contains exactly those two children. It computes the index digest locally from
`imagetools create --dry-run` and verifies the pushed index by that immutable
reference before recording and attesting it.
`release-please.yml` retags that digest to the version without rebuilding.

The amd64 cache prefix remains `image-layers-`, which matches
`e2e-smoke.yml`. The arm64 prefix is `arm64-image-layers-`. It must not start
with `image-layers-`, because that is amd64's broadest restore key and cache
lookup matches prefixes. The #315 lock-drift check runs on amd64 only. It
changes a NuGet pin and asks whether locked restore fails, which has the same
answer on either architecture. The #782 job condition stays
`!cancelled() && needs.changes.outputs.docs_only != 'true'`. GitHub waits for
every successful matrix leg before satisfying `publish.needs: image`.

## Evidence

GitHub [made both Ubuntu 26.04 runner labels generally available](https://github.com/actions/runner-images/issues/14747)
in September 2026. It [plans to move `ubuntu-latest` to 26.04](https://github.com/actions/runner-images/issues/14748)
in November 2026. Pinning both legs prevents that alias move from changing
only amd64's build host. GitHub's [workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)
says a dependent job waits for every matrix leg and warns
that colliding matrix outputs have no guaranteed winner. The image Id therefore
travels inside each leg's artifact, beside the image it identifies.

With a local `registry:2`, I retagged a two-platform index by digest with both
the default `imagetools create` setting and `--prefer-index=false`; both kept
its digest. A single-manifest source changed digest with the default and kept
it with `--prefer-index=false`. The [buildx v0.31.1 implementation](https://github.com/docker/buildx/blob/v0.31.1/util/imagetools/create.go)
returns an index source's original bytes regardless of `preferIndex`, which
explains both observations. The existing flag remains for older
single-manifest releases, and the post-retag digest comparison stays a gate.

In the same registry, the SHA-256 of `imagetools create --dry-run` JSON, with
its CLI newline removed, matched the digest of the pushed index. Buildx
v0.37.1 emitted the same JSON for both operations. A tag-built index after I
moved the amd64 tag failed the exact-child assertion; the index built from
digest references still passed. Adding a third child with `platform.os` set to
`windows` passed the former Linux-filtered architecture predicate and failed
the exact-child assertion. An index pushed with different bytes would fail the
immutable digest lookup before any digest is recorded.

I used `docker push`'s digest line for each platform manifest. Docker 29.4.2
emits `tag: digest: <digest> size: <bytes>` from both its
[containerd image store](https://github.com/moby/moby/blob/docker-v29.4.2/daemon/containerd/image_push.go#L138)
and [legacy store](https://github.com/moby/moby/blob/docker-v29.4.2/daemon/internal/distribution/push_v2.go#L207).
On a local Docker 29.8.1 client and daemon using the containerd store, I pushed
an amd64 image to `registry:2`; the workflow's anchored parser extracted the
pushed manifest digest. The CLI also printed a multi-platform notice after the
digest line. `docker image inspect`'s `RepoDigests` still named the source
index, so local image inspection would have supplied the wrong child digest.

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

Each platform manifest has a durable tag containing its full manifest digest,
`:sha-<commit>-<arch>-<manifest-hex>`. The workflow creates it from the pushed
digest, so a repair build of the same commit creates a new tag if its bytes
change. These tags protect released index children from untagged-manifest
cleanup. The plain `:sha-<commit>-<arch>` tags are convenience names used for
the initial push. They can move on a repair and do not protect retention. The
index uses digest references, not either tag. Operators use the attested index
reference from `image.json`.

Both matrix legs must pass before `publish` runs: `needs: image` causes this
coupling even without `fail-fast`. An arm64 failure can leave a merge without a
`:sha-<commit>` image, where the former amd64-only pipeline might have
published one. `fail-fast: true` also cancels an unfinished sibling after a
failure; runner shortage delays publication. Publishing a half-architecture
index would break the promised platform set, so the release stays blocked.
For a transient failure, dispatch CI from `main` for that exact commit after
the problem clears. A real pinned-image CVE needs a fix commit; rebuilding the
same source cannot clear it.

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
The [Ubuntu 24.04 cold run](https://github.com/mforce/cluckwork/actions/runs/36510334345) used
`--no-cache` on both native runners. The
[Ubuntu 24.04 warm run](https://github.com/mforce/cluckwork/actions/runs/36510573616) removed
that temporary flag and restored the previous run's architecture-specific
cache through the shared dependency-hash prefix. All four builds passed.
On Ubuntu 26.04, same-commit reruns restored exact cache keys and BuildKit
marked all build layers cached: [amd64](https://github.com/mforce/cluckwork/actions/runs/36610454419/attempts/3)
and [arm64](https://github.com/mforce/cluckwork/actions/runs/36610454419/attempts/2).
The first 26.04 run rebuilt the API layer, so the reruns are the comparable
warm measurement. No cache-cold 26.04 build was measured. The amd64 warm
step increased from 33 to 57 seconds; these runs do not isolate the runner OS
as the cause.

| Runner image | Architecture | Cold | Warm |
| --- | --- | ---: | ---: |
| Ubuntu 24.04 | amd64 | 63 s | 33 s |
| Ubuntu 24.04 Arm64 | arm64 | 66 s | 32 s |
| Ubuntu 26.04 | amd64 | — | 57 s |
| Ubuntu 26.04 Arm64 | arm64 | — | 28 s |
