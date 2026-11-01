using Microsoft.Data.Sqlite;

namespace Ridley.Storage;

/// <summary>Owns the SQLite file location, connection creation and forward-only schema migrations.</summary>
public sealed class SqliteDatabase
{
    // Migration N is applied when PRAGMA user_version < N. Append only; never edit a shipped migration.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE kv_settings (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
        CREATE TABLE secrets (name TEXT PRIMARY KEY NOT NULL, protected_blob BLOB NOT NULL);
        """,
    ];

    private readonly string _connectionString;
    private int _initialised;

    public SqliteDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // lets tests delete the file; the volume here is tiny
        }.ToString();
    }

    public string DatabasePath { get; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ridley", "ridley.db");

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _initialised, 1) == 0)
        {
            await MigrateAsync(connection, ct).ConfigureAwait(false);
        }

        return connection;
    }

    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        long current;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            current = (long)(await read.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }

        for (var version = (int)current; version < Migrations.Length; version++)
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = Migrations[version] + $"\nPRAGMA user_version = {version + 1};";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }
}
