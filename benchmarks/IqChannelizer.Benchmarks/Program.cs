using System.Reflection;
using BenchmarkDotNet.Running;
using IqChannelizer.Benchmarks;

if (args.FirstOrDefault() == "--stage-profile")
{
    StageProfileRunner.Run(args.Skip(1).ToArray());
}
else
{
    BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
}
