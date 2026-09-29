using System.Collections.Concurrent;
using Esi.Core.Storage;
using Esi.Platform.Machine;
using Esi.Platform.Telemetry;
using Microsoft.Data.Sqlite;

namespace Esi.Core.Notifications;

/// <summary>
/// Evaluation state for one rule, persisted so a condition does not re-fire each sample.
/// <para>
/// The "condition since" field is a Unix timestamp rather than a <c>DateTimeOffset</c>
/// because that is what is stored, and converting on every rule on every sample is
/// needless work in a loop that runs once a second per rule.
/// </para>
/// </summary>
public sealed record RuleState(long? ConditionSinceUnix, long? LastFiredAt, long? SuppressUntil);

/// <summary>
/// Evaluates the notification rules against each metrics sample and raises
/// notifications for conditions that are genuinely sustained.
/// <para>
/// The design point is the sustain/cooldown pair. A threshold rule alone is unusable: CPU
/// hits 95 % for two seconds every time a game starts, and the phone would light up. So a
/// rule must hold continuously for <c>SustainSeconds</c> before it fires, and cannot fire
/// again for <c>CooldownSeconds</c> afterwards. The "condition since" timestamp is
/// persisted so the behaviour survives an agent restart.
/// </para>
/// </summary>
public sealed class NotificationEngine(
    ILogger<NotificationEngine> log,
    AgentDatabase database,
    SettingsStore settings)
{
    private readonly ConcurrentDictionary<string, RuleState> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a notification should be created.</summary>
    public event EventHandler<NotificationDraft>? NotificationRaised;

    public void Prime(IEnumerable<NotificationRuleDto> rules)
    {
        foreach (var rule in rules)
        {
            if (_state.ContainsKey(rule.Id))
                continue;

            _state[rule.Id] = LoadState(rule.Id) ?? new RuleState(null, null, null);
        }

        // Drop state for rules that no longer exist so the table does not grow forever.
        var ids = rules.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _state.Keys.Where(k => !ids.Contains(k)).ToList())
            _state.TryRemove(key, out _);
    }

    /// <summary>Records a one-off event that a rule may key on.</summary>
    public void RaiseEvent(string kind, string title, string? body, NotificationSeverity severity = NotificationSeverity.Info)
    {
        var draft = new NotificationDraft
        {
            Kind = kind,
            Severity = severity.ToString().ToLowerInvariant(),
            Title = title,
            Body = body,
            Source = "agent",
        };

        NotificationRaised?.Invoke(this, draft);
        log.LogInformation("Notification: [{Severity}] {Title}{Body}",
            draft.Severity, title, body is null ? string.Empty : " — " + body);
    }

    /// <summary>Evaluates every enabled threshold rule against one metrics sample.</summary>
    public void Evaluate(MetricsResponse metrics)
    {
        var rules = settings.Current.Notifications.Rules;
        if (rules.Count == 0)
            return;

        var now = AgentDatabase.NowUnix();
        var candidates = new List<(NotificationRuleDto Rule, bool Tripped, string? Body)>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.BuiltIn || rule.Threshold is not { } threshold)
                continue;

            var (tripped, body) = EvaluateCondition(rule, metrics);

            var state = _state.GetOrAdd(rule.Id, id => LoadState(id) ?? new RuleState(null, null, null));

            // ---- Suppression ----------------------------------------------------
            if (state.SuppressUntil is { } until && now < until)
                continue;

            // ---- Sustain --------------------------------------------------------
            if (!tripped)
            {
                if (state.ConditionSinceUnix is not null)
                    _state[rule.Id] = state with { ConditionSinceUnix = null };
                continue;
            }

            var since = state.ConditionSinceUnix;
            if (since is null)
            {
                if (rule.SustainSeconds <= 0)
                {
                    // Fires immediately: the condition is already meaningful on its own
                    // (a 97% full disk, for example).
                    candidates.Add((rule, true, body));
                    _state[rule.Id] = state with
                    {
                        ConditionSinceUnix = now,
                        LastFiredAt = now,
                        SuppressUntil = now + rule.CooldownSeconds,
                    };
                }
                else
                {
                    _state[rule.Id] = state with { ConditionSinceUnix = now };
                }

                continue;
            }

            if (now - since.Value >= rule.SustainSeconds)
            {
                candidates.Add((rule, true, body));
                _state[rule.Id] = state with
                {
                    LastFiredAt = now,
                    SuppressUntil = now + rule.CooldownSeconds,
                };
            }
        }

        foreach (var (rule, _, body) in candidates)
        {
            var draft = new NotificationDraft
            {
                Kind = ConditionKind(rule.Id),
                Severity = rule.Severity,
                Title = rule.Title,
                Body = body ?? rule.Body,
                Source = "rule",
                Data = new Dictionary<string, string>
                {
                    ["ruleId"] = rule.Id,
                    ["condition"] = rule.Condition,
                },
            };

            NotificationRaised?.Invoke(this, draft);
            log.LogWarning("Rule {RuleId} fired: {Title} — {Body}", rule.Id, rule.Title, draft.Body);

            PersistState(rule.Id, _state[rule.Id]);
        }

        PersistDirty();
    }

    private static (bool Tripped, string? Body) EvaluateCondition(NotificationRuleDto rule, MetricsResponse metrics)
    {
        var threshold = rule.Threshold ?? 0;

        switch (rule.Id)
        {
            case NotificationRuleCatalog.CpuHigh:
            case NotificationRuleCatalog.CpuCritical:
                return (metrics.Cpu.UsagePercent >= threshold,
                    $"CPU is at {metrics.Cpu.UsagePercent:F0}% (threshold {threshold:F0}%).");

            case NotificationRuleCatalog.GpuTempHigh:
            case NotificationRuleCatalog.GpuTempCritical:
            case NotificationRuleCatalog.GpuOverheat:
            {
                var temp = metrics.GpuAggregate.MaxTemperatureC;
                // No trustworthy sensor means the rule is simply not evaluable, which is
                // a different thing from the condition being false.
                if (temp is null)
                    return (false, null);

                return (temp >= threshold,
                    $"The GPU is at {temp:F0}°C (threshold {threshold:F0}°C).");
            }

            case NotificationRuleCatalog.RamHigh:
                return (metrics.Memory.UsedPercent >= threshold,
                    $"RAM is at {metrics.Memory.UsedPercent:F0}% of {FormatBytes(metrics.Memory.TotalBytes)}.");

            case NotificationRuleCatalog.DiskLow:
            case NotificationRuleCatalog.DiskCritical:
            {
                var worst = metrics.Storage
                    .Where(s => !s.DeviceId.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.UsedPercent)
                    .FirstOrDefault();

                if (worst is null)
                    return (false, null);

                return (worst.UsedPercent >= threshold,
                    $"{worst.DeviceId} is {worst.UsedPercent:F0}% full " +
                    $"({FormatBytes(worst.FreeBytes)} free of {FormatBytes(worst.TotalBytes)}).");
            }

            case NotificationRuleCatalog.BatteryLow:
            {
                var charge = metrics.Battery?.ChargePercent;
                if (charge is null)
                    return (false, null);

                return (charge <= threshold, $"Battery is at {charge:F0}%.");
            }
            default:
                return (false, null);
        }
    }

    private static string ConditionKind(string ruleId) => ruleId switch
    {
        NotificationRuleCatalog.CpuHigh or NotificationRuleCatalog.CpuCritical => "alert",
        NotificationRuleCatalog.GpuTempHigh or NotificationRuleCatalog.GpuTempCritical
            or NotificationRuleCatalog.GpuOverheat => "alert",
        NotificationRuleCatalog.RamHigh or NotificationRuleCatalog.DiskLow
            or NotificationRuleCatalog.DiskCritical or NotificationRuleCatalog.BatteryLow => "alert",
        _ => "system",
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F1} {units[unit]}";
    }

    // -----------------------------------------------------------------------------
    // Persistence
    // -----------------------------------------------------------------------------

    private RuleState? LoadState(string ruleId)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT condition_since, last_fired_at, suppress_until
                  FROM rule_state WHERE rule_id = $id;
                """;
            command.Parameters.AddWithValue("$id", ruleId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new RuleState(
                ConditionSinceUnix: reader["condition_since"] is DBNull ? null : Convert.ToInt64(reader["condition_since"]),
                LastFiredAt: reader["last_fired_at"] is DBNull ? null : Convert.ToInt64(reader["last_fired_at"]),
                SuppressUntil: reader["suppress_until"] is DBNull ? null : Convert.ToInt64(reader["suppress_until"]));
        }
        catch
        {
            return null;
        }
    }

    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _dirtyLock = new();

    private void MarkDirty(string ruleId)
    {
        lock (_dirtyLock)
            _dirty.Add(ruleId);
    }

    private void PersistState(string ruleId, RuleState state)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO rule_state (rule_id, condition_since, last_fired_at, suppress_until)
                VALUES ($id, $since, $fired, $suppress)
                ON CONFLICT(rule_id) DO UPDATE SET
                    condition_since = excluded.condition_since,
                    last_fired_at = excluded.last_fired_at,
                    suppress_until = excluded.suppress_until;
                """;
            command.Parameters.AddWithValue("$id", ruleId);
            command.Parameters.AddWithValue("$since", AgentDatabase.DbValue(state.ConditionSinceUnix));
            command.Parameters.AddWithValue("$fired", state.LastFiredAt ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$suppress", state.SuppressUntil ?? (object)DBNull.Value);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not persist the state of rule {RuleId}", ruleId);
        }
    }

    /// <summary>Writes every changed rule state once per sampling cycle, not once per rule.</summary>
    private void PersistDirty()
    {
        string[] ids;
        lock (_dirtyLock)
        {
            if (_dirty.Count == 0)
                return;
            ids = [.. _dirty];
            _dirty.Clear();
        }

        foreach (var id in ids)
        {
            if (_state.TryGetValue(id, out var state))
                PersistState(id, state);
        }
    }

    /// <summary>Clears all rule state. Used when rules are edited so they re-evaluate cleanly.</summary>
    public void ResetState()
    {
        _state.Clear();

        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM rule_state";
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not reset the rule state table");
        }
    }
}

/// <summary>A notification that has not been persisted yet.</summary>
public sealed class NotificationDraft
{
    public string Kind { get; init; } = "system";
    public string Severity { get; init; } = "info";
    public string Title { get; init; } = "";
    public string? Body { get; init; }
    public string? Source { get; init; }
    public string? DeviceId { get; init; }
    public Dictionary<string, string>? Data { get; init; }
}
