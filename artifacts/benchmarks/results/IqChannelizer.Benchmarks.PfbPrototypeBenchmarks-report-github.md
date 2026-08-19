```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method  | DesignMode   | Mean      | Error     | StdDev    | Allocated |
|-------- |------------- |----------:|----------:|----------:|----------:|
| **Process** | **Conservative** | **12.281 ns** | **0.6281 ns** | **0.0344 ns** |         **-** |
| **Process** | **FoldAware**    |  **7.232 ns** | **0.2719 ns** | **0.0149 ns** |         **-** |
