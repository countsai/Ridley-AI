using Microsoft.Extensions.DependencyInjection;
using Ridley.Core;

namespace Ridley.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddRidleyStorage(this IServiceCollection services, string? databasePath = null)
    {
        services.AddSingleton(new SqliteDatabase(databasePath ?? SqliteDatabase.DefaultPath));
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        return services;
    }
}
