#!/usr/bin/env python3
"""Userspace UDP impairment proxy for the real ENet link (measurement plan W2; no root, no tc).

Clients connect to --listen; every client address gets its own upstream socket to --server, so the server sees one peer per client as usual.
Each datagram in each direction is delayed by latency +/- jitter (one way) and dropped with probability loss. Delivery order per direction and
flow is kept (ENet sequences at send time; the in-app NetSim does the same), unless --reorder is given. Unlike the in-app NetSim this drops
RELIABLE traffic too, because it works on the real datagrams, so ENet's own retransmit timing and head-of-line blocking show up in the numbers.

Usage: udp_proxy.py --listen 27016 --server 127.0.0.1:27015 --latency-ms 50 --jitter-ms 5 --loss 0.03 [--up-loss X --down-loss Y] [--stats out.json]
Stops on SIGTERM/SIGINT and writes the counters. Never run against a host you do not own.
"""
import argparse, asyncio, json, random, signal, time


class Flow:
    """One client: its upstream socket and the last scheduled delivery time per direction (keeps order)."""

    def __init__(self, addr):
        self.addr, self.up, self.last_up, self.last_down = addr, None, 0.0, 0.0


class Proxy(asyncio.DatagramProtocol):
    def __init__(self, a, loop):
        self.a, self.loop, self.flows, self.rng = a, loop, {}, random.Random(a.seed)
        self.sock = None
        self.c = {"up_pkts": 0, "up_bytes": 0, "up_dropped": 0, "down_pkts": 0, "down_bytes": 0, "down_dropped": 0, "flows": 0}
        h, p = a.server.rsplit(":", 1)
        self.srv = (h, int(p))

    def connection_made(self, transport):
        self.sock = transport

    def delay(self):
        return max(0.0, self.rng.gauss(self.a.latency_ms, self.a.jitter_ms / 2) if self.a.jitter_ms else self.a.latency_ms) / 1000.0

    def datagram_received(self, data, addr):  # client -> server
        fl = self.flows.get(addr)
        if fl is None:
            fl = self.flows[addr] = Flow(addr)
            self.c["flows"] += 1
            self.loop.create_task(self.open_upstream(fl))
            # packets that arrive before the upstream socket exists are held briefly
        self.c["up_pkts"] += 1
        self.c["up_bytes"] += len(data)
        if self.rng.random() < self.a.up_loss:
            self.c["up_dropped"] += 1
            return
        t = time.monotonic() + self.delay()
        if not self.a.reorder:
            t = max(t, fl.last_up + 1e-6)
        fl.last_up = t
        self.loop.call_at(self.loop.time() + (t - time.monotonic()), self.send_up, fl, data)

    def send_up(self, fl, data):
        if fl.up is not None:
            fl.up.sendto(data)
        else:
            self.loop.call_later(0.002, self.send_up, fl, data)

    async def open_upstream(self, fl):
        proxy = self

        class Up(asyncio.DatagramProtocol):
            def datagram_received(self, data, _addr):  # server -> client
                proxy.c["down_pkts"] += 1
                proxy.c["down_bytes"] += len(data)
                if proxy.rng.random() < proxy.a.down_loss:
                    proxy.c["down_dropped"] += 1
                    return
                t = time.monotonic() + proxy.delay()
                if not proxy.a.reorder:
                    t = max(t, fl.last_down + 1e-6)
                fl.last_down = t
                proxy.loop.call_at(proxy.loop.time() + (t - time.monotonic()), proxy.sock.sendto, data, fl.addr)

        tr, _ = await self.loop.create_datagram_endpoint(Up, remote_addr=self.srv)
        fl.up = tr


async def main(a):
    loop = asyncio.get_running_loop()
    proxy = Proxy(a, loop)
    await loop.create_datagram_endpoint(lambda: proxy, local_addr=("127.0.0.1", a.listen))
    stop = asyncio.Event()
    for s in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(s, stop.set)
    print(f"udp_proxy: 127.0.0.1:{a.listen} -> {a.server}, {a.latency_ms} ms +/- {a.jitter_ms}, loss up {a.up_loss} down {a.down_loss}", flush=True)
    await stop.wait()
    if a.stats:
        json.dump(proxy.c | {"args": vars(a)}, open(a.stats, "w"), indent=2)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--listen", type=int, required=True)
    ap.add_argument("--server", default="127.0.0.1:27015")
    ap.add_argument("--latency-ms", type=float, default=0.0, help="one-way delay")
    ap.add_argument("--jitter-ms", type=float, default=0.0)
    ap.add_argument("--loss", type=float, default=0.0, help="drop probability, both directions (overridden by --up-loss/--down-loss)")
    ap.add_argument("--up-loss", type=float, default=None)
    ap.add_argument("--down-loss", type=float, default=None)
    ap.add_argument("--reorder", action="store_true")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--stats", default="")
    a = ap.parse_args()
    a.up_loss = a.loss if a.up_loss is None else a.up_loss
    a.down_loss = a.loss if a.down_loss is None else a.down_loss
    asyncio.run(main(a))
