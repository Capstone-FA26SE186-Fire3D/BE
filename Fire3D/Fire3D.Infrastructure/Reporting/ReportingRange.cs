using System.Globalization;
using System.Text.RegularExpressions;
namespace Fire3D.Infrastructure.Reporting;
public static class ReportingRange
{
    public static bool TryParse(string? from,string? to,DateTime asOf,out (DateTime From,DateTime To) range)
    {
        range=(asOf.AddDays(-30),asOf);
        if(from is not null && !Timestamp(from,out range.Item1) || to is not null && !Timestamp(to,out range.Item2))return false;
        return range.To>range.From && range.To-range.From<=TimeSpan.FromDays(90);
    }
    private static bool Timestamp(string value,out DateTime utc)
    {
        utc=default;
        if(!Regex.IsMatch(value,@"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$",RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))return false;
        utc=date.UtcDateTime;return true;
    }
}
