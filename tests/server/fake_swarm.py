#!/usr/bin/env python3
"""A tiny BitTorrent swarm for the automated tests (real protocol on localhost, no internet).

  fake_swarm.py serve DIR [--files a.bin:300000,sub/b.bin:1000000] [--piece 65536] [--seeders 2] [--rate BYTES_PER_SEC]
                          [--corrupt-piece N] [--private] [--no-tracker] [--seed-name NAME]
      creates DIR/<name>.torrent and the data, starts seeders + an HTTP tracker + a UDP tracker + a DHT node, prints ONE json line, then serves until killed.
  fake_swarm.py leech HOST PORT TORRENT [--max-pieces N]
      connects to a client that is seeding, downloads every piece and prints one json line with the verified result.
"""
import asyncio, hashlib, json, os, socket, struct, sys, threading, random, base64
from http.server import BaseHTTPRequestHandler, HTTPServer

# ------------------------------------------------------------------ bencode
def benc(x):
    if isinstance(x, int): return b"i%de" % x
    if isinstance(x, bytes): return b"%d:%s" % (len(x), x)
    if isinstance(x, str): return benc(x.encode())
    if isinstance(x, list): return b"l" + b"".join(benc(i) for i in x) + b"e"
    if isinstance(x, dict): return b"d" + b"".join(benc(k) + benc(v) for k, v in sorted((k if isinstance(k, bytes) else k.encode(), v) for k, v in x.items())) + b"e"
    raise TypeError(type(x))

def bdec(data, i=0):
    c = data[i:i+1]
    if c == b"i":
        j = data.index(b"e", i); return int(data[i+1:j]), j + 1
    if c == b"l":
        i += 1; out = []
        while data[i:i+1] != b"e":
            v, i = bdec(data, i); out.append(v)
        return out, i + 1
    if c == b"d":
        i += 1; out = {}
        while data[i:i+1] != b"e":
            k, i = bdec(data, i); v, i = bdec(data, i); out[k] = v
        return out, i + 1
    j = data.index(b":", i); n = int(data[i:j]); return data[j+1:j+1+n], j + 1 + n

# ------------------------------------------------------------------ content
def make_data(files, piece, name, seed=b"makan"):
    """files: [(relpath, length)] -> (payload bytes, torrent dict bytes, infohash)"""
    stream = bytearray()
    for rel, length in files:
        h = hashlib.sha256(seed + rel.encode()).digest(); block = bytearray()
        while len(block) < length: h = hashlib.sha256(h).digest(); block += h
        stream += block[:length]
    payload = bytes(stream)
    pieces = b"".join(hashlib.sha1(payload[i:i+piece]).digest() for i in range(0, len(payload), piece))
    info = {"name": name, "piece length": piece, "pieces": pieces}
    if len(files) == 1 and "/" not in files[0][0]: info["length"] = files[0][1]
    else: info["files"] = [{"length": l, "path": r.split("/")} for r, l in files]
    return payload, info

