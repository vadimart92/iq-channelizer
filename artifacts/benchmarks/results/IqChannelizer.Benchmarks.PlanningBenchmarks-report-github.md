```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4


```
| Method                        | Strategy | Mean     | Error     | StdDev    | Gen0     | Gen1     | Gen2     | Allocated  |
|------------------------------ |--------- |---------:|----------:|----------:|---------:|---------:|---------:|-----------:|
| **CreateEngineWithWarmPlanCache** | **Fdc**      | **2.466 ms** | **0.0166 ms** | **0.0147 ms** | **164.0625** | **164.0625** | **164.0625** |  **571.42 KB** |
| **CreateEngineWithWarmPlanCache** | **Pfb**      | **1.395 ms** | **0.0071 ms** | **0.0066 ms** | **332.0313** | **332.0313** | **332.0313** | **1102.56 KB** |
