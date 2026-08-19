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
| **Scalar** | **128**         | **124.87 ns** |  **9.170 ns** | **0.503 ns** |  **1.00** |         **-** |          **NA** |
| Avx2   | 128         |  37.09 ns |  2.602 ns | 0.143 ns |  0.30 |         - |          NA |
|        |             |           |           |          |       |           |             |
| **Scalar** | **512**         | **507.41 ns** | **73.768 ns** | **4.043 ns** |  **1.00** |         **-** |          **NA** |
| Avx2   | 512         | 122.89 ns | 10.969 ns | 0.601 ns |  0.24 |         - |          NA |
