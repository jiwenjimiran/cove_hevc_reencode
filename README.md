# HEVC Reencode Cove Extension

HEVC Reencode is a Cove extension that ports the jiwenji Stash reencode plugin to Cove. It adds a **HEVC Reencode** panel under installed extension settings and queues Cove background jobs that run local GPU HEVC reencoding through Cove-managed FFmpeg.

## Features

- Settings panel under `Settings -> Extensions -> Installed`
- All Stash reencode options with matching defaults
- Local encoder health check
- Video detail and bulk actions for queueing HEVC reencode jobs
- Background job progress bridged into Cove's job system
- Native Cove/.NET orchestration with no Python worker or Docker sidecar

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

The extension uses `ffmpeg` and `ffprobe` from Cove's configured/managed FFmpeg installation or from PATH. It probes for GPU HEVC encoders and supports:

- `hevc_nvenc`
- `hevc_amf`

CPU `libx265` fallback is intentionally disabled. If no GPU HEVC encoder works, jobs fail with a clear encoder health error.

## Current Limitations

This port replaces files in place so existing Cove metadata stays attached to the same video/file record. Suffix outputs and failure tag application are represented in settings for compatibility, but the full Cove integrations are still follow-up work.
