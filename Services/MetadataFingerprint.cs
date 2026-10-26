using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyCAP.Models;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Produces stable fingerprints for the metadata fields managed by JellyCAP.
/// </summary>
internal static class MetadataFingerprint
{
    /// <summary>
    /// Creates a snapshot from a Jellyfin item.
    /// </summary>
    /// <param name="item">The source item.</param>
    /// <returns>The detached snapshot.</returns>
    public static MetadataSnapshot Snapshot(BaseItem item)
    {
        return new MetadataSnapshot
        {
            SortTitle = item.ForcedSortName ?? string.Empty,
            ContentRating = item.OfficialRating ?? string.Empty,
            Overview = item.Overview ?? string.Empty,
            Genres = Normalize(item.Genres),
            Tags = Normalize(item.Tags)
        };
    }

    /// <summary>
    /// Computes a fingerprint from a snapshot.
    /// </summary>
    /// <param name="snapshot">The metadata snapshot.</param>
    /// <returns>A lowercase hexadecimal SHA-256 digest.</returns>
    public static string Compute(MetadataSnapshot snapshot)
    {
        var canonical = string.Join(
            '\n',
            snapshot.SortTitle.Trim(),
            snapshot.ContentRating.Trim(),
            snapshot.Overview.Trim(),
            string.Join('\u001f', Normalize(snapshot.Genres)),
            string.Join('\u001f', Normalize(snapshot.Tags)));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string[] Normalize(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
