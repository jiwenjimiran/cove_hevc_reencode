"""Reproducible NVENC calibration. Media/output stay in ignored artifacts/quality.

Run: python scripts/compare-quality.py
Requires FFmpeg with NVENC/libvmaf and an NVIDIA GPU. Downloads the first 120
frames of each Xiph reference, not the multi-GB complete sequences.
"""
import csv
import json
import re
import subprocess
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORK = ROOT / 'artifacts' / 'quality'
WORK.mkdir(parents=True, exist_ok=True)
SOURCES = ['FourPeople_1280x720_60', 'ducks_take_off_420_720p50',
           'blue_sky_1080p25', 'sintel_trailer_2k_720p24']


def run(args):
    result = subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', *args],
                            cwd=WORK, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stderr[-4000:])


def source(name):
    path = WORK / (name + '.y4m')
    if path.exists():
        return path.name
    url = 'https://media.xiph.org/video/derf/y4m/' + path.name
    print('Downloading first 120 frames:', url, flush=True)
    temp = path.with_suffix('.partial')
    request = urllib.request.Request(url, headers={'Range': 'bytes=0-999'})
    with urllib.request.urlopen(request, timeout=30) as response:
        header = response.readline()
        frame = response.readline()
    w = int(re.search(rb' W(\d+)', header)[1])
    h = int(re.search(rb' H(\d+)', header)[1])
    assert (b'C420' in header or b' C' not in header) and frame == b'FRAME\n', (header, frame)
    total = len(header) + 120 * (len(frame) + w*h*3//2)
    from concurrent.futures import ThreadPoolExecutor
    def fetch(start):
        if start % (20*1024*1024) == 0: print("Download offset", start, flush=True)
        end = min(start+1024*1024, total)-1
        req = urllib.request.Request(url, headers={'Range': f'bytes={start}-{end}'})
        with urllib.request.urlopen(req, timeout=30) as response:
            chunk = response.read()
            assert response.status == 206 and len(chunk) == end-start+1
            return chunk
    with temp.open('wb') as output, ThreadPoolExecutor(max_workers=8) as pool:
        for chunk in pool.map(fetch, range(0, total, 1024*1024)):
            output.write(chunk)
    temp.rename(path)
    return path.name


rows = []
cache = {}
if (WORK/'results.csv').exists():
    with (WORK/'results.csv').open() as file:
        cache = {(r['case'], r['codec'], int(r['quality'])): r for r in csv.DictReader(file)}
for name in SOURCES:
    reference = source(name)
    # Sintel's opening contains fades; others cover faces, texture, camera motion.
    for height, fps, bitrate in [(720, 30, 1800), (720, 60, 3500), (1080, 30, 6000)]:
        if height == 1080 and '1080' not in name:
            continue
        case = f'{name}-{height}-{fps}-{bitrate}'
        inp = case + '.mp4'
        if not (WORK / inp).exists():
            run(['-i', reference, '-vf', f'scale=-2:{height},fps={fps}', '-t', '3',
                 '-c:v', 'libx264', '-preset', 'slow', '-b:v', f'{bitrate}k',
                 '-minrate', f'{bitrate}k', '-maxrate', f'{bitrate}k',
                 '-bufsize', f'{bitrate*2}k', '-x264-params', 'nal-hrd=cbr', '-an', inp])
        low = bitrate <= (2500 if height == 720 else 5000)
        base = 34 if low else 28
        variants = [('old', base)] + [('hevc', q) for q in range(base-4, base+3, 2)]
        variants += [('av1', q) for q in range(base-2, base+11, 2)]
        for codec, q in variants:
            if (case, codec, q) in cache:
                rows.append(cache[(case, codec, q)])
                continue
            stem = f'{case}-{codec}-{q}'
            out = stem + '.mkv'
            metrics = stem + '.json'
            start = time.monotonic()
            args = ['-i', inp, '-an', '-c:v', 'av1_nvenc' if codec == 'av1' else 'hevc_nvenc',
                    '-preset', 'p7', '-rc', 'constqp' if codec == 'old' else 'vbr',
                    '-qp' if codec == 'old' else '-cq', str(q), '-b:v', '0',
                    '-rc-lookahead', '32', '-spatial-aq', '1', '-aq-strength', '8']
            if codec != 'av1':
                args += ['-profile:v', 'main10', '-tier', 'high']
            run(args + [out])
            elapsed = time.monotonic() - start
            run(['-i', out, '-i', inp, '-lavfi',
                 f'[0:v]settb=AVTB,setpts=N/({fps}*TB)[d];[1:v]settb=AVTB,setpts=N/({fps}*TB)[r];[d][r]libvmaf=log_fmt=json:log_path={metrics}:n_threads=8',
                 '-f', 'null', '-'])
            data = json.loads((WORK / metrics).read_text())
            scores = [f['metrics']['vmaf'] for f in data['frames']]
            window = min(fps, len(scores))
            worst = min(sum(scores[i:i+window])/window for i in range(len(scores)-window+1))
            row = dict(case=case, codec=codec, quality=q, mean=data['pooled_metrics']['vmaf']['mean'],
                       worst_second=worst, bytes=(WORK/out).stat().st_size,
                       savings=100*(1-(WORK/out).stat().st_size/(WORK/inp).stat().st_size), seconds=elapsed)
            rows.append(row)
            with (WORK/'results.csv').open('w', newline='') as file:
                writer = csv.DictWriter(file, fieldnames=list(row))
                writer.writeheader()
                writer.writerows(rows)
            print(f"{stem}: VMAF {row['mean']:.2f}, worst second {worst:.2f}, savings {row['savings']:.1f}%", flush=True)

print('Results:', WORK/'results.csv', flush=True)

# Validate and export comparison frames for the selected defaults. Frame count
# equality is essential: VMAF below intentionally aligns by decoded frame index
# because MP4/MKV timestamp rounding otherwise creates false motion penalties.
def probe(path):
    return json.loads(subprocess.check_output([
        'ffprobe', '-v', 'error', '-count_frames', '-select_streams', 'v:0',
        '-show_entries', 'stream=codec_name,width,height,nb_read_frames:format=duration',
        '-of', 'json', str(path)], cwd=WORK))


for case in dict.fromkeys(r['case'] for r in rows):
    low = case.endswith('-1800')
    inp = case + '.mp4'
    original = probe(inp)
    outputs = [f'{case}-hevc-{30 if low else 28}.mkv', f'{case}-av1-{36 if low else 34}.mkv']
    for codec, out in zip(['hevc', 'av1'], outputs):
        encoded = probe(out)
        a, b = original['streams'][0], encoded['streams'][0]
        assert all(a[k] == b[k] for k in ['width', 'height', 'nb_read_frames']), out
        assert b['codec_name'] == codec, out
        assert abs(float(original['format']['duration']) - float(encoded['format']['duration'])) < .1, out
    run(['-ss', '1', '-i', inp, '-ss', '1', '-i', outputs[0], '-ss', '1', '-i', outputs[1],
         '-filter_complex', '[0:v][1:v][2:v]hstack=inputs=3', '-frames:v', '1', case+'-comparison.png'])
print('Selected outputs validated; comparison PNGs: input | HEVC | AV1.', flush=True)
