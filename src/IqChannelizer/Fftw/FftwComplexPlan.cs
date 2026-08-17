using IqChannelizer.Abstractions;

namespace IqChannelizer.Fftw;

internal sealed class FftwComplexPlan : IDisposable
{
    private FftwAlignedBuffer<ComplexF>? _input;
    private FftwAlignedBuffer<ComplexF>? _output;
    private FftwPlanCache.Lease? _lease;
    private bool _disposed;

    public FftwComplexPlan(
        int transformLength,
        int batchCount,
        int direction,
        bool inPlace = false,
        FftwPlanningPolicy? policy = null)
    {
        if (transformLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transformLength));
        }

        if (batchCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchCount));
        }

        if (direction is not FftwNative.Forward and not FftwNative.Backward)
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        TransformLength = transformLength;
        BatchCount = batchCount;
        Direction = direction;
        IsInPlace = inPlace;
        PlanningPolicy = policy ?? new FftwPlanningPolicy();
        ElementCount = checked(transformLength * batchCount);

        try
        {
            _input = new FftwAlignedBuffer<ComplexF>(ElementCount);
            _output = inPlace ? _input : new FftwAlignedBuffer<ComplexF>(ElementCount);
            if (_input.AlignmentClass != _output.AlignmentClass)
            {
                throw new InvalidOperationException("FFTW input and output buffers have incompatible alignment classes.");
            }

            var key = new FftwPlanKey(
                transformLength,
                batchCount,
                direction,
                InputStride: 1,
                InputDistance: transformLength,
                OutputStride: 1,
                OutputDistance: transformLength,
                inPlace,
                PlanningPolicy.ThreadCount,
                _input.AlignmentClass,
                PlanningPolicy.Mode);
            _lease = FftwPlanCache.Acquire(key);
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    public int TransformLength { get; }
    public int BatchCount { get; }
    public int Direction { get; }
    public int ElementCount { get; }
    public bool IsInPlace { get; }
    public FftwPlanningPolicy PlanningPolicy { get; }

    internal nuint InputAddress => _input?.Address ?? 0;
    internal nuint OutputAddress => _output?.Address ?? 0;
    internal int InputAlignmentClass => _input?.AlignmentClass ?? throw new ObjectDisposedException(nameof(FftwComplexPlan));
    internal int OutputAlignmentClass => _output?.AlignmentClass ?? throw new ObjectDisposedException(nameof(FftwComplexPlan));
    internal nint NativePlanAddress => _lease?.Plan ?? 0;

    public void Execute(ReadOnlySpan<ComplexF> input, Span<ComplexF> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.Length != ElementCount)
        {
            throw new ArgumentException($"Expected exactly {ElementCount} FFTW input values.", nameof(input));
        }

        if (output.Length != ElementCount)
        {
            throw new ArgumentException($"Expected exactly {ElementCount} FFTW output values.", nameof(output));
        }

        input.CopyTo(_input!.Span);
        FftwNative.ExecuteDft(_lease!.Plan, _input.Pointer, _output!.Pointer);
        _output.ReadOnlySpan.CopyTo(output);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseResources();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~FftwComplexPlan() => ReleaseResources();

    private void ReleaseResources()
    {
        _lease?.Dispose();
        _lease = null;
        if (!ReferenceEquals(_input, _output))
        {
            _output?.Dispose();
        }

        _output = null;
        _input?.Dispose();
        _input = null;
    }
}
