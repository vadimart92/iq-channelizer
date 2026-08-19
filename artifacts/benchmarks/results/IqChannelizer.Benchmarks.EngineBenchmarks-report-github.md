```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4


```
| Method  | Strategy | ChannelCount | Mean      | Error     | StdDev    | Allocated |
|-------- |--------- |------------- |----------:|----------:|----------:|----------:|
| **Process** | **Fdc**      | **1**            |  **2.710 ns** | **0.0135 ns** | **0.0126 ns** |         **-** |
| **Process** | **Fdc**      | **8**            |  **4.590 ns** | **0.0203 ns** | **0.0189 ns** |         **-** |
| **Process** | **Fdc**      | **32**           | **10.900 ns** | **0.0317 ns** | **0.0296 ns** |         **-** |
| **Process** | **Pfb**      | **1**            | **31.755 ns** | **0.1001 ns** | **0.0936 ns** |         **-** |
| **Process** | **Pfb**      | **8**            | **35.339 ns** | **0.2221 ns** | **0.2078 ns** |         **-** |
| **Process** | **Pfb**      | **32**           | **47.711 ns** | **0.3496 ns** | **0.3099 ns** |         **-** |
