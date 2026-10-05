#!/usr/bin/env python3
"""Local HTTP server that mimics the behaviours a download manager has to survive."""
import hashlib, os, random, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 18080
rnd = random.Random(1234)
BIG = rnd.randbytes(40 * 1024 * 1024)          # 40 MB, > 32MB => 4+ connections
MID = rnd.randbytes(6 * 1024 * 1024)
SMALL = rnd.randbytes(300 * 1024)
FILES = {"/big.bin": BIG, "/mid.bin": MID, "/small.bin": SMALL}
STATS = {"hits": {}, "bytes": 0, "flaky_left": 6, "throttle_left": 2, "cookie_at": {}, "fseg_left": 2, "ua": {}}
LOCK = threading.Lock()

def sha(b): return hashlib.sha256(b).hexdigest()

# ---------------------------------------------------------------- DASH fixtures
def dseg(rep, name, size): return random.Random(f"dash-{rep}-{name}").randbytes(size)
NS = 'xmlns="urn:mpeg:dash:schema:mpd:2011"'
DASH_MPDS = {
    "/dash/static.mpd": f"""<?xml version="1.0"?><MPD {NS} type="static" mediaPresentationDuration="PT12S"><Period>
<AdaptationSet contentType="video" mimeType="video/mp4">
 <SegmentTemplate timescale="1000" duration="4000" startNumber="1" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/seg-$Number%03d$.m4s"/>
 <Representation id="v1" bandwidth="500000" width="640" height="360" codecs="avc1.4d401e"/>
 <Representation id="v2" bandwidth="1500000" width="1280" height="720" codecs="avc1.4d401f"/>
</AdaptationSet>
<AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en">
 <Representation id="a1" bandwidth="96000" codecs="mp4a.40.2">
  <SegmentTemplate timescale="1000" initialization="a1/init.mp4" media="a1/seg-$Number$.m4s" startNumber="1"><SegmentTimeline><S t="0" d="4000" r="2"/></SegmentTimeline></SegmentTemplate>
 </Representation>
</AdaptationSet></Period></MPD>""",
    "/dash/time.mpd": f"""<MPD {NS} type="static" mediaPresentationDuration="PT9S"><Period><AdaptationSet mimeType="video/mp4">
 <Representation id="t1" bandwidth="800000" width="854" height="480" codecs="avc1.4d401e">
  <SegmentTemplate timescale="1000" initialization="t1/init.mp4" media="$RepresentationID$/t-$Time$.m4s"><SegmentTimeline><S t="0" d="2000" r="1"/><S d="5000"/></SegmentTimeline></SegmentTemplate>
 </Representation></AdaptationSet></Period></MPD>""",
    "/dash/list.mpd": f"""<MPD {NS} type="static" mediaPresentationDuration="PT8S"><BaseURL>list/</BaseURL><Period><AdaptationSet mimeType="video/mp4">
 <Representation id="L" bandwidth="600000" width="640" height="360" codecs="avc1.4d401e">
  <SegmentList timescale="1000" duration="4000"><Initialization sourceURL="init.mp4"/><SegmentURL media="s1.m4s"/><SegmentURL media="s2.m4s"/></SegmentList>
 </Representation></AdaptationSet></Period></MPD>""",
    "/dash/live.mpd": f"""<MPD {NS} type="dynamic"><Period><AdaptationSet mimeType="video/mp4"><Representation id="x" bandwidth="1"><SegmentTemplate media="x-$Number$.m4s" duration="2" startNumber="1"/></Representation></AdaptationSet></Period></MPD>""",
    "/dash/drm.mpd": f"""<MPD {NS} type="static" mediaPresentationDuration="PT4S"><Period><AdaptationSet mimeType="video/mp4"><ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
 <Representation id="e" bandwidth="1" width="640" height="360"><SegmentTemplate media="e-$Number$.m4s" duration="4" startNumber="1"/></Representation></AdaptationSet></Period></MPD>""",
    "/dash/base.mpd": f"""<MPD {NS} type="static" mediaPresentationDuration="PT4S"><Period><AdaptationSet mimeType="video/mp4"><Representation id="b" bandwidth="1" width="640" height="360"><BaseURL>file.mp4</BaseURL><SegmentBase indexRange="0-100"/></Representation></AdaptationSet></Period></MPD>""",
}
def dash_expected():
    init = lambda rep, size=5000: dseg(rep, "init.mp4", size)
    v2 = init("v2") + b"".join(dseg("v2", f"seg-{n:03d}.m4s", 100000) for n in (1, 2, 3))
    a1 = init("a1") + b"".join(dseg("a1", f"seg-{n}.m4s", 40000) for n in (1, 2, 3))
    return {
        "dash-v2": v2, "dash-a1": a1, "dash-v2a1": v2 + a1,
        "dash-v1": init("v1") + b"".join(dseg("v1", f"seg-{n:03d}.m4s", 100000) for n in (1, 2, 3)),
        "dash-time": init("t1") + b"".join(dseg("t1", f"t-{t}.m4s", 30000) for t in (0, 2000, 4000)),
        "dash-list": dseg("list", "init.mp4", 5000) + b"".join(dseg("list", f"s{n}.m4s", 20000) for n in (1, 2)),
    }

