using Commander4K.PluginSdk;
using Microsoft.Data.Sqlite;

namespace Commander4K.Core.Indexing;

/// <summary>Unit of work executed by the single writer inside a batched transaction.</summary>
internal abstract record IndexWriteCommand
{
    public abstract void Execute(SqliteConnection connection, SqliteTransaction transaction);

    protected static SqliteCommand Create(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}

internal sealed record RecordVisitCommand(string Path, long UnixSeconds) : IndexWriteCommand
{
    public override void Execute(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Create(connection, transaction, """
            INSERT INTO frecency(path, visits, last_visit) VALUES ($path, 1, $now)
            ON CONFLICT(path) DO UPDATE SET visits = visits + 1, last_visit = excluded.last_visit;
            """);
        command.Parameters.AddWithValue("$path", Path);
        command.Parameters.AddWithValue("$now", UnixSeconds);
        command.ExecuteNonQuery();
    }
}

internal sealed record IndexEntriesCommand(IReadOnlyList<VfsEntry> Entries) : IndexWriteCommand
{
    public override void Execute(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Create(connection, transaction, """
            INSERT INTO files(path, name, is_dir, size, modified) VALUES ($path, $name, $dir, $size, $modified)
            ON CONFLICT(path) DO UPDATE SET name = excluded.name, is_dir = excluded.is_dir,
                size = excluded.size, modified = excluded.modified;
            """);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var dir = command.Parameters.Add("$dir", SqliteType.Integer);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var modified = command.Parameters.Add("$modified", SqliteType.Integer);
        command.Prepare();

        foreach (var entry in Entries)
        {
            if (entry.Kind is not (VfsEntryKind.File or VfsEntryKind.Directory))
            {
                continue;
            }

            path.Value = entry.Path;
            name.Value = entry.Name;
            dir.Value = entry.Kind == VfsEntryKind.Directory ? 1 : 0;
            size.Value = entry.Size;
            modified.Value = entry.LastWriteTime.ToUnixTimeSeconds();
            command.ExecuteNonQuery();
        }
    }
}

internal sealed record RemoveFromIndexCommand(string PathPrefix) : IndexWriteCommand
{
    public override void Execute(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Create(connection, transaction,
            @"DELETE FROM files WHERE path = $path OR path LIKE $prefix ESCAPE '\';");
        // Escaped separator followed by an unescaped wildcard: matches strictly below the prefix.
        var separator = PathPrefix.Contains('\\') ? @"\\" : "/";
        command.Parameters.AddWithValue("$path", PathPrefix);
        command.Parameters.AddWithValue("$prefix", SqlLike.Escape(PathPrefix.TrimEnd('\\', '/')) + separator + "%");
        command.ExecuteNonQuery();
    }
}

internal sealed record UpsertHostCommand(NetworkHostRecord Host) : IndexWriteCommand
{
    public override void Execute(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Create(connection, transaction, """
            INSERT INTO net_hosts(host, address, open_ports, last_seen) VALUES ($host, $address, $ports, $seen)
            ON CONFLICT(host) DO UPDATE SET address = COALESCE(excluded.address, address),
                open_ports = excluded.open_ports, last_seen = excluded.last_seen;
            """);
        command.Parameters.AddWithValue("$host", Host.Host);
        command.Parameters.AddWithValue("$address", (object?)Host.Address ?? DBNull.Value);
        command.Parameters.AddWithValue("$ports", string.Join(',', Host.OpenPorts));
        command.Parameters.AddWithValue("$seen", Host.LastSeen.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }
}

internal sealed record SetDirectorySizeCommand(string Path, long Bytes, long Files, long UnixSeconds) : IndexWriteCommand
{
    public override void Execute(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Create(connection, transaction, """
            INSERT INTO dir_sizes(path, size, file_count, computed_at) VALUES ($path, $size, $files, $at)
            ON CONFLICT(path) DO UPDATE SET size = excluded.size, file_count = excluded.file_count,
                computed_at = excluded.computed_at;
            """);
        command.Parameters.AddWithValue("$path", Path);
        command.Parameters.AddWithValue("$size", Bytes);
        command.Parameters.AddWithValue("$files", Files);
        command.Parameters.AddWithValue("$at", UnixSeconds);
        command.ExecuteNonQuery();
    }
}

/// <summary>Replaces the cached listing of one container (offline / cached browsing of slow shares).</summary>
internal sealed record CacheListingCommand(string Parent, IReadOnlyList<VfsEntry> Entries) : IndexWriteCommand
{
    public override void Execute(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var delete = Create(connection, transaction, "DELETE FROM dir_cache WHERE parent = $parent;"))
        {
            delete.Parameters.AddWithValue("$parent", Parent);
            delete.ExecuteNonQuery();
        }

        using var insert = Create(connection, transaction, """
            INSERT OR REPLACE INTO dir_cache(parent, name, path, kind, size, modified, description)
            VALUES ($parent, $name, $path, $kind, $size, $modified, $description);
            """);
        insert.Parameters.AddWithValue("$parent", Parent);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var path = insert.Parameters.Add("$path", SqliteType.Text);
        var kind = insert.Parameters.Add("$kind", SqliteType.Integer);
        var size = insert.Parameters.Add("$size", SqliteType.Integer);
        var modified = insert.Parameters.Add("$modified", SqliteType.Integer);
        var description = insert.Parameters.Add("$description", SqliteType.Text);
        insert.Prepare();

        foreach (var entry in Entries)
        {
            if (entry.Kind is VfsEntryKind.ParentLink or VfsEntryKind.Action)
            {
                continue;
            }

            name.Value = entry.Name;
            path.Value = entry.Path;
            kind.Value = (int)entry.Kind;
            size.Value = entry.Size;
            modified.Value = entry.LastWriteTime.ToUnixTimeSeconds();
            description.Value = (object?)entry.Description ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
    }
}

/// <summary>Barrier: completes once every command queued before it has been committed.</summary>
internal sealed record FlushCommand : IndexWriteCommand
{
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void Execute(SqliteConnection connection, SqliteTransaction transaction)
    {
    }
}

internal static class SqlLike
{
    public static string Escape(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
