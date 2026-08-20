using System.Globalization;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Cli;

internal static class ChannelSpecParser
{
    public static ChannelRequest Parse(string value)
    {
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 4 or > 8)
        {
            throw new ArgumentException(
                $"Invalid channel '{value}'. Expected ID:CENTER:PASSBAND:TRANSITION" +
                "[:STOPBAND[:RIPPLE[:MIN_RATE[:PREFERRED_RATE]]]].");
        }

        return new ChannelRequest(
            ParseInt(parts[0], "ID"),
            ParseDouble(parts[1], "CENTER"),
            ParseDouble(parts[2], "PASSBAND"),
            ParseDouble(parts[3], "TRANSITION"),
            parts.Length > 4 ? ParseDouble(parts[4], "STOPBAND") : 80.0,
            parts.Length > 5 ? ParseDouble(parts[5], "RIPPLE") : 0.1,
            parts.Length > 6 ? ParseDouble(parts[6], "MIN_RATE") : null,
            parts.Length > 7 ? ParseDouble(parts[7], "PREFERRED_RATE") : null);
    }

    private static int ParseInt(string value, string field) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new ArgumentException($"Channel {field} must be an invariant-culture integer, got '{value}'.");

    private static double ParseDouble(string value, string field) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result)
            ? result
            : throw new ArgumentException($"Channel {field} must be a finite invariant-culture number, got '{value}'.");
}
