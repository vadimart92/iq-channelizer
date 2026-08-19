using IqChannelizer.Abstractions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace IqChannelizer.Fftw;

internal enum FftwPlanningMode
{
    Estimate,
    Measure
}

internal readonly record struct FftwPlanningPolicy
{
    public FftwPlanningPolicy(FftwPlanningMode mode = FftwPlanningMode.Estimate, int threadCount = 1)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (threadCount != 1)
        {
            throw new NotSupportedException(
                "The bundled runtime is single-threaded. ThreadCount > 1 requires a separately distributed FFTW threads DLL and an explicit initialization policy.");
        }

        Mode = mode;
        ThreadCount = threadCount;
    }

    public FftwPlanningMode Mode { get; }
    public int ThreadCount { get; }
}

internal readonly record struct FftwPlanKey(
    int TransformLength,
    int BatchCount,
    int Direction,
    int InputStride,
    int InputDistance,
    int OutputStride,
    int OutputDistance,
    bool InPlace,
    int ThreadCount,
    int AlignmentClass,
    FftwPlanningMode PlanningMode);

internal static unsafe class FftwPlanCache
{
    internal const int MaximumIdlePlanCount = 32;

    private sealed class Entry
    {
        public required nint Plan { get; init; }
        public int LeaseCount { get; set; }
        public long LastReleasedSequence { get; set; }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly FftwPlanKey _key;
        private bool _disposed;

        internal Lease(FftwPlanKey key, nint plan)
        {
            _key = key;
            Plan = plan;
        }

        public nint Plan { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Release(_key);
            _disposed = true;
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<FftwPlanKey, Entry> Plans = [];
    private static long _createdPlanCount;
    private static long _releaseSequence;

    static FftwPlanCache() => AppDomain.CurrentDomain.ProcessExit += (_, _) => ClearIdle();

    internal static object PlanningGate => Gate;
    public static long CreatedPlanCount => Interlocked.Read(ref _createdPlanCount);

    public static int CachedPlanCount
    {
        get
        {
            lock (Gate)
            {
                return Plans.Count;
            }
        }
    }

    public static int IdlePlanCount
    {
        get
        {
            lock (Gate)
            {
                return Plans.Values.Count(entry => entry.LeaseCount == 0);
            }
        }
    }

    public static int ActiveLeaseCount
    {
        get
        {
            lock (Gate)
            {
                return Plans.Values.Sum(entry => entry.LeaseCount);
            }
        }
    }

    internal static Lease Acquire(FftwPlanKey key)
    {
        lock (Gate)
        {
            if (!Plans.TryGetValue(key, out var entry))
            {
                entry = new Entry { Plan = CreatePlan(key) };
                Plans.Add(key, entry);
                Interlocked.Increment(ref _createdPlanCount);
            }

            entry.LeaseCount++;
            return new Lease(key, entry.Plan);
        }
    }

    public static void ClearIdle()
    {
        lock (Gate)
        {
            var idleKeys = Plans.Where(pair => pair.Value.LeaseCount == 0).Select(pair => pair.Key).ToArray();
            foreach (var key in idleKeys)
            {
                FftwNative.DestroyPlan(Plans[key].Plan);
                Plans.Remove(key);
            }
        }
    }

    private static nint CreatePlan(FftwPlanKey key)
    {
        var elementCount = checked(key.TransformLength * key.BatchCount);
        using var input = new FftwAlignedBuffer<ComplexF>(elementCount);
        using var separateOutput = key.InPlace ? null : new FftwAlignedBuffer<ComplexF>(elementCount);
        var outputPointer = key.InPlace ? input.Pointer : separateOutput!.Pointer;
        var outputAlignment = key.InPlace ? input.AlignmentClass : separateOutput!.AlignmentClass;
        if (input.AlignmentClass != key.AlignmentClass || outputAlignment != key.AlignmentClass)
        {
            throw new InvalidOperationException("FFTW planning buffers do not match the requested alignment class.");
        }

        var flags = key.PlanningMode == FftwPlanningMode.Estimate ? FftwNative.Estimate : FftwNative.Measure;
        nint plan;
        if (key.BatchCount == 1)
        {
            plan = FftwNative.PlanDft1D(key.TransformLength, input.Pointer, outputPointer, key.Direction, flags);
        }
        else
        {
            var length = key.TransformLength;
            plan = FftwNative.PlanManyDft(
                1,
                &length,
                key.BatchCount,
                input.Pointer,
                null,
                key.InputStride,
                key.InputDistance,
                outputPointer,
                null,
                key.OutputStride,
                key.OutputDistance,
                key.Direction,
                flags);
        }

        return plan != 0
            ? plan
            : throw new InvalidOperationException(
                $"FFTW failed to create plan length={key.TransformLength}, batches={key.BatchCount}, " +
                $"direction={key.Direction}, inPlace={key.InPlace}, mode={key.PlanningMode}.");
    }

    private static void Release(FftwPlanKey key)
    {
        lock (Gate)
        {
            if (!Plans.TryGetValue(key, out var entry) || entry.LeaseCount <= 0)
            {
                throw new InvalidOperationException("FFTW plan cache lease ownership is inconsistent.");
            }

            entry.LeaseCount--;
            if (entry.LeaseCount == 0)
            {
                entry.LastReleasedSequence = ++_releaseSequence;
                TrimIdlePlans();
            }
        }
    }

    private static void TrimIdlePlans()
    {
        while (Plans.Values.Count(entry => entry.LeaseCount == 0) > MaximumIdlePlanCount)
        {
            var oldest = Plans
                .Where(pair => pair.Value.LeaseCount == 0)
                .MinBy(pair => pair.Value.LastReleasedSequence);
            FftwNative.DestroyPlan(oldest.Value.Plan);
            Plans.Remove(oldest.Key);
        }
    }
}

internal static unsafe class FftwWisdom
{
    private sealed class WisdomWriter
    {
        public StringBuilder Builder { get; } = new(capacity: 1024);
        public Exception? Error { get; set; }
    }

    public static string ExportToString()
    {
        _ = FftwRuntime.Info;
        lock (FftwPlanCache.PlanningGate)
        {
            var writer = new WisdomWriter();
            var handle = GCHandle.Alloc(writer);
            try
            {
                var callback = (delegate* unmanaged[Cdecl]<byte, nint, void>)&AppendWisdomCharacter;
                FftwNative.ExportWisdom((nint)callback, GCHandle.ToIntPtr(handle));
                if (writer.Error is not null)
                {
                    throw new InvalidOperationException("Managed wisdom export callback failed.", writer.Error);
                }

                return writer.Builder.ToString();
            }
            finally
            {
                handle.Free();
            }
        }
    }

    public static bool ImportFromString(string wisdom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wisdom);
        _ = FftwRuntime.Info;
        lock (FftwPlanCache.PlanningGate)
        {
            return FftwNative.ImportWisdomFromString(wisdom) != 0;
        }
    }

    public static void Forget()
    {
        _ = FftwRuntime.Info;
        lock (FftwPlanCache.PlanningGate)
        {
            FftwNative.ForgetWisdom();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AppendWisdomCharacter(byte value, nint context)
    {
        var writer = (WisdomWriter)GCHandle.FromIntPtr(context).Target!;
        if (writer.Error is not null)
        {
            return;
        }

        try
        {
            writer.Builder.Append((char)value);
        }
        catch (Exception exception)
        {
            // Exceptions must never cross an unmanaged callback boundary.
            writer.Error = exception;
        }
    }
}
