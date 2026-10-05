# Borrowed-buffer forwarding and allocation probe

```sh
dotnet test tools/BorrowedBufferTests -c Release --logger 'trx;LogFileName=regression.trx' --results-directory tools/BorrowedBufferProbe/results
dotnet run --project tools/BorrowedBufferProbe -c Release -- --benchmark
```

## Buffer lifetime

Hive TCP and ConnectX ZeroTier TCP implement `IBorrowedBufferSession`.
`ReceiveHandler` is a single asynchronous consumer; multicast delegates are
rejected because only the last delegate's ValueTask would be awaited. The pipe
read stays active until the consumer completes. Slices of its sequence remain
valid across awaited operations. Slow consumers apply backpressure. Never retain
that sequence after returning. Wait for `StartAsync` to finish before disposing
sessions, and observe the supplied cancellation token in asynchronous consumers.

```csharp
source.ReceiveHandler = async (_, packet, token) =>
{
    await destination.TrySendAsync(packet, token);
};
```

Borrowed sends write the existing Hive frame header followed by the original
array-backed payload segments directly to the transport. They share a wire lock
with stream sends to prevent interleaving. No ordering is promised between
concurrent sends. The existing 16-bit frame supports 65,529 payload bytes.
Failure after starting a frame closes the stream, preventing retry on a stream
containing a partial frame. This is managed payload zero-copy, not kernel zero-copy.

They also implement `IWritableFrameSession`: when producing an owned contiguous
buffer, reserve the first six bytes and pass the complete buffer to
`TrySendFrameAsync`. The session fills the header and sends header/payload in one
transport call (partial writes can require more). Keep the buffer alive and
unchanged until completion. Hive encoding and worker compression use this path;
segmented relay forwarding continues to send the original borrowed segments.

## Changes

- Relay workers forward the pipe sequence directly and await completion. On the
  main Actor architecture, startup waits retain the source pipe read and an
  output barrier ensures the reverse handshake acknowledgement is sent first.
  Legacy sessions retain the existing bounded startup queue.
- Hive's dispatcher encodes into a reusable buffer/stream and uses borrowed sending
  instead of a per-message RecyclableMemoryStream and SendPipe payload copy.
- Hive receive/send pipes reuse array-backed memory-owner wrappers.
- Snappy worker blocks decode directly from segmented input into the final pooled
  output. The allocation-free decoder follows the existing
  [raw Snappy format](https://github.com/google/snappy/blob/main/format_description.txt).
  Compression still uses Snappier, with unchanged wire bytes.
- Buffers and proxy carriers use bounded object caches. A buffer carries explicit
  shared references, and each queued fan-out consumer gets its own carrier.
  Return each rented reference exactly once; do not access a pooled object after
  returning it. Output buffers reject oversized blocks before allocating storage.
- Object caches retain at most 32 items. Buffers larger than 128 KiB release their
  arrays on return. Pool growth or exhaustion can allocate; retained arrays are
  not wiped, and only the written range is exposed as packet payload.
- Proxy outbound queues are bounded to 128 entries with producer backpressure.
  Retries keep the current packet and wait, instead of repeatedly enqueuing it in
  a busy loop. Inbound queues remain unbounded to preserve synchronous dispatcher
  delivery without silently dropping TCP payloads. Queues drain owners on shutdown;
  receive failures release an owner before it has entered a queue.
- Callback snapshots are created when registrations change, rather than on every
  dispatch. Removing a Hive handler removes the exact id. `PacketContext` is a
  value type, proxy log metadata and transport endpoints are cached.
- Packet-id lookups read an immutable dictionary snapshot without a lock;
  registration publishes the forward and reverse maps together.
- .NET 10 asynchronous send/receive state machines are pooled. ConnectX's `Forget`
  consumes discarded ValueTasks exactly once, reusing observer objects/delegates.
- ZeroTier uses its existing offset/count API directly on pipe storage. Its receive
  poll waits on a connection-level timer rather than continuously yielding.
  The native ZeroTier path is compile/API-verified only on this macOS host; its
  package ships Windows native DLLs, so an end-to-end native test was not run.

## Verification and measured scope

The NUnit suite and probe cover concurrent pool ownership, bounded-queue backpressure/retry ordering, pending-owner cleanup, suspended receive lifetime with a poisoning pool, fragmented
headers/bodies, coalesced frames, invalid/truncated frames, consumer faults,
cancellation, partial sends, original payload-array identity, queued owner
lifetime, subscription mutation during an awaited send, and concurrent real TCP
borrowed/stream sends. Snappy tests include 260 corpus fragmentation cases, all
three backreference formats, overlapping copies, malformed lengths/offsets,
configured output limits, and 2,000 mutated compressed blocks. MemoryPack encoding
is compared byte-for-byte with the prior stream implementation.
Writable-frame tests check header bytes, original array identity, a single
transport call, partial writes, cancellation and failure. Packet-id registration
is checked alongside concurrent lookups and duplicate rejection.

Latest verification: ConnectX Release solution build passed, and all 14 targeted
NUnit regressions passed. The existing `Hive.Network.Tests` project was also
attempted, but could not build on this host: its ECS dependency references the
legacy Unity.Mathematics .NET Framework 4.8 project, whose reference assemblies
are unavailable (MSB3644). Those tests did not run; the targeted suite above
exercises the changed Hive TCP, codec, dispatcher and buffer paths directly.

Measured after warmup in Release, with disabled loggers:

| Path | Work | Managed allocation |
| --- | --- | --- |
| Borrowed sends + ConnectX callback dispatch, fake synchronous transport | 100,000 packets | 0 bytes |
| Segmented Snappy decode + two pooled consumers | 10,000 packets | 0 bytes |
| Prior flattened Snappy decode + new carrier | 10,000 packets | about 2.08 MB |
| Real MemoryPack encode + Hive send/dispatch, fake synchronous transport | 100,000 packets | 0 bytes |
| Pending ValueTask result observers, reusable source | 100,000 completions | 0 bytes |
| Existing Snappier span compression | 10,000 packets | 480,000 bytes (48 bytes/packet) |
| Real loopback TCP send/receive | 10,000 packets | about 2-4 KB in the recorded runs |

The real TCP measurement is process-wide and includes runtime/thread scheduling;
its value can vary between runs. Setup, first use, pool exhaustion, semaphore
contention/cancellation, enabled logging providers, error/retry paths, general
MemoryPack object deserialization and Brotli field output, inbound queue growth,
and third-party compression remain outside a whole-application 0-GC claim.
Compression/decompression necessarily writes an output buffer; these changes
remove redundant copies and wrappers, not that transformation itself.

Changes live in this checkout and the `Hive.Framework` submodule. Publish Hive
changes and update the parent submodule pin together.

The [benchmark report](results/benchmark.md) records seven-sample median CPU times
and allocations; [CSV](results/benchmark.csv) and [NUnit TRX](results/regression.trx)
are saved alongside it. Benchmark legacy rows reconstruct the old hot-path code
with identical dependencies and inputs; they are not an old-commit binary run.
