# Ideas not built yet

Recorded so they are not lost. Nothing here is implemented.

## 1. Show connected Link clients in the app

Right now the app has no idea who is listening. `NetworkStreamer` keeps a `List<WebSocket>`
of live clients but never surfaces it.

**Want:** a panel listing every phone or browser currently connected — when it joined, its
address, and whether audio is actually flowing to it.

**Feasible today:** yes. The client list already exists; it needs an event when a client
joins or leaves, and a row per client in the UI. The remote address is available from the
`TcpClient` at handshake time.

## 2. Control connected clients from the PC

**Want:** per-client volume, and mute, adjustable from the PC in real time.

**How it would work:** the WebSocket is currently one-way, PC to browser. It would become
two-way: the PC sends a small JSON control message ("set volume 0.4"), the page applies it
to its own `GainNode`. The audio itself stays untouched, so one listener turning down does
not affect the others.

**Feasible today:** yes, and not much work. The socket is already open in both directions;
only the audio path uses it.

## 3. Control the PC from the web page

**Want:** the phone shows the same device list as the PC — outputs, volumes, delay, tick
boxes — and changing something there changes it on the PC, live.

**How it would work:** the same two-way channel carries state the other way. The PC pushes
a snapshot of the device list on connect and whenever something changes; the page renders it
and sends back commands. Effectively a remote control for the whole app.

**Feasible today:** yes, but this is the biggest of the three. It needs a proper message
schema, and the token that already guards the stream would then also be guarding control of
the machine's audio — worth a second look at the security side before building it, and
probably a separate opt-in from merely listening.

## Order that makes sense

1 first: it is small, and it makes 2 and 3 easier to reason about because you can see who is
connected. Then 2. Then 3.

---

# Signal path, as layers

Agreed shape for the audio path, so every consumer draws from the same clean signal
instead of inheriting whatever the one before it did.

## Layer 1 — compatibility

Only what is needed to make the signal usable by a given destination: sample rate and
channel layout. No taste, no adjustment. `FormatAdapter` and `ChannelMapSampleProvider`.

## Layer 2 — distribution

One clean copy of the source that everyone takes from.

WASAPI loopback hands over audio **after** the default device's volume has been applied,
so the raw capture already carries the master's setting. Layer 2 divides that back out
once, in the capture callback, and both the local outputs and the network listeners read
the result. Nobody inherits the master volume any more.

Anything that wants the pure signal takes it here.

## Layer 3 — adjustments

Per-consumer, and only per-consumer: volume, equaliser, delay. A local output applies its
own; a browser applies its own in its gain node. One consumer's settings can never reach
another's.

## Why this order

The failure it prevents is real and was hit in practice: the phone stream was reading from
the raw capture, so turning the PC volume down turned the phone down too, and no amount of
raising the phone's own volume could recover a signal that had already been attenuated
before it left the machine.