# ---------------------------------------------------------------- HLS fixtures
HLS_KEY = bytes(range(16, 32))
def hseg(prefix, n, size=300 * 1024): return random.Random(f"{prefix}-{n}").randbytes(size)
def aes_enc(data, iv):
    from cryptography.hazmat.primitives import padding
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    padder = padding.PKCS7(128).padder(); padded = padder.update(data) + padder.finalize()
    enc = Cipher(algorithms.AES(HLS_KEY), modes.CBC(iv)).encryptor()
    return enc.update(padded) + enc.finalize()
FIXED_IV = bytes(range(16))
def seq_iv(seq): return seq.to_bytes(16, "big")
INIT = random.Random("init").randbytes(20000)
ALLBIN = random.Random("all").randbytes(400000)
FILES["/hls/all.bin"] = ALLBIN

def pl(*lines, end=True):
    out = ["#EXTM3U", "#EXT-X-VERSION:3", "#EXT-X-TARGETDURATION:4"] + list(lines)
    if end: out.append("#EXT-X-ENDLIST")
    return ("\n".join(out) + "\n").encode()
def segs(prefix, count, ext="ts"):
    out = []
    for n in range(count): out += ["#EXTINF:4.000,", f"{prefix}/{n}.{ext}"]
    return out

HLS_PLAYLISTS = {
    "/hls/vod.m3u8": lambda: pl(*segs("seg", 12)),
    "/hls/small.m3u8": lambda: pl(*segs("seg", 3)),
    "/hls/enc.m3u8": lambda: pl('#EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x' + FIXED_IV.hex(), *segs("eseg", 6)),
    "/hls/enc-seq.m3u8": lambda: pl("#EXT-X-MEDIA-SEQUENCE:7", '#EXT-X-KEY:METHOD=AES-128,URI="key.bin"', *segs("eqseg", 5)),
    "/hls/drm.m3u8": lambda: pl('#EXT-X-KEY:METHOD=SAMPLE-AES,URI="skd://x"', *segs("seg", 3)),
    "/hls/fmp4.m3u8": lambda: pl('#EXT-X-MAP:URI="init.mp4"', *segs("frag", 5, "m4s")),
    "/hls/live.m3u8": lambda: pl(*segs("seg", 3), end=False),
    "/hls/master2.m3u8": lambda: b"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=426x240\nsmall.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720\nvod.m3u8\n",
    "/hls/master-audio.m3u8": lambda: (b'#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="en",DEFAULT=YES,URI="audio-en.m3u8"\n'
                                       b'#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360,AUDIO="aud"\nvonly.m3u8\n'),
    "/hls/vonly.m3u8": lambda: pl(*segs("vseg", 4)),
    "/hls/audio-en.m3u8": lambda: pl(*segs("aseg", 4, "aac")),
    "/hls/missing.m3u8": lambda: pl(*segs("seg", 3), "#EXTINF:4.000,", "gone.ts"),
    "/hls/flaky.m3u8": lambda: pl(*segs("fseg", 6)),
    "/hls/slow.m3u8": lambda: pl(*segs("sseg", 24)),
    "/hls/range.m3u8": lambda: pl("#EXTINF:4,", "#EXT-X-BYTERANGE:100000@0", "all.bin", "#EXTINF:4,", "#EXT-X-BYTERANGE:100000@100000", "all.bin",
                                  "#EXTINF:4,", "#EXT-X-BYTERANGE:100000", "all.bin", "#EXTINF:4,", "#EXT-X-BYTERANGE:100000", "all.bin"),
    "/hls/cookie.m3u8": lambda: pl(*segs("cseg", 4)),
    "/hls/html.m3u8": lambda: b"<html>please log in</html>",
}
def hls_expected():
    cat = lambda prefix, n, size=300 * 1024: b"".join(hseg(prefix, i, size) for i in range(n))
    return {
        "vod": cat("seg", 12), "master2": cat("seg", 12), "enc": cat("eseg", 6), "fmp4": INIT + cat("frag", 5, 100000),
        "vonly": cat("vseg", 4), "aonly": cat("aseg", 4, 50000), "vplusa": cat("vseg", 4) + cat("aseg", 4, 50000),
        "range": ALLBIN, "flaky": cat("fseg", 6), "slow": cat("sseg", 24, 200 * 1024), "cookie": cat("cseg", 4),
        "xhost": cat("xseg", 4), "enc-seq": cat("eqseg", 5, 100000),
    }

