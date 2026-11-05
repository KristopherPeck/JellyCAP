using Jellyfin.Plugin.JellyCAP.Models;
using Jellyfin.Plugin.JellyCAP.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.JellyCAP.Tests;

[TestClass]
public sealed class MetadataFingerprintTests
{
    [TestMethod]
    public void ComputeIsStableAcrossListOrderWhitespaceAndDuplicates()
    {
        var first = CreateSnapshot();
        first.Genres = ["Science Fiction", "Drama"];
        first.Tags = ["Dystopian", "Classic"];
        var second = CreateSnapshot();
        second.SortTitle = "  Example Collection  ";
        second.Overview = "  An example overview.  ";
        second.Genres = [" Drama ", "Science Fiction", "Drama"];
        second.Tags = ["Classic", " Dystopian ", "Classic"];

        Assert.AreEqual(MetadataFingerprint.Compute(first), MetadataFingerprint.Compute(second));
    }

    [TestMethod]
    public void ComputeReturnsLowercaseSha256Digest()
    {
        var result = MetadataFingerprint.Compute(CreateSnapshot());

        Assert.AreEqual(64, result.Length);
        Assert.IsTrue(result.All(character => char.IsDigit(character) || character is >= 'a' and <= 'f'));
    }

    [TestMethod]
    public void ComputeChangesWhenManagedReleaseMetadataChanges()
    {
        var original = CreateSnapshot();
        var changed = CreateSnapshot();
        changed.ProductionYear++;
        changed.PremiereDate = changed.PremiereDate?.AddDays(1);

        Assert.AreNotEqual(MetadataFingerprint.Compute(original), MetadataFingerprint.Compute(changed));
    }

    [TestMethod]
    public void ComputeVersion2IgnoresReleaseMetadata()
    {
        var original = CreateSnapshot();
        var changed = CreateSnapshot();
        changed.ProductionYear++;
        changed.PremiereDate = changed.PremiereDate?.AddYears(1);

        Assert.AreEqual(MetadataFingerprint.ComputeVersion2(original), MetadataFingerprint.ComputeVersion2(changed));
    }

    [TestMethod]
    public void ComputeLegacyIgnoresCommunityAndReleaseMetadata()
    {
        var original = CreateSnapshot();
        var changed = CreateSnapshot();
        changed.CommunityRating = 1.2f;
        changed.ProductionYear = 1999;
        changed.PremiereDate = new DateTime(1999, 1, 1, 12, 30, 0, DateTimeKind.Local);

        Assert.AreEqual(MetadataFingerprint.ComputeLegacy(original), MetadataFingerprint.ComputeLegacy(changed));
        Assert.AreNotEqual(MetadataFingerprint.Compute(original), MetadataFingerprint.Compute(changed));
    }

    [TestMethod]
    public void ComputeUsesOnlyTheCalendarDateOfPremiereDate()
    {
        var morning = CreateSnapshot();
        morning.PremiereDate = new DateTime(2020, 5, 4, 1, 2, 3, DateTimeKind.Utc);
        var evening = CreateSnapshot();
        evening.PremiereDate = new DateTime(2020, 5, 4, 23, 59, 59, DateTimeKind.Utc);

        Assert.AreEqual(MetadataFingerprint.Compute(morning), MetadataFingerprint.Compute(evening));
    }

    private static MetadataSnapshot CreateSnapshot()
    {
        return new MetadataSnapshot
        {
            SortTitle = "Example Collection",
            ContentRating = "PG-13",
            CommunityRating = 7.5f,
            ProductionYear = 2020,
            PremiereDate = new DateTime(2020, 5, 4, 0, 0, 0, DateTimeKind.Utc),
            Overview = "An example overview.",
            Genres = ["Drama"],
            Tags = ["Classic"]
        };
    }
}
