```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method | SliceLength | Mean      | Error     | StdDev   | Ratio | Allocated | Alloc Ratio |
|------- |------------ |----------:|----------:|---------:|------:|----------:|------------:|
| **Scalar** | **128**         | **124.68 ns** |  **9.708 ns** | **0.532 ns** |  **1.00** |         **-** |          **NA** |
| Avx2   | 128         |  35.10 ns |  5.368 ns | 0.294 ns |  0.28 |         - |          NA |
| Avx512 | 128         |  33.71 ns |  2.818 ns | 0.154 ns |  0.27 |         - |          NA |
|        |             |           |           |          |       |           |             |
| **Scalar** | **512**         | **503.92 ns** | **31.732 ns** | **1.739 ns** |  **1.00** |         **-** |          **NA** |
| Avx2   | 512         | 122.41 ns |  9.956 ns | 0.546 ns |  0.24 |         - |          NA |
| Avx512 | 512         | 102.05 ns |  6.477 ns | 0.355 ns |  0.20 |         - |          NA |
