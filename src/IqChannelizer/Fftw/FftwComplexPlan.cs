using IqChannelizer.Abstractions;

namespace IqChannelizer.Fftw;

internal sealed unsafe class FftwComplexPlan : IDisposable
{
    private nint _plan;
    private nint _input;
    private nint _output;
    private bool _disposed;

    public FftwComplexPlan(int transformLength, int batchCount, int direction)
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
        ElementCount = checked(transformLength * batchCount);
        var byteCount = checked((nuint)ElementCount * (nuint)sizeof(ComplexF));

        try
        {
            _input = FftwNative.Malloc(byteCount);
            _output = FftwNative.Malloc(byteCount);
            if (_input == 0 || _output == 0)
            {
                throw new OutOfMemoryException("FFTW could not allocate its aligned complex buffers.");
            }

            if (batchCount == 1)
            {
                _plan = FftwNative.PlanDft1D(transformLength, _input, _output, direction, FftwNative.Estimate);
            }
            else
            {
                var length = transformLength;
                _plan = FftwNative.PlanManyDft(
                    1,
                    &length,
                    batchCount,
                    _input,
                    null,
                    1,
                    transformLength,
                    _output,
                    null,
                    1,
                    transformLength,
                    direction,
                    FftwNative.Estimate);
            }

            if (_plan == 0)
            {
                throw new InvalidOperationException("FFTW failed to create a complex DFT plan.");
            }
        }
        catch
        {
            ReleaseNativeResources();
            throw;
        }
    }

    public int TransformLength { get; }
    public int BatchCount { get; }
    public int ElementCount { get; }

    internal nuint InputAddress => (nuint)_input;
    internal nuint OutputAddress => (nuint)_output;

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

        input.CopyTo(new Span<ComplexF>((void*)_input, ElementCount));
        FftwNative.Execute(_plan);
        new ReadOnlySpan<ComplexF>((void*)_output, ElementCount).CopyTo(output);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseNativeResources();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~FftwComplexPlan() => ReleaseNativeResources();

    private void ReleaseNativeResources()
    {
        if (_plan != 0)
        {
            FftwNative.DestroyPlan(_plan);
            _plan = 0;
        }

        if (_input != 0)
        {
            FftwNative.Free(_input);
            _input = 0;
        }

        if (_output != 0)
        {
            FftwNative.Free(_output);
            _output = 0;
        }
    }
}
