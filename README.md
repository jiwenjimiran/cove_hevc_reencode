# HEVC/AV1 Reencode Cove Extension

HEVC/AV1 Reencode is a Cove extension that ports the jiwenji Stash reencode plugin to Cove. It adds a **HEVC/AV1 Reencode** panel under installed extension settings and queues Cove background jobs that run local GPU HEVC or AV1 reencoding through Cove-managed FFmpeg.

## Features

- Settings panel under `Settings -> Extensions -> Installed`
- Configurable quality settings and optional aggressive retries
- Local encoder health check
- Video detail and bulk actions for queueing reencode jobs
- Background job progress bridged into Cove's job system
- Native Cove/.NET orchestration with no Python worker or Docker sidecar
- HEVC and AV1 output formats with separate quality defaults
- Optional suffix outputs when keeping original files
- Multi-engine encode concurrency with NVIDIA engine auto-detection where possible

## Build

```powershell
dotnet build .\SingleExtensionTemplate.slnx -c Release
```

To force package-mode contracts, matching CI:

```powershell
dotnet build .\SingleExtensionTemplate.slnx -c Release -p:UseLocalCovePlugins=false
```

## Package

The CI workflow publishes `src/HevcReencode/HevcReencode.csproj`, copies `extension.json` to the package root, creates `cove.community.ai.hevc-reencode-<version>.zip`, and attaches it to tags named `v<version>`.

## Encoder

The extension uses `ffmpeg` and `ffprobe` from Cove's configured/managed FFmpeg installation or from PATH. It probes for GPU encoders and supports:

- `hevc_nvenc`
- `hevc_amf`
- `av1_nvenc`
- `av1_amf`

CPU fallback is intentionally disabled. If no matching GPU encoder works, jobs fail with a clear encoder health error.

## Quality Defaults

Both codecs use quality-targeted variable bitrate: NVENC VBR/CQ or AMD AMF QVBR.
NVIDIA uses preset `p7`, lookahead 32 and spatial AQ strength 8 for both codecs.
AMD uses codec-specific quality presets and explicit preanalysis. AMF hardware
quality parity has not been measured; its quality scale is not NVENC CQ.

| Setting | HEVC | AV1 |
| --- | ---: | ---: |
| Normal quality | 28 | 34 |
| Low-bitrate quality | 30 | 36 |
| Aggressive retry | 34 | 38 |
| Ultra-aggressive ceiling | 40 | 44 |

Aggressive retries default to **off**. They trade quality for size, and when
explicitly enabled retain the existing retry/savings behavior. Existing saved
settings are preserved on upgrade, including an enabled retry toggle and previous
quality values. To adopt the new defaults on an existing install, enter the values
above and disable aggressive retries in extension settings.

The source-bitrate classification is unchanged: 720p sources at or below 2.5 Mbps
use the low-bitrate setting. This is not an output bitrate cap. Quality-targeted
encoding can produce larger files; the minimum-savings check retains originals
when output does not qualify. Numeric quality values are not interchangeable
between codecs or vendors. CQ 0 on NVENC means automatic, not lossless.

See [quality comparison](docs/quality-comparison.md) for sample measurements and
limitations. Equal visual quality on every source is not guaranteed.

## Local package and checks

```powershell
./scripts/package.ps1
dotnet run --project tests/EncoderChecks -c Release -p:UseLocalCovePlugins=false
# Optional real NVIDIA health probes:
dotnet run --project tests/EncoderChecks -c Release -p:UseLocalCovePlugins=false -- --gpu
```

The package version comes from `extension.json`; an explicit `-Version` must
match. The script builds using the pinned Cove.Plugins package and writes
`artifacts/cove.community.ai.hevc-reencode-<version>.zip`. It does not install it.
CI uploads the ZIP for PRs and manual runs; a matching `v<version>` tag creates a
GitHub release with the ZIP and `docs/release-notes/v<version>.md` notes.

To reproduce the NVIDIA calibration with FFmpeg/libvmaf and Python:

```powershell
python scripts/compare-quality.py
```

Reference downloads and comparison outputs stay in ignored `artifacts/quality`.
