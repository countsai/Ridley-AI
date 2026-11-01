using System.Text.Json;
using Ridley.Core;

namespace Ridley.Storage;

public sealed class SqliteSettingsStore(SqliteDatabase database) : ISettingsStore
{
    private const string Key = "user_settings";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<UserSettings> LoadAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM kv_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", Key);
        var stored = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (stored is null)
        {
            return new UserSettings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<UserSettings>(stored, Json) ?? new UserSettings();
            // A hand-edited or corrupt row must not poison the app: fall back to defaults if invalid.
            return loaded.Validate().Count == 0 ? loaded : new UserSettings();
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken ct)
    {
        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException("Invalid settings: " + string.Join(" ", problems), nameof(settings));
        }

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO kv_settings (key, value) VALUES ($key, $value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", Key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings, Json));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
