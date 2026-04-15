using System.IO;
using System.Text.Json;
using SpotCont.Models;

namespace SpotCont.Services;

public sealed class UsageHistoryService
{
    private const int SaveRetryCount = 3;
    private const int SaveRetryDelayMs = 80;
    private const int MaxTrackedQueries = 320;
    private const int MaxResultsPerQuery = 36;

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly string _historyFilePath;
    private Dictionary<string, UsageEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, QueryUsageState> _queryUsageEntries = new(StringComparer.Ordinal);

    public UsageHistoryService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpotCont");

        _historyFilePath = Path.Combine(appDataPath, "usage-history.json");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_historyFilePath))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_historyFilePath);
            var snapshot = await JsonSerializer.DeserializeAsync<UsageSnapshot>(stream, cancellationToken: cancellationToken);
            if (snapshot is null)
            {
                return;
            }

            lock (_syncRoot)
            {
                _entries = snapshot.Entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
                    .ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);

                _queryUsageEntries = new Dictionary<string, QueryUsageState>(StringComparer.Ordinal);

                foreach (var queryUsageEntry in snapshot.QueryUsage)
                {
                    var normalizedQuery = NormalizeQuery(queryUsageEntry.Query);
                    if (string.IsNullOrWhiteSpace(normalizedQuery))
                    {
                        continue;
                    }

                    var state = new QueryUsageState
                    {
                        Query = normalizedQuery,
                        LastLaunchedUtc = queryUsageEntry.LastLaunchedUtc
                    };

                    foreach (var resultUsageEntry in queryUsageEntry.Results)
                    {
                        if (string.IsNullOrWhiteSpace(resultUsageEntry.Id) ||
                            resultUsageEntry.LaunchCount <= 0)
                        {
                            continue;
                        }

                        state.Results[resultUsageEntry.Id] = new QueryResultUsageState
                        {
                            Id = resultUsageEntry.Id,
                            LaunchCount = resultUsageEntry.LaunchCount,
                            LastLaunchedUtc = resultUsageEntry.LastLaunchedUtc
                        };
                    }

                    if (state.Results.Count == 0)
                    {
                        continue;
                    }

                    _queryUsageEntries[normalizedQuery] = state;
                }

                TrimQueryUsage_NoLock();
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                _entries = new Dictionary<string, UsageEntry>(StringComparer.OrdinalIgnoreCase);
                _queryUsageEntries = new Dictionary<string, QueryUsageState>(StringComparer.Ordinal);
            }
        }
    }

    public UsageEntry? GetUsage(string id)
    {
        lock (_syncRoot)
        {
            return _entries.TryGetValue(id, out var entry)
                ? new UsageEntry
                {
                    Id = entry.Id,
                    LaunchCount = entry.LaunchCount,
                    LastLaunchedUtc = entry.LastLaunchedUtc
                }
                : null;
        }
    }

    public IReadOnlyDictionary<string, double> GetLearningScores(
        string normalizedQuery,
        IReadOnlyCollection<string> resultIds)
    {
        var normalizedSearchQuery = NormalizeQuery(normalizedQuery);
        if (string.IsNullOrWhiteSpace(normalizedSearchQuery) || resultIds.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        var candidates = resultIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        lock (_syncRoot)
        {
            if (_queryUsageEntries.Count == 0)
            {
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            var now = DateTimeOffset.UtcNow;
            var rawScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var maxScore = 0d;

            foreach (var queryState in _queryUsageEntries.Values)
            {
                var queryRelationWeight = GetQueryRelationWeight(queryState.Query, normalizedSearchQuery);
                if (queryRelationWeight <= 0)
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (!queryState.Results.TryGetValue(candidate, out var usage) || usage.LaunchCount <= 0)
                    {
                        continue;
                    }

                    var recencyWeight = Math.Exp(-Math.Max(0, (now - usage.LastLaunchedUtc).TotalDays) / 45.0);
                    var launchWeight = Math.Log(usage.LaunchCount + 1);
                    var contribution = launchWeight * (0.65 + recencyWeight * 0.35) * queryRelationWeight;

                    var accumulatedScore = rawScores.TryGetValue(candidate, out var existingScore)
                        ? existingScore + contribution
                        : contribution;

                    rawScores[candidate] = accumulatedScore;
                    maxScore = Math.Max(maxScore, accumulatedScore);
                }
            }

            if (maxScore <= 0)
            {
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            return rawScores.ToDictionary(
                pair => pair.Key,
                pair => Math.Clamp(pair.Value / maxScore, 0, 1),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public Task RecordLaunchAsync(SearchResultItem item, CancellationToken cancellationToken = default)
    {
        return RecordLaunchAsync(item, null, cancellationToken);
    }

    public async Task RecordLaunchAsync(
        SearchResultItem item,
        string? queryContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            return;
        }

        UsageSnapshot snapshot;
        var launchedAtUtc = DateTimeOffset.UtcNow;
        var normalizedQueryContext = NormalizeQuery(queryContext);

        lock (_syncRoot)
        {
            if (!_entries.TryGetValue(item.Id, out var entry))
            {
                entry = new UsageEntry
                {
                    Id = item.Id
                };
                _entries[item.Id] = entry;
            }

            entry.LaunchCount++;
            entry.LastLaunchedUtc = launchedAtUtc;

            UpdateQueryUsage_NoLock(item.Id, normalizedQueryContext, launchedAtUtc);
            snapshot = CreateSnapshot_NoLock();
        }

        var directoryPath = Path.GetDirectoryName(_historyFilePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await PersistSnapshotAsync(snapshot, cancellationToken);
    }

    private void UpdateQueryUsage_NoLock(
        string resultId,
        string normalizedQuery,
        DateTimeOffset launchedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return;
        }

        if (!_queryUsageEntries.TryGetValue(normalizedQuery, out var queryUsageState))
        {
            queryUsageState = new QueryUsageState
            {
                Query = normalizedQuery
            };
            _queryUsageEntries[normalizedQuery] = queryUsageState;
        }

        queryUsageState.LastLaunchedUtc = launchedAtUtc;

        if (!queryUsageState.Results.TryGetValue(resultId, out var resultUsageState))
        {
            resultUsageState = new QueryResultUsageState
            {
                Id = resultId
            };
            queryUsageState.Results[resultId] = resultUsageState;
        }

        resultUsageState.LaunchCount++;
        resultUsageState.LastLaunchedUtc = launchedAtUtc;

        if (queryUsageState.Results.Count > MaxResultsPerQuery)
        {
            queryUsageState.Results = queryUsageState.Results.Values
                .OrderByDescending(entry => entry.LaunchCount)
                .ThenByDescending(entry => entry.LastLaunchedUtc)
                .Take(MaxResultsPerQuery)
                .ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        }

        TrimQueryUsage_NoLock();
    }

    private void TrimQueryUsage_NoLock()
    {
        if (_queryUsageEntries.Count <= MaxTrackedQueries)
        {
            return;
        }

        _queryUsageEntries = _queryUsageEntries.Values
            .OrderByDescending(entry => entry.LastLaunchedUtc)
            .Take(MaxTrackedQueries)
            .ToDictionary(entry => entry.Query, StringComparer.Ordinal);
    }

    private UsageSnapshot CreateSnapshot_NoLock()
    {
        return new UsageSnapshot
        {
            Entries = _entries.Values
                .Select(existing => new UsageEntry
                {
                    Id = existing.Id,
                    LaunchCount = existing.LaunchCount,
                    LastLaunchedUtc = existing.LastLaunchedUtc
                })
                .ToList(),
            QueryUsage = _queryUsageEntries.Values
                .OrderByDescending(entry => entry.LastLaunchedUtc)
                .Select(entry => new QueryUsageEntry
                {
                    Query = entry.Query,
                    LastLaunchedUtc = entry.LastLaunchedUtc,
                    Results = entry.Results.Values
                        .OrderByDescending(result => result.LaunchCount)
                        .ThenByDescending(result => result.LastLaunchedUtc)
                        .Select(result => new QueryResultUsageEntry
                        {
                            Id = result.Id,
                            LaunchCount = result.LaunchCount,
                            LastLaunchedUtc = result.LastLaunchedUtc
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private async Task PersistSnapshotAsync(UsageSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temporaryFilePath = $"{_historyFilePath}.tmp";

        for (var attempt = 0; attempt < SaveRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using (var stream = new FileStream(
                                 temporaryFilePath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.Read))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, SaveOptions, cancellationToken);
                }

                File.Move(temporaryFilePath, _historyFilePath, true);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException) when (attempt < SaveRetryCount - 1)
            {
                await Task.Delay(SaveRetryDelayMs, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < SaveRetryCount - 1)
            {
                await Task.Delay(SaveRetryDelayMs, cancellationToken);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryFilePath))
                    {
                        File.Delete(temporaryFilePath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static string NormalizeQuery(string? query)
    {
        var normalizedQuery = SearchTextUtility.Normalize(query ?? string.Empty);
        if (normalizedQuery.Length <= 96)
        {
            return normalizedQuery;
        }

        return normalizedQuery[..96];
    }

    private static double GetQueryRelationWeight(string historyQuery, string searchQuery)
    {
        if (string.Equals(historyQuery, searchQuery, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (historyQuery.StartsWith(searchQuery, StringComparison.Ordinal) ||
            searchQuery.StartsWith(historyQuery, StringComparison.Ordinal))
        {
            return 0.78;
        }

        if (historyQuery.Contains(searchQuery, StringComparison.Ordinal) ||
            searchQuery.Contains(historyQuery, StringComparison.Ordinal))
        {
            return 0.6;
        }

        var historyTokens = historyQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var searchTokens = searchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (historyTokens.Length == 0 || searchTokens.Length == 0)
        {
            return 0;
        }

        var historyTokenSet = new HashSet<string>(historyTokens, StringComparer.Ordinal);
        var overlap = searchTokens.Count(historyTokenSet.Contains);
        if (overlap == 0)
        {
            return 0;
        }

        var coverage = overlap / (double)Math.Max(historyTokens.Length, searchTokens.Length);
        return 0.42 + coverage * 0.36;
    }

    public sealed class UsageEntry
    {
        public string Id { get; set; } = string.Empty;

        public int LaunchCount { get; set; }

        public DateTimeOffset LastLaunchedUtc { get; set; }
    }

    private sealed class QueryUsageState
    {
        public string Query { get; set; } = string.Empty;

        public DateTimeOffset LastLaunchedUtc { get; set; }

        public Dictionary<string, QueryResultUsageState> Results { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class QueryResultUsageState
    {
        public string Id { get; set; } = string.Empty;

        public int LaunchCount { get; set; }

        public DateTimeOffset LastLaunchedUtc { get; set; }
    }

    private sealed class UsageSnapshot
    {
        public List<UsageEntry> Entries { get; set; } = new();

        public List<QueryUsageEntry> QueryUsage { get; set; } = new();
    }

    private sealed class QueryUsageEntry
    {
        public string Query { get; set; } = string.Empty;

        public DateTimeOffset LastLaunchedUtc { get; set; }

        public List<QueryResultUsageEntry> Results { get; set; } = new();
    }

    private sealed class QueryResultUsageEntry
    {
        public string Id { get; set; } = string.Empty;

        public int LaunchCount { get; set; }

        public DateTimeOffset LastLaunchedUtc { get; set; }
    }
}
