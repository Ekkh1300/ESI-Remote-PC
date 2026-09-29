using Esi.Core.Notifications;
using Esi.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Esi.Core.Notifications;

/// <summary>Persistence and query surface for notifications.</summary>
public sealed class NotificationStore(AgentDatabase database)
{
    /// <summary>Creates a notification from a draft and returns the stored row.</summary>
    public NotificationDto Create(NotificationDraft draft)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notifications (kind, severity, title, body, created_at, is_read, source, device_id, data)
            VALUES ($kind, $severity, $title, $body, $now, 0, $source, $deviceId, $data)
            RETURNING id;
            """;

        var now = AgentDatabase.NowUnix();
        command.Parameters.AddWithValue("$kind", draft.Kind);
        command.Parameters.AddWithValue("$severity", draft.Severity);
        command.Parameters.AddWithValue("$title", draft.Title);
        command.Parameters.AddWithValue("$body", AgentDatabase.DbValue(draft.Body));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$source", AgentDatabase.DbValue(draft.Source));
        command.Parameters.AddWithValue("$deviceId", AgentDatabase.DbValue(draft.DeviceId));
        command.Parameters.AddWithValue("$data",
            draft.Data is null ? (object)DBNull.Value : AgentDatabase.ToJson(draft.Data));

        var id = Convert.ToInt64(command.ExecuteScalar());

        return new NotificationDto
        {
            Id = id,
            Kind = draft.Kind,
            Severity = draft.Severity,
            Title = draft.Title,
            Body = draft.Body,
            CreatedAt = now,
            IsRead = false,
            Source = draft.Source,
            DeviceId = draft.DeviceId,
            Data = draft.Data,
        };
    }

    public NotificationListResponse List(bool unreadOnly = false, int limit = 100, long? since = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT id, kind, severity, title, body, created_at, is_read, source, device_id, data
              FROM notifications
             WHERE ({AgentDatabase.ToDbBool(unreadOnly)} = 0 OR is_read = 0)
               AND ($since IS NULL OR created_at >= $since)
             ORDER BY id DESC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$since", since ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var items = new List<NotificationDto>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                items.Add(Map(reader));
        }

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM notifications WHERE is_read = 0";
        var unread = Convert.ToInt32(countCommand.ExecuteScalar());

        return new NotificationListResponse
        {
            Notifications = items,
            UnreadCount = unread,
            Total = items.Count,
        };
    }

    public int UnreadCount()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notifications WHERE is_read = 0";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public bool MarkRead(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE notifications SET is_read = 1 WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public int MarkAllRead()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE notifications SET is_read = 1 WHERE is_read = 0";
        return command.ExecuteNonQuery();
    }

    public bool Delete(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notifications WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public int DeleteAll()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notifications";
        return command.ExecuteNonQuery();
    }

    /// <summary>Removes notifications older than the retention window.</summary>
    public int Purge(int retentionDays)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notifications WHERE created_at < $cutoff";
        command.Parameters.AddWithValue("$cutoff", AgentDatabase.NowUnix() - (long)TimeSpan.FromDays(retentionDays).TotalSeconds);
        return command.ExecuteNonQuery();
    }

    private static NotificationDto Map(SqliteDataReader reader) => new()
    {
        Id = Convert.ToInt64(reader["id"]),
        Kind = (string)reader["kind"],
        Severity = (string)reader["severity"],
        Title = (string)reader["title"],
        Body = reader["body"] is DBNull ? null : (string)reader["body"],
        CreatedAt = Convert.ToInt64(reader["created_at"]),
        IsRead = Convert.ToInt32(reader["is_read"]) != 0,
        Source = reader["source"] is DBNull ? null : (string)reader["source"],
        DeviceId = reader["device_id"] is DBNull ? null : (string)reader["device_id"],
        Data = reader["data"] is DBNull ? null : AgentDatabase.FromJson<Dictionary<string, string>>((string)reader["data"]),
    };
}
