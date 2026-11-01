using Microsoft.Data.Sqlite;
using Ridley.Core;
using Ridley.Storage;

namespace Ridley.Storage.Tests;

public sealed class TempDb : IDisposable
{
    public TempDb() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ridley-test-" + Guid.NewGuid().ToString("N") + ".db");
    public string Path { get; }
    public SqliteDatabase Database => new(Path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { Path, Path + "-wal", Path + "-shm", Path + "-journal" })
        {
            try { File.Delete(file); } catch (IOException) { /* best effort */ }
        }
    }
}

public class SettingsStoreTests
{
    [Fact]
    public async Task Empty_database_returns_defaults()
    {
        using var db = new TempDb();
        var loaded = await new SqliteSettingsStore(db.Database).LoadAsync(default);
        Assert.Equal(new UserSettings(), loaded);
    }

    [Fact]
    public async Task Settings_round_trip_across_store_instances()
    {
        using var db = new TempDb();
        var saved = new UserSettings { PushToTalkHotkey = "Ctrl+Shift+R", AlwaysOnTop = false, AllowScreenCapture = true, MaxRetriesPerStep = 4, LogLevel = "Debug" };
        await new SqliteSettingsStore(db.Database).SaveAsync(saved, default);
        var loaded = await new SqliteSettingsStore(db.Database).LoadAsync(default);
        Assert.Equal(saved, loaded);
    }

    [Fact]
    public async Task Saving_twice_overwrites_instead_of_duplicating()
    {
        using var db = new TempDb();
        var store = new SqliteSettingsStore(db.Database);
        await store.SaveAsync(new UserSettings { MaxRetriesPerStep = 1 }, default);
        await store.SaveAsync(new UserSettings { MaxRetriesPerStep = 3 }, default);
        Assert.Equal(3, (await store.LoadAsync(default)).MaxRetriesPerStep);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected_and_not_stored()
    {
        using var db = new TempDb();
        var store = new SqliteSettingsStore(db.Database);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new UserSettings { MaxRetriesPerStep = 99 }, default));
        Assert.Equal(new UserSettings(), await store.LoadAsync(default));
    }

    [Fact]
    public async Task Corrupt_stored_value_falls_back_to_defaults()
    {
        using var db = new TempDb();
        var database = db.Database;
        await using (var connection = await database.OpenAsync(default))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO kv_settings (key, value) VALUES ('user_settings', '{not json');";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(new UserSettings(), await new SqliteSettingsStore(database).LoadAsync(default));
    }

    [Fact]
    public async Task Cancelled_token_stops_the_operation()
    {
        using var db = new TempDb();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqliteSettingsStore(db.Database).LoadAsync(cts.Token));
    }

    [Fact]
    public async Task Migration_sets_schema_version_once()
    {
        using var db = new TempDb();
        await (await db.Database.OpenAsync(default)).DisposeAsync();
        await using var second = await db.Database.OpenAsync(default); // new instance, existing file: must not re-run
        await using var command = second.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }
}

public class SecretStoreTests
{
    [Fact]
    public async Task Secret_round_trips_and_is_not_stored_as_plaintext()
    {
        using var db = new TempDb();
        var store = new DpapiSecretStore(db.Database);
        const string name = "Test:Roundtrip" + "A1";
        const string secret = "sk-test-very-secret-value";
        await store.SetAsync(name, secret, default);

        Assert.Equal(secret, await store.GetAsync(name, default));

        await using var connection = await db.Database.OpenAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_blob FROM secrets;";
        var blob = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(blob));
    }

    [Fact]
    public async Task Missing_secret_returns_null_and_delete_removes_it()
    {
        using var db = new TempDb();
        var store = new DpapiSecretStore(db.Database);
        Assert.Null(await store.GetAsync("Test:Missing", default));
        await store.SetAsync("Test:ToDelete", "v", default);
        await store.DeleteAsync("Test:ToDelete", default);
        Assert.Null(await store.GetAsync("Test:ToDelete", default));
    }

    [Fact]
    public async Task Environment_variable_takes_priority()
    {
        using var db = new TempDb();
        var store = new DpapiSecretStore(db.Database);
        var variable = DpapiSecretStore.EnvironmentVariableName("Test:EnvWins");
        Assert.Equal("RIDLEY__Test__EnvWins", variable);
        await store.SetAsync("Test:EnvWins", "from-db", default);
        Environment.SetEnvironmentVariable(variable, "from-env");
        try
        {
            Assert.Equal("from-env", await store.GetAsync("Test:EnvWins", default));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
