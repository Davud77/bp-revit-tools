namespace Ac2Revit;

/// <summary>A reopen is useful only after committed progress and memory growth.</summary>
public static class CheckpointPolicy
{
    public static bool ShouldReopen(bool committed,long initialBytes,long currentBytes) =>
        committed && (currentBytes-initialBytes>768L*1024*1024 ||
            initialBytes<=8L*1024*1024*1024 && currentBytes>8L*1024*1024*1024);

    /// <summary>
    /// Saving a large RVT takes seconds; the 1.6.3 Kaspiysk build saved ~52 times. Routine checkpoints are taken at
    /// most once per interval; saves before a forced reopen and the final save are always made by the caller.
    /// </summary>
    public static readonly TimeSpan SaveInterval=TimeSpan.FromMinutes(2.5);
    public static bool SaveDue(DateTime? lastSaveUtc,DateTime nowUtc)=>lastSaveUtc is not DateTime last || nowUtc-last>=SaveInterval;
}