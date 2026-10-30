namespace Jellyfin.Plugin.JellyCAP.Models;

/// <summary>
/// One bounded page of results.
/// </summary>
/// <typeparam name="T">Result item type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>
    /// Gets or sets the items in this page.
    /// </summary>
    public IReadOnlyList<T> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the total number of matching items.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the zero-based index of the first requested item.
    /// </summary>
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of items in this page.
    /// </summary>
    public int PageSize { get; set; }
}

/// <summary>
/// A lightweight proposal count response.
/// </summary>
public sealed class ProposalCountResponse
{
    /// <summary>
    /// Gets or sets the number of matching proposals.
    /// </summary>
    public int Count { get; set; }
}

/// <summary>
/// Result of rejecting all pending proposals.
/// </summary>
public sealed class BulkProposalRejectionResponse
{
    /// <summary>
    /// Gets or sets the number of proposals rejected.
    /// </summary>
    public int RejectedCount { get; set; }
}

/// <summary>
/// Result of applying all pending proposals.
/// </summary>
public sealed class BulkProposalApprovalResponse
{
    /// <summary>
    /// Gets or sets the number of proposals applied successfully.
    /// </summary>
    public int AppliedCount { get; set; }

    /// <summary>
    /// Gets or sets the number of proposals that could not be applied and remain pending.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Gets or sets a bounded list of individual failures.
    /// </summary>
    public IReadOnlyList<BulkProposalApprovalFailure> Failures { get; set; } = [];
}

/// <summary>
/// One proposal that could not be applied during bulk approval.
/// </summary>
public sealed class BulkProposalApprovalFailure
{
    /// <summary>Gets or sets the proposal identifier.</summary>
    public Guid ProposalId { get; set; }

    /// <summary>Gets or sets the collection name.</summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>Gets or sets the bounded failure message.</summary>
    public string Message { get; set; } = string.Empty;
}
