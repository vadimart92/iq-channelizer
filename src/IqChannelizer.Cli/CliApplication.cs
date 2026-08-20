using System.CommandLine;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Cli;

internal static class CliApplication
{
    public static Task<int> RunAsync(string[] args)
    {
        var inputOption = new Option<FileInfo>("--input", "-i")
        {
            Description = "Input .wav, .sigmf, .sigmf-meta, or .sigmf-data file.",
            Required = true
        };
        var channelOption = new Option<string[]>("--channel", "-c")
        {
            Description = "Channel ID:CENTER:PASSBAND:TRANSITION[:STOPBAND[:RIPPLE[:MIN_RATE[:PREFERRED_RATE]]]]. Repeat for each channel.",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };
        var outputOption = new Option<DirectoryInfo>("--output", "-o")
        {
            Description = "Output directory.",
            DefaultValueFactory = _ => new DirectoryInfo("channels")
        };
        var strategyOption = new Option<string>("--strategy")
        {
            Description = "Channelizer strategy: fdc, pfb, or auto.",
            DefaultValueFactory = _ => "fdc"
        };
        var outputFormatOption = new Option<string>("--output-format")
        {
            Description = "Per-channel output format: sigmf or wav.",
            DefaultValueFactory = _ => "sigmf"
        };
        var chunkSizeOption = new Option<int>("--chunk-size")
        {
            Description = "Preferred number of new IQ samples per processing block.",
            DefaultValueFactory = _ => 8192
        };
        var maxChunkSizeOption = new Option<int>("--max-chunk-size")
        {
            Description = "Maximum number of new IQ samples per processing block.",
            DefaultValueFactory = _ => 32768
        };
        var simdOption = new Option<string>("--simd")
        {
            Description = "SIMD backend: auto, scalar, avx2, or avx512.",
            DefaultValueFactory = _ => "auto"
        };

        var root = new RootCommand("Channelize complex IQ recordings into one baseband recording per requested channel.")
        {
            inputOption,
            channelOption,
            outputOption,
            strategyOption,
            outputFormatOption,
            chunkSizeOption,
            maxChunkSizeOption,
            simdOption
        };

        root.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var options = new ChannelizeOptions(
                    parseResult.GetValue(inputOption)!,
                    parseResult.GetValue(channelOption) ?? [],
                    parseResult.GetValue(outputOption)!,
                    ParseStrategy(parseResult.GetValue(strategyOption)!),
                    ParseOutputFormat(parseResult.GetValue(outputFormatOption)!),
                    parseResult.GetValue(chunkSizeOption),
                    parseResult.GetValue(maxChunkSizeOption),
                    ParseSimd(parseResult.GetValue(simdOption)!));
                await ChannelizeCommand.RunAsync(options, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Channelization cancelled.");
                return 130;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or NotSupportedException)
            {
                Console.Error.WriteLine($"error: {exception.Message}");
                return 1;
            }
        });

        return root.Parse(args).InvokeAsync();
    }

    private static ChannelizerStrategy ParseStrategy(string value) => value.ToLowerInvariant() switch
    {
        "fdc" => ChannelizerStrategy.Fdc,
        "pfb" => ChannelizerStrategy.Pfb,
        "auto" => ChannelizerStrategy.Auto,
        _ => throw new ArgumentException($"Unknown strategy '{value}'. Expected fdc, pfb, or auto.")
    };

    private static OutputFormat ParseOutputFormat(string value) => value.ToLowerInvariant() switch
    {
        "sigmf" => OutputFormat.SigMf,
        "wav" => OutputFormat.Wav,
        _ => throw new ArgumentException($"Unknown output format '{value}'. Expected sigmf or wav.")
    };

    private static SimdPreference ParseSimd(string value) => value.ToLowerInvariant() switch
    {
        "auto" => SimdPreference.Auto,
        "scalar" => SimdPreference.Scalar,
        "avx2" => SimdPreference.Avx2,
        "avx512" => SimdPreference.Avx512,
        _ => throw new ArgumentException($"Unknown SIMD backend '{value}'. Expected auto, scalar, avx2, or avx512.")
    };
}

internal sealed record ChannelizeOptions(
    FileInfo Input,
    IReadOnlyList<string> Channels,
    DirectoryInfo Output,
    ChannelizerStrategy Strategy,
    OutputFormat OutputFormat,
    int PreferredChunkSize,
    int MaxChunkSize,
    SimdPreference Simd);

internal enum OutputFormat
{
    SigMf,
    Wav
}
