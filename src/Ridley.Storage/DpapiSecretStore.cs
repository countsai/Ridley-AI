using System.Security.Cryptography;
using System.Text;
using Ridley.Core;

namespace Ridley.Storage;

/// <summary>
/// Secret lookup order: environment variable (RIDLEY__Section__Name) first, then a value the user stored in
/// Settings, encrypted with Windows DPAPI for the current user. Plaintext is never written to disk.
/// </summary>
public sealed class DpapiSecretStore(SqliteDatabase database) : ISecretStore
{
    private static readonly byte[] Entropy = "Ridley.SecretStore.v1"u8.ToArray();

    public static string EnvironmentVariableName(string name) =>
        "RIDLEY__" + name.Replace(":", "__", StringComparison.Ordinal);

    public async Task<string?> GetAsync(string name, CancellationToken ct)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName(name));
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_blob FROM secrets WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] blob)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null; // different Windows user or corrupt blob: treat as not set rather than crash
        }
    }

    public async Task SetAsync(string name, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(value);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO secrets (name, protected_blob) VALUES ($name, $blob) " +
            "ON CONFLICT(name) DO UPDATE SET protected_blob = excluded.protected_blob;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$blob", blob);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM secrets WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
