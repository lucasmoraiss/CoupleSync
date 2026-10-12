namespace CoupleSync.Application.OcrImport;

/// <summary>How many statements a group may have waiting to be read at the same time.</summary>
public sealed class ImportQueueOptions
{
    public const string SectionName = "OcrImport";

    public const int DefaultMaxUnfinishedJobsPerCouple = 3;

    /// <summary>
    /// Most import jobs (Pending + Processing) one group may have at once. The reader handles one file at a time for
    /// every group of the single instance, so a long queue from one group is a long wait for all of them.
    /// </summary>
    public int MaxUnfinishedJobsPerCouple { get; set; } = DefaultMaxUnfinishedJobsPerCouple;
}
