```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method       | TapsPerPhase | Mean      | Error     | StdDev    | Ratio | Allocated | Alloc Ratio |
|------------- |------------- |----------:|----------:|----------:|------:|----------:|------------:|
| **Scalar**       | **8**            |  **8.982 ns** | **0.4029 ns** | **0.0221 ns** |  **1.00** |         **-** |          **NA** |
| Avx2Compact  | 8            |  5.925 ns | 0.4609 ns | 0.0253 ns |  0.66 |         - |          NA |
| Avx2Expanded | 8            |  3.676 ns | 0.1295 ns | 0.0071 ns |  0.41 |         - |          NA |
|              |              |           |           |           |       |           |             |
| **Scalar**       | **20**           | **24.371 ns** | **3.2288 ns** | **0.1770 ns** |  **1.00** |         **-** |          **NA** |
| Avx2Compact  | 20           | 14.088 ns | 0.9510 ns | 0.0521 ns |  0.58 |         - |          NA |
| Avx2Expanded | 20           |  8.594 ns | 0.3458 ns | 0.0190 ns |  0.35 |         - |          NA |
