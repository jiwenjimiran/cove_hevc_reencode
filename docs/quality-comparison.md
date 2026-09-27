# NVIDIA quality comparison ? 0.2.1

Measured on 2026-09-27 with an RTX 4090 and FFmpeg 8.0.1 (Gyan full build).

## Method

- Public references: [Xiph test collection](https://media.xiph.org/video/derf/): FourPeople, ducks_take_off, blue_sky, and the Sintel trailer opening.
- Downloaded the first 120 frames per reference; encoded at most three seconds. FourPeople contributes two seconds and ducks 2.4 seconds. This is a small calibration sample, not a library-wide quality guarantee.
- Created H.264 inputs with libx264 slow at requested 720p/30 fps/1.8 Mbps and 720p/60 fps/3.5 Mbps; added a native-resolution 1080p/30 fps/6 Mbps blue_sky control. Frame-rate conversions duplicate/drop frames; they do not simulate native 60 fps motion.
- Compared the reencode to its H.264 input, not to the original raw reference. Bitrate values identify source construction targets, not measured output targets.
- Measured default-model libvmaf at native resolution, aligning decoded frames by index to avoid MP4/MKV timestamp-rounding errors. Worst second is the lowest rolling one-second mean, not the worst individual frame.
- Swept HEVC CQ 24?30 (normal) / 30?36 (low), and AV1 CQ 26?38 / 32?44. Also measured the old HEVC constant-QP defaults. All sweeps use p7/lookahead 32/spatial AQ 8; no aggressive retry.
- Exported input/HEVC/AV1 comparison PNGs. Checked output codec, dimensions, decoded frame counts, and duration against each input.

## Selected defaults

**HEVC normal/low: 28/30. AV1 normal/low: 34/36.** Existing saved overrides are preserved.

HEVC CQ 28 preserves at least the measured quality of the previous normal QP 28 setting. The low-bitrate HEVC default changes from QP 34 to CQ 30 because retaining the old low-quality target would conflict with the fidelity goal. Higher quality can reduce savings or fail the savings requirement; with retries disabled, the original remains.

| Source / input | HEVC VMAF | AV1 VMAF | HEVC worst second | AV1 worst second | HEVC savings | AV1 savings |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| FourPeople_1280x720_60-720-30-1800 | 95.02 | 95.14 | 95.00 | 95.07 | 33.93% | 31.80% |
| FourPeople_1280x720_60-720-60-3500 | 95.41 | 95.37 | 95.17 | 95.14 | 31.00% | 36.74% |
| ducks_take_off_420_720p50-720-30-1800 | 82.07 | 80.86 | 80.39 | 79.78 | -113.60% | -107.38% |
| ducks_take_off_420_720p50-720-60-3500 | 83.56 | 85.36 | 82.29 | 84.46 | -115.21% | -91.43% |
| blue_sky_1080p25-720-30-1800 | 96.05 | 96.52 | 95.45 | 96.18 | 10.46% | 16.41% |
| blue_sky_1080p25-720-60-3500 | 93.11 | 92.63 | 92.63 | 92.47 | 23.68% | 23.77% |
| blue_sky_1080p25-1080-30-6000 | 96.98 | 97.10 | 96.82 | 96.86 | 33.32% | 41.21% |
| sintel_trailer_2k_720p24-720-30-1800 | 93.39 | 94.23 | 91.47 | 92.56 | 73.59% | 66.80% |
| sintel_trailer_2k_720p24-720-60-3500 | 93.66 | 94.42 | 92.17 | 93.11 | 76.40% | 73.38% |

Maximum codec difference: **1.81 mean VMAF points**, **2.17 worst-second points**. The normal-bitrate ducks sample exceeds the two-point investigation threshold for worst-second score; both outputs are larger than their source and fail the savings gate. No setting achieves exact parity for every scene.

Negative savings mean output growth. At the default 15% minimum, neither ducks encode qualifies, and the low-bitrate blue_sky HEVC output also does not qualify. Those results must not be interpreted as successful filesize reductions.

## Limits and reproduction

- Both selected codecs visibly soften difficult water texture; the sample quality scores are not a promise of transparency. The faces/text and gradient comparison frames were also inspected.
- The test uses short SDR, 8-bit clips. It does not establish HDR, grain-heavy, long-GOP, interlaced, or whole-library behavior. Presets and bit-depth changes were not swept.
- AMD HEVC/AV1 command generation is tested, but AMD hardware quality and performance have **not** been measured. AMF QVBR values are clamped to its 1?51 range and are not calibrated NVENC equivalents.
- Full sweep: [quality-results.csv](quality-results.csv). Encoding times include FFmpeg startup; do not treat them as sustained throughput benchmarks.
- Run `python scripts/compare-quality.py`; media, metric JSON, comparison PNGs, and the CSV are written under `artifacts/quality`. Existing CSV results are reused. For a fresh comparison after changing benchmark settings or FFmpeg/GPU, use a fresh artifacts/quality directory.
- Reference media are not redistributed in the release ZIP. Follow source licensing when reusing downloads.
