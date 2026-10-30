using System.Diagnostics;
using System.Threading.Channels;
using Commander4K.PluginSdk;
using Microsoft.Data.Sqlite;

namespace Commander4K.Core.Indexing;

/// <summary>
/// SQLite-backed cache and index (frecency navigation, FTS5 trigram file search, LAN host history, directory sizes,
/// offline listings). All writes funnel through one <see cref="Channel{T}"/> drained by a single writer that commits
/// in batches; reads run concurrently on pooled read-only connections. Microsoft.Data.Sqlite's async API is
/// synchronous underneath, so every read is moved to the thread pool explicitly.
/// </summary>
public sealed class SqliteIndexService : IAsyncDisposable
{
    private const int MaxCommandsPerTransaction = 2_000;

    private const string Pragmas = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;
        PRAGMA mmap_size = 268435456;
        """;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS frecency(
            path TEXT PRIMARY KEY COLLATE NOCASE,
            visits INTEGER NOT NULL,
            last_visit INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS files(
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE COLLATE NOCASE,
            name TEXT NOT NULL,
            is_dir INTEGER NOT NULL,
            size INTEGER NOT NULL,
            modified INTEGER NOT NULL);

        CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
            name, content='files', content_rowid='id', tokenize='trigram');

        CREATE TRIGGER IF NOT EXISTS files_ai AFTER INSERT ON files BEGIN
            INSERT INTO files_fts(rowid, name) VALUES (new.id, new.name);
        END;
        CREATE TRIGGER IF NOT EXISTS files_ad AFTER DELETE ON files BEGIN
            INSERT INTO files_fts(files_fts, rowid, name) VALUES ('delete', old.id, old.name);
        END;
        CREATE TRIGGER IF NOT EXISTS files_au AFTER UPDATE OF name ON files BEGIN
            INSERT INTO files_fts(files_fts, rowid, name) VALUES ('delete', old.id, old.name);
            INSERT INTO files_fts(rowid, name) VALUES (new.id, new.name);
        END;

        CREATE TABLE IF NOT EXISTS net_hosts(
            host TEXT PRIMARY KEY COLLATE NOCASE,
            address TEXT,
            open_ports TEXT NOT NULL,
            last_seen INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS dir_sizes(
            path TEXT PRIMARY KEY COLLATE NOCASE,
            size INTEGER NOT NULL,
            file_count INTEGER NOT NULL,
            computed_at INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS dir_cache(
            parent TEXT NOT NULL COLLATE NOCASE,
            name TEXT NOT NULL COLLATE NOCASE,
            path TEXT NOT NULL,
            kind INTEGER NOT NULL,
            size INTEGER NOT NULL,
            modified INTEGER NOT NULL,
            description TEXT,
            PRIMARY KEY(parent, name));
        """;

    private readonly string _writeConnectionString;
    private readonly string _readConnectionString;
    private readonly Channel<IndexWriteCommand> _writes =
        Channel.CreateUnbounded<IndexWriteCommand>(new UnboundedChannelOptions { SingleReader = true });

    private SqliteConnection? _writer;
    private Task _writerLoop = Task.CompletedTask;

    private SqliteIndexService(string databasePath)
    {
        DatabasePath = databasePath;
        _writeConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        _readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>Opens (creating if needed) the database and starts the writer. Runs entirely off the caller's thread.</summary>
    public static Task<SqliteIndexService> OpenAsync(string databasePath, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var service = new SqliteIndexService(databasePath);
                service.Initialize();
                return service;
            },
            cancellationToken);

    // ---- writes (fire-and-forget, never block the caller) ----

    public void RecordVisit(string path) =>
        Enqueue(new RecordVisitCommand(path, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public void IndexEntries(IReadOnlyList<VfsEntry> entries) => Enqueue(new IndexEntriesCommand(entries));

    /// <summary>Removes <paramref name="path"/> and everything below it from the file index.</summary>
    public void RemoveFromIndex(string path) => Enqueue(new RemoveFromIndexCommand(path));

    public void UpsertHost(NetworkHostRecord host) => Enqueue(new UpsertHostCommand(host));

    public void SetDirectorySize(string path, long bytes, long files) =>
        Enqueue(new SetDirectorySizeCommand(path, bytes, files, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public void CacheListing(string parent, IReadOnlyList<VfsEntry> entries) =>
        Enqueue(new CacheListingCommand(parent, entries));

    /// <summary>Completes when every write queued so far has been committed.</summary>
    public Task FlushAsync()
    {
        var flush = new FlushCommand();
        Enqueue(flush);
        return flush.Completion.Task;
    }

    // ---- reads (concurrent) ----

    /// <summary>Most "frecent" locations: visit count decayed by days since the last visit.</summary>
    public Task<IReadOnlyList<FrecencyEntry>> GetFrecentAsync(string? filter, int limit, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<FrecencyEntry>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT path, visits, last_visit,
                           visits / (1.0 + (($now - last_visit) / 86400.0)) AS score
                    FROM frecency
                    WHERE $filter IS NULL OR path LIKE $filter ESCAPE '\'
                    ORDER BY score DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$filter",
                    string.IsNullOrWhiteSpace(filter) ? DBNull.Value : $"%{SqlLike.Escape(filter)}%");
                command.Parameters.AddWithValue("$limit", limit);

                var results = new List<FrecencyEntry>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new FrecencyEntry(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                        reader.GetDouble(3)));
                }

                return results;
            },
            cancellationToken);

