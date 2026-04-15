using System.Text;
using SpotCont.Models;

namespace SpotCont.Services;

public static class SearchTextUtility
{
    public static SearchRequest CreateRequest(string? query)
    {
        var trimmedQuery = query?.Trim() ?? string.Empty;
        var normalizedQuery = Normalize(trimmedQuery);
        var tokens = string.IsNullOrWhiteSpace(normalizedQuery)
            ? Array.Empty<string>()
            : normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new SearchRequest(trimmedQuery, normalizedQuery, tokens, string.IsNullOrWhiteSpace(normalizedQuery));
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = true;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasSeparator = false;
                continue;
            }

            if (!lastWasSeparator)
            {
                builder.Append(' ');
                lastWasSeparator = true;
            }
        }

        return builder.ToString().Trim();
    }

    public static bool TryGetMatchQuality(IndexedItem item, SearchRequest request, out double matchQuality)
    {
        return TryGetMatchQuality(item.Title, item.SearchText, request, out matchQuality);
    }

    public static bool TryGetMatchQuality(
        string title,
        string searchText,
        SearchRequest request,
        out double matchQuality)
    {
        if (request.IsEmpty)
        {
            matchQuality = 0;
            return false;
        }

        var normalizedTitle = Normalize(title);
        var normalizedSearchText = Normalize($"{title} {searchText}");

        if (string.IsNullOrWhiteSpace(normalizedSearchText))
        {
            matchQuality = 0;
            return false;
        }

        if (normalizedTitle.Equals(request.NormalizedQuery, StringComparison.Ordinal))
        {
            matchQuality = 1.0;
            return true;
        }

        if (normalizedTitle.StartsWith(request.NormalizedQuery, StringComparison.Ordinal))
        {
            var penalty = Math.Min(0.08, Math.Max(0, normalizedTitle.Length - request.NormalizedQuery.Length) * 0.0025);
            matchQuality = 0.96 - penalty;
            return true;
        }

        if (ContainsWordPrefix(normalizedTitle, request.NormalizedQuery))
        {
            matchQuality = 0.9;
            return true;
        }

        if (request.Tokens.Count > 0 && request.Tokens.All(token => ContainsWordPrefix(normalizedSearchText, token)))
        {
            matchQuality = 0.84;
            return true;
        }

        if (normalizedTitle.Contains(request.NormalizedQuery, StringComparison.Ordinal))
        {
            matchQuality = 0.78;
            return true;
        }

        if (normalizedSearchText.Contains(request.NormalizedQuery, StringComparison.Ordinal))
        {
            matchQuality = 0.7;
            return true;
        }

        if (request.Tokens.Count > 0 && request.Tokens.All(token => normalizedSearchText.Contains(token, StringComparison.Ordinal)))
        {
            matchQuality = 0.62;
            return true;
        }

        if (TryGetSubsequencePenalty(normalizedTitle, request.NormalizedQuery, out var titlePenalty))
        {
            matchQuality = Math.Max(0.44, 0.62 - titlePenalty * 0.01);
            return true;
        }

        if (TryGetSubsequencePenalty(normalizedSearchText, request.NormalizedQuery, out var searchPenalty))
        {
            matchQuality = Math.Max(0.34, 0.52 - searchPenalty * 0.0065);
            return true;
        }

        matchQuality = 0;
        return false;
    }

    private static bool ContainsWordPrefix(string normalizedValue, string normalizedQuery)
    {
        return normalizedValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.StartsWith(normalizedQuery, StringComparison.Ordinal));
    }

    private static bool TryGetSubsequencePenalty(string normalizedValue, string normalizedQuery, out int penalty)
    {
        penalty = 0;

        if (normalizedQuery.Length < 2 || normalizedValue.Length < normalizedQuery.Length)
        {
            return false;
        }

        var previousIndex = -1;

        foreach (var character in normalizedQuery)
        {
            var currentIndex = normalizedValue.IndexOf(character, previousIndex + 1);
            if (currentIndex < 0)
            {
                penalty = 0;
                return false;
            }

            if (previousIndex >= 0)
            {
                penalty += currentIndex - previousIndex - 1;
            }
            else
            {
                penalty += currentIndex;
            }

            previousIndex = currentIndex;
        }

        penalty += normalizedValue.Length - normalizedQuery.Length;
        return true;
    }
}
