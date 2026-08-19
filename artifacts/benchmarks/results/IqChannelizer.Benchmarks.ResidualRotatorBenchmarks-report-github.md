```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method | Mean     | Error     | StdDev    | Ratio | Allocated | Alloc Ratio |
|------- |---------:|----------:|----------:|------:|----------:|------------:|
| Scalar | 1.886 ns | 0.0079 ns | 0.0004 ns |  1.00 |         - |          NA |
| Avx2   | 1.853 ns | 0.0303 ns | 0.0017 ns |  0.98 |         - |          NA |
