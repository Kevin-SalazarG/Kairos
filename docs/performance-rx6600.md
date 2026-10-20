# RX 6600 performance revision

Version `1.0.1-rx.4` reduces repeated managed work and fixes resource ownership. It preserves the encoder, QP, picture size, frame rate, replay budget, voice thresholds and microphone samples. These measurements do not establish a change in game FPS.

## Measured changes

| Operation | Before | After | Method |
| --- | --- | --- | --- |
| Voice resampling allocations per 480-sample block | 3,640 B | 664 B | Warm steady-state calls; output arrays remain caller-owned |
| Stable status allocations per update | 7,016 B | 672 B | Three runs of 20,000 status updates |
| Stable status property notifications | 40 | 0 | Identical displayed values; changing values still notify |
| Progress status allocations per update | 7,232 B | 960 B | Same updates with advancing capture time |
| Latest video + picture lookup median | 29.17 ms | 17.69 ms | Twenty alternating warm runs on a synthetic 5,000-file tree |
| Latest lookup pair allocations | 8,018,608 B | 5,710,352 B | Same synthetic tree and results |
| Native sources left immediately after a failed build | 2 | 0 | Three injected failures after capture sources were created |
| Closed-window subscriptions retained by one reused view model | 12 | 0 | Three window open/close cycles, four events per window |

The voice resampler reuses staging and history arrays, preserving the exact convolution order. A 127,484-sample comparison with the previous implementation was identical. Its CPU timings varied between runs, so only the allocation reduction is claimed.

Status polling keeps its original frequency. It compares typed displayed values, reuses event arguments and avoids rebuilding unchanged hotkey state. File deletion, actual running QP, voice cards and externally changed reservations still refresh. Lifetime generations reject late poll, capability, thumbnail and file-watcher callbacks after closing or restarting the view model.

Newest-file lookups walk the existing tree rules without constructing and sorting every library entry. The full library scan preserves its output, including stable timestamp ties, hidden/system exclusions, case-insensitive file types and maximum folder depth. The synthetic-tree speedup applies to library lookups, not video encoding.

Window closure detaches its view-model event handlers and cancels pending idle warmup. Closing an old window after a language replacement does not clear the replacement's activity callback.

Pipeline construction now releases partially owned native resources before propagating an error. The recovery check verifies both automatic cleanup and idempotent disposal, then starts AMD HEVC CQP14 capture successfully. It runs in a separate process with isolated settings and logs.

## Validation

Release x64 compilation completed with no warnings or errors. All 17 new regression checks passed. The complete suite passed 606 of 607 checks; the sole failure was `ShareTranscodeTests.An_encoder_overshoot_is_retried_until_the_file_fits`. The same 0.4 MB export-size failure was previously reproduced on pristine upstream commit `04fd8bf` on this PC. It is separate from recording/replay capture.

The injected native failure covers the partially constructed scene/capture-source path. It does not exercise every possible encoder or output failure.

Build with the repository's .NET 10 toolchain and use a fresh `KAIROS_DATA` directory for tests. Native checks need the published OBS 32.2.2 runtime and must run sequentially.

```powershell
dotnet build Kairos.sln -c Release -p:Platform=x64
dotnet tests\Kairos.Tests\bin\x64\Release\net10.0-windows\win-x64\Kairos.Tests.dll
.\dist\KairosRX\Kairos.exe --selftest-recovery .\work\recovery-check
.\dist\KairosRX\Kairos.exe --selftest-quality-trial .\reference-1080p60.mp4 .\work\quality-check 14 12 58
```

Native recovery and encoding checks report their own frame counters. Reference-file decoding adds CPU cost and cannot estimate live capture overhead. End-to-end game FPS and same-scene visual comparisons remain the next phase.
