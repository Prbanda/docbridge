using System.Collections.Concurrent;
using DocBridge.Shared.Clients;

namespace DocBridge.Shared.Jobs;

public static class TaskStates
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public sealed record TaskCounts(long Pending, long InProgress, long Completed, long Failed)
{
    public long Total => Pending + InProgress + Completed + Failed;

    /// <summary>
    /// Job status is derived on read (TDD): any failed with nothing pending or in progress → completed_with_errors;
    /// all completed → completed; all pending → pending; otherwise in_progress.
    /// </summary>
    public string Aggregate()
    {
        if (Pending == 0 && InProgress == 0)
        {
            return Failed > 0 ? "completed_with_errors" : "completed";
        }
        return Pending == Total ? "pending" : "in_progress";
    }

    /// <summary>One aggregate query for any set of jobs (the TDD's GET /jobs note), also used by workers for push counts.</summary>
    public const string CountsSql =
        """
        SELECT j.id AS job_id,
               count(*) FILTER (WHERE t.status = 'pending')     AS pending,
               count(*) FILTER (WHERE t.status = 'in_progress') AS in_progress,
               count(*) FILTER (WHERE t.status = 'completed')   AS completed,
               count(*) FILTER (WHERE t.status = 'failed')      AS failed
        FROM jobs j LEFT JOIN tasks t ON t.job_id = j.id
        WHERE j.id = ANY(@jobIds)
        GROUP BY j.id
        """;
}

public sealed record JobCountsRow(Guid JobId, long Pending, long InProgress, long Completed, long Failed)
{
    public TaskCounts Counts => new(Pending, InProgress, Completed, Failed);
}

/// <summary>
/// "A user can see any job submitted by a member of their teams": requester and submitter must share a team.
/// Team lists are cached for 60 s (TDD 3.1), so a dashboard refresh doesn't repeat the platform calls.
/// </summary>
public sealed class TeamAccess(PlatformClient platform, TimeProvider? clock = null)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlySet<Guid> Teams)> _cache = new();

    public async Task<bool> SharesTeamAsync(Guid requester, Guid submitter, CancellationToken ct = default)
    {
        if (requester == submitter)
        {
            return true;
        }
        var mine = await TeamsOf(requester, ct);
        if (mine.Count == 0)
        {
            return false;
        }
        var theirs = await TeamsOf(submitter, ct);
        return mine.Overlaps(theirs);
    }

    private async Task<IReadOnlySet<Guid>> TeamsOf(Guid userId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(userId, out var hit) && now - hit.At < Ttl)
        {
            return hit.Teams;
        }
        var teams = await platform.GetUserTeamIdsAsync(userId, ct);
        _cache[userId] = (now, teams);
        return teams;
    }
}
