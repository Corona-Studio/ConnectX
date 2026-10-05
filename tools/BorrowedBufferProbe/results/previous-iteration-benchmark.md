# Release microbenchmarks

Runtime: .NET 10.0.11; macOS 27.0.1; Arm64.

Warmup: 10,000 operations per row; 7 samples of 20,000 operations. Median of warmed samples; same deterministic payloads, no enabled logging.

| Scenario | Median ns/op | Managed bytes/op |
| --- | ---: | ---: |
| decode.legacy.random20KiB | 1139.99 | 208.00 |
| decode.pooled.random20KiB | 616.70 | 0.00 |
| decode.legacy.repeat20KiB | 3951.95 | 208.00 |
| decode.pooled.repeat20KiB | 1311.71 | 0.00 |
| encode.legacy.Ping | 730.10 | 280.00 |
| encode.pooled.Ping | 197.18 | 0.00 |
| encode.send.dispatch.pooled.Ping | 661.34 | 0.00 |

Legacy rows reproduce the prior allocation/copy sites with the current dependency versions; they are not a separate build of the previous commit. Encode rows measure serialization only. The combined send row uses a synchronous fake transport and is not a network throughput benchmark. CPU timings vary with host load and runtime optimization. Raw Snappy wire compatibility is tested separately. Compression remains the existing Snappier compressor.
