namespace Fmpostor.Api.Innersloth
{
    /// <summary>Result shown to the reporting player after using the game's report menu.</summary>
    public enum ReportOutcome : byte
    {
        NotReportedUnknown = 0,
        NotReportedNoAccount = 1,
        NotReportedNotFound = 2,
        NotReportedRateLimit = 3,
        Reported = 4,
    }
}
