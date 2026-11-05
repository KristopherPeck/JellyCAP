using Jellyfin.Plugin.JellyCAP.Models;
using Jellyfin.Plugin.JellyCAP.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.JellyCAP.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProposalStoreTests
{
    private string _temporaryDirectory = null!;
    private ProposalStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "JellyCAP.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
        _store = CreateStore();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ScopedActivityReturnsOnlyRequestedCollections()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await _store.EnqueueGenerationJobsAsync(
            [(firstId, "First"), (secondId, "Second")],
            CancellationToken.None);

        var scoped = await _store.GetCollectionActivityAsync([firstId], CancellationToken.None);
        var all = await _store.GetCollectionActivityAsync(CancellationToken.None);

        Assert.AreEqual(1, scoped.Count);
        Assert.IsTrue(scoped.ContainsKey(firstId));
        Assert.IsFalse(scoped.ContainsKey(secondId));
        Assert.AreEqual(GenerationJobStatus.Queued, scoped[firstId].ActiveJobStatus);
        Assert.AreEqual(2, all.Count);
    }

    [TestMethod]
    public async Task EmptyScopedActivityDoesNotCreateDatabase()
    {
        var result = await _store.GetCollectionActivityAsync([], CancellationToken.None);

        Assert.AreEqual(0, result.Count);
        Assert.IsFalse(File.Exists(GetDatabasePath()));
    }

    [TestMethod]
    public async Task QueuedJobsPersistAcrossStoreInstances()
    {
        var collectionId = Guid.NewGuid();
        await _store.EnqueueGenerationJobsAsync(
            [(collectionId, "Persistent")],
            CancellationToken.None);

        var reopenedStore = CreateStore();
        var activity = await reopenedStore.GetCollectionActivityAsync(
            [collectionId],
            CancellationToken.None);

        Assert.AreEqual(GenerationJobStatus.Queued, activity[collectionId].ActiveJobStatus);
    }

    [TestMethod]
    public async Task InterruptedRunningJobsReturnToQueue()
    {
        var collectionId = Guid.NewGuid();
        await _store.EnqueueGenerationJobsAsync(
            [(collectionId, "Interrupted")],
            CancellationToken.None);
        var claimed = await _store.ClaimGenerationJobAsync(1, CancellationToken.None);

        Assert.IsNotNull(claimed);
        Assert.AreEqual(GenerationJobStatus.Running, claimed.Status);

        var recoveredCount = await _store.RecoverInterruptedGenerationJobsAsync(CancellationToken.None);
        var activity = await _store.GetCollectionActivityAsync(
            [collectionId],
            CancellationToken.None);

        Assert.AreEqual(1, recoveredCount);
        Assert.AreEqual(GenerationJobStatus.Queued, activity[collectionId].ActiveJobStatus);
    }

    [TestMethod]
    public async Task CompletingJobPersistsPendingProposalAndGenerationHistory()
    {
        var collectionId = Guid.NewGuid();
        await _store.EnqueueGenerationJobsAsync(
            [(collectionId, "Completed")],
            CancellationToken.None);
        var claimed = await _store.ClaimGenerationJobAsync(1, CancellationToken.None);
        Assert.IsNotNull(claimed);
        var proposal = CreateProposal(collectionId, ProposalStatus.Pending);

        await _store.CompleteGenerationJobAsync(claimed.Id, proposal, CancellationToken.None);
        var activity = await _store.GetCollectionActivityAsync(
            [collectionId],
            CancellationToken.None);

        Assert.AreEqual(1L, activity[collectionId].GenerationCount);
        Assert.AreEqual(true, activity[collectionId].LastGenerationSucceeded);
        Assert.AreEqual(true, activity[collectionId].LastGenerationHadChanges);
        Assert.IsTrue(activity[collectionId].HasPendingReview);
        Assert.IsNull(activity[collectionId].ActiveJobStatus);
    }

    [TestMethod]
    public async Task CompletingUnchangedJobDoesNotCreatePendingReview()
    {
        var collectionId = Guid.NewGuid();
        await _store.EnqueueGenerationJobsAsync(
            [(collectionId, "Unchanged")],
            CancellationToken.None);
        var claimed = await _store.ClaimGenerationJobAsync(1, CancellationToken.None);
        Assert.IsNotNull(claimed);
        var proposal = CreateProposal(collectionId, ProposalStatus.Unchanged);

        await _store.CompleteGenerationJobAsync(claimed.Id, proposal, CancellationToken.None);
        var activity = await _store.GetCollectionActivityAsync(
            [collectionId],
            CancellationToken.None);

        Assert.AreEqual(1L, activity[collectionId].GenerationCount);
        Assert.AreEqual(false, activity[collectionId].LastGenerationHadChanges);
        Assert.IsFalse(activity[collectionId].HasPendingReview);
    }

    private ProposalStore CreateStore()
    {
        return new ProposalStore(
            NullLogger<ProposalStore>.Instance,
            GetDatabasePath(),
            Path.Combine(_temporaryDirectory, "proposals.json"));
    }

    private string GetDatabasePath()
    {
        return Path.Combine(_temporaryDirectory, "jellycap.db");
    }

    private static MetadataProposal CreateProposal(Guid collectionId, ProposalStatus status)
    {
        return new MetadataProposal
        {
            CollectionId = collectionId,
            CollectionName = "Test Collection",
            Status = status,
            Original = new MetadataSnapshot
            {
                Overview = "Original overview"
            },
            Proposed = new GeneratedMetadata
            {
                Overview = status == ProposalStatus.Unchanged
                    ? "Original overview"
                    : "Proposed overview"
            },
            OriginalFingerprint = "test-fingerprint",
            FingerprintVersion = MetadataFingerprint.CurrentVersion,
            Provider = "test",
            Model = "test-model"
        };
    }
}