    /// <summary>
    /// Substring search over indexed names. The trigram tokenizer serves <c>LIKE</c> from the index for terms of
    /// three or more characters; shorter terms fall back to a scan.
    /// </summary>
    public Task<IReadOnlyList<VfsEntry>> SearchFilesAsync(string term, int limit, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<VfsEntry>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT f.path, f.name, f.is_dir, f.size, f.modified
                    FROM files_fts
                    JOIN files f ON f.id = files_fts.rowid
                    WHERE files_fts.name LIKE $pattern ESCAPE '\'
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$pattern", $"%{SqlLike.Escape(term)}%");
                command.Parameters.AddWithValue("$limit", limit);

                var results = new List<VfsEntry>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new VfsEntry(
                        reader.GetString(1),
                        reader.GetString(0),
                        reader.GetInt32(2) != 0 ? VfsEntryKind.Directory : VfsEntryKind.File,
                        reader.GetInt64(3),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                        VfsEntryFlags.Cached));
                }

                return results;
            },
            cancellationToken);

    public Task<IReadOnlyList<NetworkHostRecord>> GetHostsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<NetworkHostRecord>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT host, address, open_ports, last_seen FROM net_hosts ORDER BY host;";

                var results = new List<NetworkHostRecord>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var ports = reader.GetString(2)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(int.Parse)
                        .ToArray();
                    results.Add(new NetworkHostRecord(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        ports,
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
                }

                return results;
            },
            cancellationToken);

    public Task<CachedDirectorySize?> GetDirectorySizeAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAsync<CachedDirectorySize?>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT size, file_count, computed_at FROM dir_sizes WHERE path = $path;";
                command.Parameters.AddWithValue("$path", path);

                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new CachedDirectorySize(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)))
                    : null;
            },
            cancellationToken);

    public Task<IReadOnlyList<VfsEntry>> GetCachedListingAsync(string parent, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<VfsEntry>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT name, path, kind, size, modified, description
                    FROM dir_cache WHERE parent = $parent ORDER BY name;
                    """;
                command.Parameters.AddWithValue("$parent", parent);

                var results = new List<VfsEntry>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new VfsEntry(
                        reader.GetString(0),
                        reader.GetString(1),
                        (VfsEntryKind)reader.GetInt32(2),
                        reader.GetInt64(3),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                        VfsEntryFlags.Cached,
                        reader.IsDBNull(5) ? null : reader.GetString(5)));
                }

                return results;
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        await _writerLoop.ConfigureAwait(false);

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }

        using var pooled = new SqliteConnection(_readConnectionString);
        SqliteConnection.ClearPool(pooled);
    }

    private void Initialize()
    {
        _writer = new SqliteConnection(_writeConnectionString);
        _writer.Open();
        using (var command = _writer.CreateCommand())
        {
            command.CommandText = Pragmas + Schema;
            command.ExecuteNonQuery();
        }

        _writerLoop = Task.Run(WriterLoopAsync);
    }

    private void Enqueue(IndexWriteCommand command)
    {
        if (!_writes.Writer.TryWrite(command) && command is FlushCommand flush)
        {
            flush.Completion.TrySetException(new ObjectDisposedException(nameof(SqliteIndexService)));
        }
    }

    private async Task WriterLoopAsync()
    {
        var connection = _writer!;
        var reader = _writes.Reader;
        var flushes = new List<FlushCommand>();

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            Exception? batchError = null;
            try
            {
                using var transaction = connection.BeginTransaction();
                var count = 0;
                while (count < MaxCommandsPerTransaction && reader.TryRead(out var command))
                {
                    count++;
                    if (command is FlushCommand flush)
                    {
                        flushes.Add(flush);
                        continue;
                    }

                    try
                    {
                        command.Execute(connection, transaction);
                    }
                    catch (SqliteException ex)
                    {
                        Trace.TraceError($"Index write {command.GetType().Name} failed: {ex.Message}");
                    }
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                batchError = ex;
                Trace.TraceError($"Index transaction failed: {ex}");
            }

            foreach (var flush in flushes)
            {
                if (batchError is null)
                {
                    flush.Completion.TrySetResult();
                }
                else
                {
                    flush.Completion.TrySetException(batchError);
                }
            }

            flushes.Clear();
        }
    }

    private Task<T> ReadAsync<T>(Func<SqliteConnection, T> query, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var connection = new SqliteConnection(_readConnectionString);
                connection.Open();
                return query(connection);
            },
            cancellationToken);
}
