#!/usr/bin/env python3
"""A stand-in for yt-dlp used by the automated tests: understands the options Makan passes and behaves like the real tool
(JSON info with -J, machine-readable progress lines, .part files that can be resumed, [Merger] lines, ERROR lines + exit code 1)."""
import json, os, sys, time

args = sys.argv[1:]
url = args[-1]
def opt(name, default=None):
    return args[args.index(name) + 1] if name in args else default

def out(line):
    sys.stdout.write(line + "\n"); sys.stdout.flush()

if "-U" in args:
    out("yt-dlp is up to date"); sys.exit(0)

if "fail" in url:
    sys.stderr.write("ERROR: [youtube] abcdefghijk: Sign in to confirm you're not a bot\n"); sys.exit(1)

if "-J" in args:
    if "unsupported" in url:
        sys.stderr.write("ERROR: Unsupported URL: " + url + "\n"); sys.exit(1)
    if "--flat-playlist" in args:
        if "playlist" in url:
            info = {
                "_type": "playlist", "title": "Wider Shoulders - Full Series",
                "entries": [
                    {"id": "abc111", "title": "Day 1 - Warm Up", "webpage_url": "https://www.youtube.com/watch?v=abc111"},
                    {"id": "abc222", "title": "Day 2 - Shoulders", "url": "https://www.youtube.com/watch?v=abc222"},
                    {"id": "abc333", "title": "Day 3 - Recovery", "url": "abc333"},   # a bare id, like some real extractors give
                    {"id": "", "title": "", "url": ""},   # a broken/removed entry: must be skipped, not crash the whole listing
                ],
            }
        else:
            info = {"title": "The #1 Workout: Wider Shoulders / Fast!"}   # a normal single video: no "entries" at all
        out(json.dumps(info)); sys.exit(0)
    info = {
        "title": "The #1 Workout: Wider Shoulders / Fast!", "duration": 100, "uploader": "Test Channel",
        "formats": [
            {"format_id": "137", "height": 1080, "vcodec": "avc1.640028", "acodec": "none", "filesize": 8_000_000, "ext": "mp4"},
            {"format_id": "248", "height": 1080, "vcodec": "vp9", "acodec": "none", "filesize": 6_000_000, "ext": "webm"},
            {"format_id": "136", "height": 720, "vcodec": "avc1.4d401f", "acodec": "none", "filesize_approx": 4_000_000, "ext": "mp4"},
            {"format_id": "134", "height": 360, "vcodec": "avc1.4d401e", "acodec": "none", "tbr": 800, "ext": "mp4"},
            {"format_id": "18", "height": 360, "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "filesize": 2_000_000, "ext": "mp4"},
            {"format_id": "sb0", "height": 90, "vcodec": "none", "acodec": "none", "ext": "mhtml"},
            {"format_id": "140", "vcodec": "none", "acodec": "mp4a.40.2", "filesize": 1_000_000, "ext": "m4a"},
            {"format_id": "251", "vcodec": "none", "acodec": "opus", "filesize": 900_000, "ext": "webm"},
        ],
        "subtitles": {"en": [{"ext": "vtt"}], "fa": [{"ext": "vtt"}], "live_chat": [{"ext": "json"}]},
        "automatic_captions": {"en": [{"ext": "vtt"}], "de": [{"ext": "vtt"}], "en-orig": [{"ext": "vtt"}]},
    }
    out(json.dumps(info)); sys.exit(0)

# ---- download
template = opt("--progress-template", "download:MAKAN|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s")
template = template.split(":", 1)[1]
base = opt("-o").replace("%%", "%").replace(".%(ext)s", "")
slow = "slow" in url
delay = 0.12 if slow else 0.004

def progress(status, done, total, fmt):
    out(template.replace("%(progress.status)s", status).replace("%(progress.downloaded_bytes)s", str(done))
        .replace("%(progress.total_bytes)s", str(total)).replace("%(progress.total_bytes_estimate)s", "NA")
        .replace("%(progress.speed)s", "1048576.5").replace("%(progress.eta)s", "3").replace("%(info.format_id)s", fmt))

def fetch(path, total, fmt):
    """Writes total bytes into path + .part in 40 steps; continues a partial file (like yt-dlp -c)."""
    part = path + ".part"
    have = os.path.getsize(part) if os.path.exists(part) else 0
    step = max(1, total // 40)
    with open(part, "ab") as f:
        while have < total:
            n = min(step, total - have)
            f.write(bytes([65 + (have // step) % 26]) * n); f.flush(); have += n
            progress("downloading", have, total, fmt); time.sleep(delay)
    os.replace(part, path)
    progress("finished", total, total, fmt)

if "--skip-download" in args:                      # subtitles
    lang = opt("--sub-langs", "en")
    with open(f"{base}.{lang}.srt", "w", encoding="utf-8") as f: f.write("1\n00:00:00,000 --> 00:00:01,000\nhello\n")
    sys.exit(0)

selector = opt("-f", "")
if "-x" in args:                                   # mp3
    fetch(base + ".f140.m4a", 1_000_000, "140")
    out('[ExtractAudio] Destination: ' + base + ".mp3")
    with open(base + ".mp3", "wb") as f: f.write(open(base + ".f140.m4a", "rb").read())
    os.remove(base + ".f140.m4a"); sys.exit(0)
if selector.startswith("ba"):                      # audio only (m4a)
    fetch(base + ".m4a", 1_000_000, "140"); sys.exit(0)

fetch(base + ".f137.mp4", 4_000_000, "137")        # video-only, then audio, then merge
fetch(base + ".f140.m4a", 1_000_000, "140")
out(f'[Merger] Merging formats into "{base}.mp4"')
with open(base + ".mp4", "wb") as f:
    f.write(open(base + ".f137.mp4", "rb").read()); f.write(open(base + ".f140.m4a", "rb").read())
os.remove(base + ".f137.mp4"); os.remove(base + ".f140.m4a")
