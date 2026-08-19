```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4


```
| Method  | Strategy | ChannelCount | Simd   | Mean      | Error     | StdDev    | Allocated |
|-------- |--------- |------------- |------- |----------:|----------:|----------:|----------:|
| **Process** | **Fdc**      | **1**            | **Scalar** |  **2.700 ns** | **0.0085 ns** | **0.0080 ns** |         **-** |
| **Process** | **Fdc**      | **1**            | **Avx2**   |  **2.673 ns** | **0.0049 ns** | **0.0044 ns** |         **-** |
| **Process** | **Fdc**      | **8**            | **Scalar** |  **4.153 ns** | **0.0161 ns** | **0.0142 ns** |         **-** |
| **Process** | **Fdc**      | **8**            | **Avx2**   |  **4.018 ns** | **0.0061 ns** | **0.0051 ns** |         **-** |
| **Process** | **Fdc**      | **32**           | **Scalar** |  **9.079 ns** | **0.0198 ns** | **0.0185 ns** |         **-** |
| **Process** | **Fdc**      | **32**           | **Avx2**   |  **8.417 ns** | **0.0443 ns** | **0.0393 ns** |         **-** |
| **Process** | **Pfb**      | **1**            | **Scalar** | **22.541 ns** | **0.4486 ns** | **0.4196 ns** |         **-** |
| **Process** | **Pfb**      | **1**            | **Avx2**   | **10.113 ns** | **0.0369 ns** | **0.0308 ns** |         **-** |
| **Process** | **Pfb**      | **8**            | **Scalar** | **25.084 ns** | **0.0413 ns** | **0.0366 ns** |         **-** |
| **Process** | **Pfb**      | **8**            | **Avx2**   | **13.053 ns** | **0.0364 ns** | **0.0323 ns** |         **-** |
| **Process** | **Pfb**      | **32**           | **Scalar** | **36.806 ns** | **0.1100 ns** | **0.0975 ns** |         **-** |
| **Process** | **Pfb**      | **32**           | **Avx2**   | **24.410 ns** | **0.0646 ns** | **0.0573 ns** |         **-** |
