namespace Jellyfin.Plugin.JellyCAP.Models;

/// <summary>
/// Lifecycle state for a durable generation job.
/// </summary>
public enum GenerationJobStatus
{
    /// <summary>The job is waiting for an available worker.</summary>
    Queued,

    /// <summary>The job is currently generating a proposal.</summary>
    Running,

    /// <summary>The job completed and created a proposal.</summary>
    Completed,

    /// <summary>The job stopped because generation failed.</summary>
    Failed
}

/// <summary>
/// A durable request to generate one collection proposal.
/// </summary>
public sealed class GenerationJob
{
    /// <summary>Gets or sets the job identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the collection identifier.</summary>
    public Guid CollectionId { get; set; }

    /// <summary>Gets or sets the collection name captured when queued.</summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>Gets or sets the job status.</summary>
    public GenerationJobStatus Status { get; set; }

    /// <summary>Gets or sets when the job entered the queue.</summary>
    public DateTimeOffset QueuedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets when a worker claimed the job.</summary>
    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>Gets or sets when the job completed or failed.</summary>
    public DateTimeOffset? FinishedAtUtc { get; set; }

    /// <summary>Gets or sets the generated proposal identifier.</summary>
    public Guid? ProposalId { get; set; }

    /// <summary>Gets or sets a bounded failure message.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Result of queuing a filtered collection batch.
/// </summary>
public sealed class GenerationEnqueueResult
{
    /// <summary>Gets or sets the number of new jobs created.</summary>
    public int QueuedCount { get; set; }

    /// <summary>Gets or sets the number already queued or running.</summary>
    public int AlreadyActiveCount { get; set; }

    /// <summary>Gets or sets the number excluded because they already have pending reviews.</summary>
    public int SkippedPendingReviewCount { get; set; }
}

/// <summary>
/// Result of cancelling one or more queued generation jobs.
/// </summary>
public sealed class GenerationCancellationResult
{
    /// <summary>Gets or sets the number of queued jobs cancelled.</summary>
    public int CancelledCount { get; set; }
}

/// <summary>
/// Aggregate state of the durable generation queue.
/// </summary>
public sealed class GenerationQueueStatus
{
    /// <summary>Gets or sets the number of queued jobs.</summary>
    public int QueuedCount { get; set; }

    /// <summary>Gets or sets the number of running jobs.</summary>
    public int RunningCount { get; set; }
}
