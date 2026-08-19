```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.55GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4


```
| Method    | TapCount | Mean     | Error    | StdDev   | Allocated |
|---------- |--------- |---------:|---------:|---------:|----------:|
| **ScalarFir** | **31**       | **16.21 ns** | **0.052 ns** | **0.043 ns** |         **-** |
| **ScalarFir** | **127**      | **73.23 ns** | **0.382 ns** | **0.357 ns** |         **-** |
