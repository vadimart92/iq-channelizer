using System.Runtime.CompilerServices;

namespace IqChannelizer.Fftw;

internal sealed unsafe class FftwAlignedBuffer<T> : IDisposable where T : unmanaged
{
    private const int RequiredAlignment = 64;

    private nint _allocation;
    private nint _pointer;
    private bool _disposed;

    public FftwAlignedBuffer(int length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _ = FftwRuntime.Info;
        Length = length;
        ByteCount = checked((nuint)length * (nuint)Unsafe.SizeOf<T>());
        var allocationBytes = checked(ByteCount + (nuint)(RequiredAlignment - 1));
        _allocation = FftwNative.Malloc(allocationBytes);
        if (_allocation == 0)
        {
            throw new OutOfMemoryException($"FFTW could not allocate {allocationBytes} bytes of aligned native memory.");
        }

        var allocationAddress = (nuint)_allocation;
        _pointer = (nint)((allocationAddress + (RequiredAlignment - 1)) & ~(nuint)(RequiredAlignment - 1));
        if (Address % RequiredAlignment != 0)
        {
            Dispose();
            throw new InvalidOperationException($"Could not align FFTW-owned memory to {RequiredAlignment} bytes.");
        }

        AlignmentClass = FftwNative.AlignmentOf(_pointer);
    }

    public int Length { get; }
    public nuint ByteCount { get; }
    public nuint Address => (nuint)_pointer;
    public int AlignmentClass { get; }

    public Span<T> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Span<T>((void*)_pointer, Length);
        }
    }

    public ReadOnlySpan<T> ReadOnlySpan => Span;

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pointer;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_allocation != 0)
        {
            FftwNative.Free(_allocation);
            _allocation = 0;
            _pointer = 0;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~FftwAlignedBuffer() => Dispose();
}
