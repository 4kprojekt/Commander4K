using System.Globalization;
using System.Text.RegularExpressions;

namespace Commander4K.Core.Mail;

/// <summary>Resolves the account's <c>since</c> setting: <c>10d</c>, <c>6m</c>, <c>1y</c>, <c>YYYY-MM-DD</c>; empty = 3 months.</summary>
public static partial class MailSince
{
    public static DateTimeOffset Resolve(string? since, DateTimeOffset now)
    {
        var text = since?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return now.AddMonths(-3);
        }

        var m = Relative().Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 10000)
        {
            return char.ToLowerInvariant(m.Groups[2].Value[0]) switch
            {
                'd' => now.AddDays(-n),
                'm' => now.AddMonths(-n),
                _ => now.AddYears(-n),
            };
        }

        if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return date;
        }

        throw new FormatException($"Invalid 'since' value: {text}");
    }

    [GeneratedRegex("^(\\d+)([dDmMyY])$")]
    private static partial Regex Relative();
}
