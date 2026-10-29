using System.Text.RegularExpressions;

namespace InfoMail.Services;

public static partial class AbsenceRouting
{
    public const string DestinationFolder = "@Dagens melding";

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:sygemeld\w*|sygefravæ\w*|sygefravaer\w*|raskmeld\w*|fravær\w*|fravaer\w*|ferie\w*|orlov\w*|barsel\w*|omsorgsdag\w*|afspadser\w*|fridag\w*|frihed|barnets\s+sygedag)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1_000)]
    private static partial Regex AbsencePattern();

    public static bool IsAbsence(string subject, string body) =>
        AbsencePattern().IsMatch(subject + " " + body);

}
