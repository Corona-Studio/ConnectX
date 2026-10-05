# Release microbenchmarks

Runtime: .NET 10.0.11; macOS 27.0.1; Arm64.

Microbenchmark warmup: 10,000 operations per row; 7 samples of 20,000 operations. TCP warmup: 2,000 packets per mode; 7 samples of 10,000 packets. Median of warmed samples; same deterministic payloads, no enabled logging.

| Scenario | Median ns/op | Managed bytes/op |
| --- | ---: | ---: |
| decode.legacy.random20KiB | 1102.66 | 208.00 |
| decode.pooled.random20KiB | 669.36 | 0.00 |
| decode.legacy.repeat20KiB | 3909.62 | 208.00 |
| decode.pooled.repeat20KiB | 1364.38 | 0.00 |
| mapper.locked.Ping | 34.32 | 0.00 |
| mapper.snapshot.Ping | 28.74 | 0.00 |
| encode.legacy.Ping | 693.58 | 280.00 |
| encode.pooled.Ping | 187.63 | 0.00 |
| encode.send.dispatch.pooled.Ping | 534.42 | 0.00 |
| tcp.sequence.512B | 5820.73 | 0.03 |
| tcp.frame.512B | 3067.47 | 0.03 |

Legacy rows reproduce the prior allocation/copy sites with the current dependency versions; they are not a separate build of the previous commit. Encode rows measure serialization only. The combined send row uses a synchronous fake transport and is not a network throughput benchmark. CPU timings vary with host load and runtime optimization. Raw Snappy wire compatibility is tested separately. Compression remains the existing Snappier compressor. TCP rows use one warmed loopback connection, 512-byte payloads, NoDelay and alternating sequence/frame batches (seven samples of 10,000 packets each); time includes receipt of the full batch and allocation is process-wide, including runtime scheduling. The other rows measure allocations on the benchmark thread.