# ------------------------------------------------------------------ seeder
class Seeder:
    def __init__(self, payload, info, infohash, piece, rate=0, corrupt=None, tracker_lie=False):
        self.payload, self.info, self.infohash, self.piece, self.rate, self.corrupt = payload, info, infohash, piece, rate, corrupt
        self.raw_info = benc(info); self.corrupted = set(); self.port = 0; self.requests = 0; self.peer_ut_metadata = 2
        self.npieces = (len(payload) + piece - 1) // piece

    async def start(self):
        self.server = await asyncio.start_server(self.handle, "127.0.0.1", 0)
        self.port = self.server.sockets[0].getsockname()[1]

    async def handle(self, reader, writer):
        try:
            hs = await reader.readexactly(68)
            if hs[28:48] != self.infohash: writer.close(); return
            reserved = bytearray(8); reserved[5] |= 0x10
            writer.write(b"\x13BitTorrent protocol" + bytes(reserved) + self.infohash + b"-FS0001-" + os.urandom(12))
            ext_hs = benc({"m": {"ut_metadata": 3}, "metadata_size": len(self.raw_info), "v": "FakeSeeder"})
            writer.write(struct.pack(">IBB", len(ext_hs) + 2, 20, 0) + ext_hs)
            bf = bytearray((self.npieces + 7) // 8)
            for i in range(self.npieces): bf[i // 8] |= 0x80 >> (i % 8)
            writer.write(struct.pack(">IB", len(bf) + 1, 5) + bytes(bf))
            await writer.drain()
            while True:
                (length,) = struct.unpack(">I", await reader.readexactly(4))
                if length == 0: continue
                msg = await reader.readexactly(length); mid = msg[0]
                if mid == 2:                                          # interested -> unchoke
                    writer.write(struct.pack(">IB", 1, 1)); await writer.drain()
                elif mid == 6:                                        # request
                    idx, off, ln = struct.unpack(">III", msg[1:13]); self.requests += 1
                    start = idx * self.piece + off
                    data = self.payload[start:start + ln]
                    if self.corrupt is not None and idx == self.corrupt and idx not in self.corrupted and off + ln >= min(self.piece, len(self.payload) - idx * self.piece):
                        data = bytes(b ^ 0xFF for b in data); self.corrupted.add(idx)
                    elif self.corrupt is not None and idx == self.corrupt and idx not in self.corrupted:
                        data = bytes(b ^ 0xFF for b in data)
                    if self.rate: await asyncio.sleep(len(data) / self.rate)
                    writer.write(struct.pack(">IBII", 9 + len(data), 7, idx, off) + data); await writer.drain()
                elif mid == 20:
                    if msg[1] == 0:                                   # the peer's extension handshake: learn which id it uses for ut_metadata
                        hd, _ = bdec(msg, 2); self.peer_ut_metadata = hd.get(b"m", {}).get(b"ut_metadata", 2)
                    elif msg[1] == 3:                                 # ut_metadata request (our own id)
                        d, end = bdec(msg, 2)
                        if d.get(b"msg_type") == 0:
                            p = d[b"piece"]; chunk = self.raw_info[p * 16384:(p + 1) * 16384]
                            head = benc({"msg_type": 1, "piece": p, "total_size": len(self.raw_info)})
                            body = bytes([20, self.peer_ut_metadata]) + head + chunk
                            writer.write(struct.pack(">I", len(body)) + body); await writer.drain()
        except (asyncio.IncompleteReadError, ConnectionError): pass
        finally:
            try: writer.close()
            except Exception: pass

# ------------------------------------------------------------------ trackers / dht
def compact(ports): return b"".join(socket.inet_aton("127.0.0.1") + struct.pack(">H", p) for p in ports)

def start_http_tracker(ports, infohash, events):
    class H(BaseHTTPRequestHandler):
        def log_message(self, *a): pass
        def do_GET(self):
            from urllib.parse import urlparse, parse_qs, unquote_to_bytes
            q = urlparse(self.path)
            if q.path != "/announce": self.send_response(404); self.end_headers(); return
            raw = dict(p.split("=", 1) for p in q.query.split("&") if "=" in p)
            ih = unquote_to_bytes(raw.get("info_hash", ""))
            events.append({"kind": "http", "event": raw.get("event", ""), "left": raw.get("left"), "port": raw.get("port")})
            if ih != infohash: body = benc({"failure reason": "unknown torrent"})
            else: body = benc({"interval": 1, "complete": len(ports), "incomplete": 0, "peers": compact(ports)})
            self.send_response(200); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    srv = HTTPServer(("127.0.0.1", 0), H); threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv.server_address[1]

class UdpTracker(asyncio.DatagramProtocol):
    def __init__(self, ports, infohash, events): self.ports, self.infohash, self.events = ports, infohash, events
    def connection_made(self, t): self.t = t
    def datagram_received(self, data, addr):
        if len(data) >= 16:
            cid, action, tid = struct.unpack(">QII", data[:16])
            if action == 0 and cid == 0x41727101980:
                self.t.sendto(struct.pack(">IIQ", 0, tid, 0x1122334455667788), addr)
            elif action == 1 and len(data) >= 98 and cid == 0x1122334455667788:
                ih = data[16:36]
                self.events.append({"kind": "udp", "event": struct.unpack(">I", data[80:84])[0]})
                if ih == self.infohash: self.t.sendto(struct.pack(">IIIII", 1, tid, 1, 0, len(self.ports)) + compact(self.ports), addr)
                else: self.t.sendto(struct.pack(">II", 3, tid) + b"unknown torrent", addr)

class Dht(asyncio.DatagramProtocol):
    def __init__(self, ports, infohash): self.ports, self.infohash = ports, infohash; self.queries = []
    def connection_made(self, t): self.t = t
    def datagram_received(self, data, addr):
        try: msg, _ = bdec(data)
        except Exception: return
        tid = msg.get(b"t", b"aa"); q = msg.get(b"q"); nid = b"F" * 20
        self.queries.append(q)
        if q == b"ping": self.t.sendto(benc({"t": tid, "y": "r", "r": {"id": nid}}), addr)
        elif q == b"get_peers":
            ih = msg[b"a"][b"info_hash"]
            r = {"id": nid, "token": b"tok"}
            if ih == self.infohash: r["values"] = [socket.inet_aton("127.0.0.1") + struct.pack(">H", p) for p in self.ports]
            else: r["nodes"] = b""
            self.t.sendto(benc({"t": tid, "y": "r", "r": r}), addr)
        elif q == b"find_node": self.t.sendto(benc({"t": tid, "y": "r", "r": {"id": nid, "nodes": b""}}), addr)

# ------------------------------------------------------------------ serve
async def serve(args):
    d = args[0]; opts = {}
    i = 1
    while i < len(args):
        a = args[i]
        if a in ("--private", "--no-tracker"): opts[a] = True; i += 1
        else: opts[a] = args[i + 1]; i += 2
    os.makedirs(d, exist_ok=True)
    spec = opts.get("--files", "a.bin:300000,sub/b.bin:1000000")
    files = [(f.split(":")[0], int(f.split(":")[1])) for f in spec.split(",")]
    piece = int(opts.get("--piece", 65536)); name = opts.get("--seed-name", "swarm-test")
    payload, info = make_data(files, piece, name)
    if "--private" in opts: info["private"] = 1
    infohash = hashlib.sha1(benc(info)).digest()
    corrupt = int(opts["--corrupt-piece"]) if "--corrupt-piece" in opts else None
    seeders = [Seeder(payload, info, infohash, piece, int(opts.get("--rate", 0)), corrupt if n == 0 else None) for n in range(int(opts.get("--seeders", 1)))]
    for s in seeders: await s.start()
    ports = [s.port for s in seeders]; events = []
    loop = asyncio.get_running_loop()
    http_port = start_http_tracker(ports, infohash, events)
    udp_t, _ = await loop.create_datagram_endpoint(lambda: UdpTracker(ports, infohash, events), local_addr=("127.0.0.1", 0))
    udp_port = udp_t.get_extra_info("sockname")[1]
    dht_t, dht = await loop.create_datagram_endpoint(lambda: Dht(ports, infohash), local_addr=("127.0.0.1", 0))
    dht_port = dht_t.get_extra_info("sockname")[1]
    tpath = os.path.join(d, name + ".torrent")
    meta = {"info": info, "comment": "makan test"}
    if "--no-tracker" not in opts: meta["announce-list"] = [[f"http://127.0.0.1:{http_port}/announce"]]
    open(tpath, "wb").write(benc(meta))
    open(os.path.join(d, name + ".payload"), "wb").write(payload)
    magnet = f"magnet:?xt=urn:btih:{infohash.hex()}&dn={name}" + ("" if "--no-tracker" in opts else f"&tr=http://127.0.0.1:{http_port}/announce")
    print(json.dumps({"torrent": tpath, "payload": os.path.join(d, name + ".payload"), "infohash": infohash.hex(), "magnet": magnet, "name": name,
                      "http_tracker": f"http://127.0.0.1:{http_port}/announce", "udp_tracker": f"udp://127.0.0.1:{udp_port}/announce", "dht_port": dht_port,
                      "seeder_ports": ports, "total": len(payload), "piece": piece, "files": files}), flush=True)
    while True:
        await asyncio.sleep(3600)

# ------------------------------------------------------------------ leecher (tests our seeding)
async def leech(args):
    host, port, tpath = args[0], int(args[1]), args[2]
    max_pieces = int(args[args.index("--max-pieces") + 1]) if "--max-pieces" in args else None
    meta, _ = bdec(open(tpath, "rb").read()); info = meta[b"info"]
    raw = open(tpath, "rb").read(); start = raw.index(b"4:info") + 6; _, end = bdec(raw, start); infohash = hashlib.sha1(raw[start:end]).digest()
    piece = info[b"piece length"]; hashes = info[b"pieces"]
    total = info[b"length"] if b"length" in info else sum(f[b"length"] for f in info[b"files"])
    npieces = len(hashes) // 20
    reader, writer = await asyncio.open_connection(host, port)
    writer.write(b"\x13BitTorrent protocol" + bytes(8) + infohash + b"-FL0001-" + os.urandom(12)); await writer.drain()
    hs = await reader.readexactly(68); ok_hs = hs[28:48] == infohash
    writer.write(struct.pack(">IB", 1, 2)); await writer.drain()
    have = set(); unchoked = False; got = {}; good = 0; bad = 0; nxt = 0
    async def read_msg():
        (length,) = struct.unpack(">I", await reader.readexactly(4))
        return b"" if length == 0 else await reader.readexactly(length)
    async def ask(idx):
        size = min(piece, total - idx * piece); off = 0
        while off < size:
            ln = min(16384, size - off); writer.write(struct.pack(">IBIII", 13, 6, idx, off, ln)); off += ln
        await writer.drain()
    try:
        while nxt < (max_pieces or npieces) or got:
            if unchoked and nxt < (max_pieces or npieces) and len(got) < 4:
                await ask(nxt); got[nxt] = bytearray(); nxt += 1; continue
            msg = await asyncio.wait_for(read_msg(), 30)
            if not msg: continue
            if msg[0] == 1: unchoked = True
            elif msg[0] == 7:
                idx, off = struct.unpack(">II", msg[1:9]); got[idx] += msg[9:]
                size = min(piece, total - idx * piece)
                if len(got[idx]) >= size:
                    if hashlib.sha1(bytes(got[idx])).digest() == hashes[idx * 20:idx * 20 + 20]: good += 1
                    else: bad += 1
                    del got[idx]
    except Exception as e:
        print(json.dumps({"ok": False, "error": repr(e), "good": good, "bad": bad, "handshake": ok_hs}), flush=True); return
    print(json.dumps({"ok": bad == 0 and good == (max_pieces or npieces), "good": good, "bad": bad, "pieces": npieces, "handshake": ok_hs}), flush=True)

if __name__ == "__main__":
    cmd = sys.argv[1]
    asyncio.run({"serve": serve, "leech": leech}[cmd](sys.argv[2:]))
