```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  Job-FGEKWY : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4

IterationCount=3  LaunchCount=1  WarmupCount=1

```
| Method                | TapsPerPhase | Mean      | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------- |------------- |----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| **Scalar**                | **4**            |  **4.968 ns** |  **0.5468 ns** | **0.0300 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Avx2Compact           | 4            |  3.088 ns |  0.2893 ns | 0.0159 ns |  0.62 |    0.00 |         - |          NA |
| Avx2Expanded          | 4            |  1.491 ns |  0.2535 ns | 0.0139 ns |  0.30 |    0.00 |         - |          NA |
| Avx2ExpandedGeneric   | 4            |  2.061 ns |  0.0488 ns | 0.0027 ns |  0.41 |    0.00 |         - |          NA |
| Avx512Expanded        | 4            |  1.631 ns |  0.2217 ns | 0.0122 ns |  0.33 |    0.00 |         - |          NA |
| Avx512ExpandedGeneric | 4            |  1.722 ns |  0.1926 ns | 0.0106 ns |  0.35 |    0.00 |         - |          NA |
|                       |              |           |            |           |       |         |           |             |
| **Scalar**                | **8**            |  **9.122 ns** |  **0.8396 ns** | **0.0460 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Avx2Compact           | 8            |  5.996 ns |  0.8556 ns | 0.0469 ns |  0.66 |    0.01 |         - |          NA |
| Avx2Expanded          | 8            |  3.386 ns |  0.9211 ns | 0.0505 ns |  0.37 |    0.01 |         - |          NA |
| Avx2ExpandedGeneric   | 8            |  3.688 ns |  0.4860 ns | 0.0266 ns |  0.40 |    0.00 |         - |          NA |
| Avx512Expanded        | 8            |  2.561 ns |  0.8451 ns | 0.0463 ns |  0.28 |    0.00 |         - |          NA |
| Avx512ExpandedGeneric | 8            |  3.320 ns |  0.4546 ns | 0.0249 ns |  0.36 |    0.00 |         - |          NA |
|                       |              |           |            |           |       |         |           |             |
| **Scalar**                | **12**           | **13.400 ns** |  **3.5453 ns** | **0.1943 ns** |  **1.00** |    **0.02** |         **-** |          **NA** |
| Avx2Compact           | 12           |  8.707 ns |  1.0557 ns | 0.0579 ns |  0.65 |    0.01 |         - |          NA |
| Avx2Expanded          | 12           |  3.492 ns |  0.7485 ns | 0.0410 ns |  0.26 |    0.00 |         - |          NA |
| Avx2ExpandedGeneric   | 12           |  5.440 ns |  0.9827 ns | 0.0539 ns |  0.41 |    0.01 |         - |          NA |
| Avx512Expanded        | 12           |  3.504 ns |  0.2826 ns | 0.0155 ns |  0.26 |    0.00 |         - |          NA |
| Avx512ExpandedGeneric | 12           |  4.731 ns |  0.3569 ns | 0.0196 ns |  0.35 |    0.00 |         - |          NA |
|                       |              |           |            |           |       |         |           |             |
| **Scalar**                | **16**           | **17.340 ns** |  **1.5166 ns** | **0.0831 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Avx2Compact           | 16           | 11.465 ns |  1.6465 ns | 0.0903 ns |  0.66 |    0.01 |         - |          NA |
| Avx2Expanded          | 16           |  6.567 ns |  4.8564 ns | 0.2662 ns |  0.38 |    0.01 |         - |          NA |
| Avx2ExpandedGeneric   | 16           | 10.085 ns |  3.1021 ns | 0.1700 ns |  0.58 |    0.01 |         - |          NA |
| Avx512Expanded        | 16           |  5.460 ns | 16.9976 ns | 0.9317 ns |  0.31 |    0.05 |         - |          NA |
| Avx512ExpandedGeneric | 16           |  8.390 ns |  7.0528 ns | 0.3866 ns |  0.48 |    0.02 |         - |          NA |
|                       |              |           |            |           |       |         |           |             |
| **Scalar**                | **20**           | **28.634 ns** | **81.9545 ns** | **4.4922 ns** |  **1.02** |    **0.21** |         **-** |          **NA** |
| Avx2Compact           | 20           | 20.068 ns |  2.4595 ns | 0.1348 ns |  0.71 |    0.11 |         - |          NA |
| Avx2Expanded          | 20           |  9.590 ns | 43.6795 ns | 2.3942 ns |  0.34 |    0.09 |         - |          NA |
| Avx2ExpandedGeneric   | 20           | 10.222 ns | 37.9418 ns | 2.0797 ns |  0.36 |    0.08 |         - |          NA |
| Avx512Expanded        | 20           |  6.832 ns |  1.2228 ns | 0.0670 ns |  0.24 |    0.04 |         - |          NA |
| Avx512ExpandedGeneric | 20           |  7.765 ns |  4.3042 ns | 0.2359 ns |  0.28 |    0.04 |         - |          NA |
