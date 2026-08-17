using System.Runtime.CompilerServices;

namespace IqChannelizer.Fftw;

internal sealed unsafe class FftwAlignedBuffer<T> : IDisposable where T : unmanaged
{
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
        _pointer = FftwNative.Malloc(ByteCount);
        if (_pointer == 0)
        {
            throw new OutOfMemoryException($"FFTW could not allocate {ByteCount} bytes of aligned native memory.");
        }

        if (Address % 16 != 0)
        {
            Dispose();
            throw new InvalidOperationException("fftwf_malloc returned memory that is not at least 16-byte aligned.");
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

        if (_pointer != 0)
        {
            FftwNative.Free(_pointer);
            _pointer = 0;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~FftwAlignedBuffer() => Dispose();
}
