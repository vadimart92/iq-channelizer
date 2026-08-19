```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method         | TapsPerPhase | Mean      | Error     | StdDev    | Ratio | Allocated | Alloc Ratio |
|--------------- |------------- |----------:|----------:|----------:|------:|----------:|------------:|
| **Scalar**         | **8**            |  **8.991 ns** | **1.8553 ns** | **0.1017 ns** |  **1.00** |         **-** |          **NA** |
| Avx2Compact    | 8            |  6.004 ns | 0.3797 ns | 0.0208 ns |  0.67 |         - |          NA |
| Avx2Expanded   | 8            |  3.658 ns | 0.0612 ns | 0.0034 ns |  0.41 |         - |          NA |
| Avx512Expanded | 8            |  3.242 ns | 0.0721 ns | 0.0040 ns |  0.36 |         - |          NA |
|                |              |           |           |           |       |           |             |
| **Scalar**         | **20**           | **21.169 ns** | **1.2230 ns** | **0.0670 ns** |  **1.00** |         **-** |          **NA** |
| Avx2Compact    | 20           | 14.045 ns | 0.1837 ns | 0.0101 ns |  0.66 |         - |          NA |
| Avx2Expanded   | 20           |  8.171 ns | 0.1247 ns | 0.0068 ns |  0.39 |         - |          NA |
| Avx512Expanded | 20           |  6.734 ns | 0.5784 ns | 0.0317 ns |  0.32 |         - |          NA |