class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *a): pass

    def _hit(self):
        with LOCK: STATS["hits"][self.path] = STATS["hits"].get(self.path, 0) + 1

    def do_HEAD(self): self._serve(head=True)
    def do_GET(self):  self._serve(head=False)

    def _serve(self, head):
        self._hit()
        path = self.path.split("?")[0]
        if path == "/__stats":
            import json
            body = json.dumps({"hits": STATS["hits"], "bytes": STATS["bytes"],
                               "sha": {k: sha(v) for k, v in FILES.items()}, "cookie_at": STATS["cookie_at"], "ua": STATS["ua"]}).encode()
            return self._plain(200, body, "application/json")
        if path == "/__reset":
            with LOCK:
                STATS["hits"].clear(); STATS["cookie_at"].clear(); STATS["ua"].clear(); STATS["bytes"] = 0; STATS["flaky_left"] = 6; STATS["throttle_left"] = 2; STATS["fseg_left"] = 2
            return self._plain(200, b"ok")
        if path == "/hls/master.m3u8":
            if self.headers.get("Cookie") != "session=abc" or self.headers.get("Referer") != "https://site.example/watch":
                return self._plain(403, b"auth")
            body = ("#EXTM3U\n"
                    '#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="en",DEFAULT=YES,URI="audio/en.m3u8"\n'
                    '#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="fr",DEFAULT=NO,URI="audio/fr.m3u8"\n'
                    '#EXT-X-STREAM-INF:BANDWIDTH=800000,CODECS="avc1.64001f,mp4a.40.2",RESOLUTION=640x360,AUDIO="aud"\n'
                    "v360/index.m3u8\n"
                    '#EXT-X-STREAM-INF:BANDWIDTH=2800000,CODECS="avc1.640028,mp4a.40.2",RESOLUTION=1280x720,AUDIO="aud"\n'
                    "v720/index.m3u8\n").encode()
            return self._plain(200, body, "application/vnd.apple.mpegurl")
        if path == "/hls/plain.m3u8":   # muxed variants: audio inside the video playlist
            body = ("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=854x480\nlow.m3u8\n").encode()
            return self._plain(200, body, "application/vnd.apple.mpegurl")
        if path == "/xhost":      # redirect to a DIFFERENT host name (localhost vs 127.0.0.1)
            self.send_response(302); self.send_header("Location", f"http://localhost:{PORT}/landing-x"); self.send_header("Content-Length", "0"); self.end_headers(); return
        if path == "/samehost":
            self.send_response(302); self.send_header("Location", "/landing-s"); self.send_header("Content-Length", "0"); self.end_headers(); return
        if path in ("/landing-x", "/landing-s"):
            with LOCK: STATS["cookie_at"][path] = self.headers.get("Cookie") or ""
            return self._data(SMALL, head)
        if path.startswith("/__expect/"):
            key = path.split("/")[-1]
            table = dash_expected() if key.startswith("dash-") else hls_expected()
            return self._plain(200, sha(table[key]).encode())
        if path in DASH_MPDS: return self._plain(200, DASH_MPDS[path].encode(), "application/dash+xml")
        import re as _re2
        md = _re2.match(r"^/dash/([A-Za-z0-9]+)/(init\.mp4|seg-\d+\.m4s|t-\d+\.m4s|s\d+\.m4s)$", path)
        if md:
            rep, name = md.group(1), md.group(2)
            size = 5000 if name == "init.mp4" else (40000 if rep == "a1" else 30000 if name.startswith("t-") else 20000 if rep == "list" else 100000)
            return self._data(dseg(rep, name, size), head)
        if path == "/hls/key.bin": return self._plain(200, HLS_KEY, "application/octet-stream")
        if path in HLS_PLAYLISTS and path != "/hls/master.m3u8":
            if path == "/hls/cookie.m3u8" and "session=abc" not in (self.headers.get("Cookie") or ""): return self._plain(403, b"cookie required")
            if path == "/hls/html.m3u8": return self._plain(200, HLS_PLAYLISTS[path](), "text/html")
            body = HLS_PLAYLISTS[path]()
            if path == "/hls/cookie.m3u8":
                body = body.replace(b"cseg/", b"cseg/")
            return self._plain(200, body, "application/vnd.apple.mpegurl")
        if path == "/hls/xhost.m3u8":
            lines = []
            for n in range(4): lines += ["#EXTINF:4.000,", f"http://localhost:{PORT}/hls/xseg/{n}.ts"]
            return self._plain(200, pl(*lines), "application/vnd.apple.mpegurl")
        if path == "/hls/init.mp4": return self._data(INIT, head)
        import re as _re
        m = _re.match(r"^/hls/(seg|eseg|eqseg|frag|vseg|aseg|fseg|sseg|cseg|xseg)/(\d+)\.(ts|m4s|aac)$", path)
        if m:
            kind, n = m.group(1), int(m.group(2))
            if kind in ("cseg", "xseg"):
                with LOCK: STATS["cookie_at"][path] = self.headers.get("Cookie") or ""
                if kind == "cseg" and "session=abc" not in (self.headers.get("Cookie") or ""): return self._plain(403, b"cookie required")
            if kind == "fseg" and n == 3:
                with LOCK:
                    left = STATS["fseg_left"]
                    if left > 0: STATS["fseg_left"] = left - 1
                if left > 0: return self._plain(503, b"busy")
            if kind == "sseg": time.sleep(0.3)
            size = {"frag": 100000, "aseg": 50000, "sseg": 200 * 1024, "eqseg": 100000}.get(kind, 300 * 1024)
            data = hseg(kind, n, size)
            if kind == "eseg": data = aes_enc(data, FIXED_IV)
            if kind == "eqseg": data = aes_enc(data, seq_iv(7 + n))
            return self._data(data, head)
        if path.startswith("/tools/"):
            import zipfile as _zf, io as _io
            exe = b"MZ fake yt-dlp executable " + b"x" * 5000
            deno_zip = _io.BytesIO(); z = _zf.ZipFile(deno_zip, "w"); z.writestr("deno.exe", b"MZ fake deno"); z.close(); deno_zip = deno_zip.getvalue()
            ff_zip = _io.BytesIO(); z = _zf.ZipFile(ff_zip, "w"); z.writestr("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe", b"MZ fake ffmpeg"); z.writestr("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe", b"MZ fake ffprobe"); z.close(); ff_zip = ff_zip.getvalue()
            table = {
                "/tools/yt-dlp.exe": exe,
                "/tools/SHA2-256SUMS": (sha(exe) + "  yt-dlp.exe\n" + "0" * 64 + "  yt-dlp\n").encode(),
                "/tools/SHA2-256SUMS-bad": ("f" * 64 + "  yt-dlp.exe\n").encode(),
                "/tools/deno.zip": deno_zip,
                "/tools/deno.zip.sha256sum": (sha(deno_zip) + "  deno-x86_64-pc-windows-msvc.zip\n").encode(),
                "/tools/ffmpeg-master-latest-win64-gpl.zip": ff_zip,
                "/tools/checksums.sha256": (sha(ff_zip) + "  ffmpeg-master-latest-win64-gpl.zip\n").encode(),
            }
            if path in table: return self._plain(200, table[path], "application/octet-stream")
            return self._plain(404, b"nope")
        if path == "/ua.bin":
            with LOCK: STATS["ua"][self.path] = self.headers.get("User-Agent") or ""
            return self._data(SMALL, head)
        if path == "/notfound": return self._plain(404, b"nope")
        if path == "/forbidden": return self._plain(403, b"no")
        if path == "/html": return self._plain(200, b"<html>please log in</html>", "text/html")
        if path == "/needs-cookie":
            if "session=abc" not in (self.headers.get("Cookie") or ""):
                return self._plain(403, b"cookie required")
            return self._data(SMALL, head)
        if path == "/cd":  # generic URL, real name only in Content-Disposition
            return self._data(SMALL, head, extra={"Content-Disposition": 'attachment; filename="Quarterly Report (final).pdf"'})
        if path == "/cd-utf8":
            return self._data(SMALL, head, extra={"Content-Disposition": "attachment; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf"})
        if path == "/redir":
            self.send_response(302); self.send_header("Location", "/files/moved-name.zip"); self.send_header("Content-Length", "0"); self.end_headers(); return
        if path == "/files/moved-name.zip": return self._data(SMALL, head)
        if path == "/norange.bin": return self._data(MID, head, ranges=False)
        if path == "/norange-slow.bin": return self._data(BIG, head, ranges=False, slow=True)   # no ranges and slow: one connection that can be watched
        if path == "/throttle.bin":
            with LOCK:
                left = STATS["throttle_left"]
                if left > 0: STATS["throttle_left"] -= 1
            if left > 0:
                self.send_response(429); self.send_header("Retry-After", "1"); self.send_header("Content-Length", "0"); self.end_headers(); return
            return self._data(MID, head)
        if path == "/flaky.bin":  # closes the socket mid-body on the first few requests
            return self._data(MID, head, drop_after=1_000_000 if self._take_flaky() else None)
        if path == "/slow.bin":   # ~ 4 MB/s so pause/cancel can be exercised mid-flight
            return self._data(BIG, head, slow=True)
        if path in FILES: return self._data(FILES[path], head)
        return self._plain(404, b"unknown")

    def _take_flaky(self):
        with LOCK:
            if STATS["flaky_left"] > 0:
                STATS["flaky_left"] -= 1; return True
        return False

    def _plain(self, code, body, ctype="text/plain"):
        self.send_response(code); self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body))); self.end_headers()
        if self.command != "HEAD": self.wfile.write(body)

    def _data(self, data, head, ranges=True, drop_after=None, slow=False, extra=None):
        total = len(data); start, end, code = 0, total - 1, 200
        rng = self.headers.get("Range")
        if ranges and rng and rng.startswith("bytes="):
            a, _, b = rng[6:].partition("-")
            start = int(a) if a else 0
            end = min(int(b), total - 1) if b else total - 1
            if start >= total:
                self.send_response(416); self.send_header("Content-Range", f"bytes */{total}"); self.send_header("Content-Length", "0"); self.end_headers(); return
            code = 206
        length = end - start + 1
        self.send_response(code)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(length))
        self.send_header("ETag", '"v1-%d"' % total)
        self.send_header("Last-Modified", "Tue, 03 Mar 2020 10:20:30 GMT")
        if ranges: self.send_header("Accept-Ranges", "bytes")
        if code == 206: self.send_header("Content-Range", f"bytes {start}-{end}/{total}")
        for k, v in (extra or {}).items(): self.send_header(k, v)
        self.end_headers()
        if head: return
        pos, sent = start, 0
        try:
            while pos <= end:
                n = min(64 * 1024, end - pos + 1)
                if drop_after is not None and sent >= drop_after:
                    self.close_connection = True; self.wfile.flush(); self.connection.shutdown(2); return
                self.wfile.write(data[pos:pos + n]); pos += n; sent += n
                with LOCK: STATS["bytes"] += n
                if slow: time.sleep(0.015)
        except (BrokenPipeError, ConnectionResetError, OSError):
            self.close_connection = True

class TestHTTPServer(ThreadingHTTPServer):
    # HLS downloads deliberately open many segment connections together. The stdlib default backlog is only 5,
    # which can reject healthy bursts on Windows CI before handler threads have a chance to accept them.
    request_queue_size = 128

if __name__ == "__main__":
    srv = TestHTTPServer(("127.0.0.1", PORT), H)
    srv.daemon_threads = True
    print("listening", PORT, flush=True)
    srv.serve_forever()
