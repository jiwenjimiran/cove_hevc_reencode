# HEVC/AV1 Reencode Cove Extension

HEVC/AV1 Reencode is a Cove extension that ports the jiwenji Stash reencode plugin to Cove. It adds a **HEVC/AV1 Reencode** panel under installed extension settings and queues Cove background jobs that run local GPU HEVC or AV1 reencoding through Cove-managed FFmpeg.

## Features

- Settings panel under `Settings -> Extensions -> Installed`
- All Stash reencode options with matching defaults
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

HEVC keeps the Stash plugin defaults: CQ `28`, low-bitrate CQ `34`, aggressive retry CQ `34`, and ultra-aggressive ceiling `40`.

AV1 uses separate CQ-style defaults intended to preserve visual quality while taking advantage of AV1 compression: CQ `30`, low-bitrate CQ `36`, aggressive retry CQ `38`, and ultra-aggressive ceiling `44`.
