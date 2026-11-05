namespace Ac2Revit;

/// <summary>A reopen is useful only after committed progress and memory growth.</summary>
public static class CheckpointPolicy
{
    public static bool ShouldReopen(bool committed,long initialBytes,long currentBytes,long growthLimit=768L*1024*1024) =>
        committed && (currentBytes-initialBytes>growthLimit ||
            initialBytes<=8L*1024*1024*1024 && currentBytes>8L*1024*1024*1024);

    /// <summary>
    /// Saving a large RVT takes seconds; the 1.6.3 Kaspiysk build saved ~52 times. Routine checkpoints are taken at
    /// most once per interval; saves before a forced reopen and the final save are always made by the caller.
    /// </summary>
    /// <summary>
    /// Growth allowed before a reopen: 1/8 of the machine's memory, between 768 MB and 4 GB. A fixed 768 MB made the
    /// Hippodrome reopen its 525 MB model 55 times on a 28 GB computer; small computers keep the previous limit.
    /// </summary>
    public static long GrowthLimit(long totalMemoryBytes)=>Math.Clamp(totalMemoryBytes/8,768L*1024*1024,4L*1024*1024*1024);

    public static readonly TimeSpan SaveInterval=TimeSpan.FromMinutes(2.5);
    public static bool SaveDue(DateTime? lastSaveUtc,DateTime nowUtc)=>lastSaveUtc is not DateTime last || nowUtc-last>=SaveInterval;
}