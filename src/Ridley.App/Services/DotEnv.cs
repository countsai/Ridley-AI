namespace Ridley.App.Services;

/// <summary>Development-only .env loader. Existing real environment variables always win.</summary>
public static class DotEnv
{
    public static int Load(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var applied = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            if (value.Length == 0 || Environment.GetEnvironmentVariable(name) is not null)
            {
                continue; // blank placeholders in .env.example must not shadow anything
            }

            Environment.SetEnvironmentVariable(name, value);
            applied++;
        }

        return applied;
    }
}
