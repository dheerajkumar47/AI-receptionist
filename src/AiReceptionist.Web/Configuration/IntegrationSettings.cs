using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace AiReceptionist.Web.Configuration;

/// <summary>
/// Account settings (API keys, tokens, passwords) entered on the dashboard's Connections page. They are kept in their own
/// small SQLite file next to the main database, encrypted with ASP.NET Core Data Protection, and override
/// appsettings / user-secrets / Azure App Service settings. Only keys listed in <see cref="AllowedKeys"/> can be stored.
/// </summary>
public sealed class IntegrationSettingsStore
{
    private const string Purpose = "AiReceptionist.IntegrationSettings.v1";

    /// <summary>Everything the Connections page may set. Paths, database and hosting settings are deliberately excluded.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Admin:Username", "Admin:Password",
        "OpenAI:Provider", "OpenAI:Endpoint", "OpenAI:ApiKey", "OpenAI:Deployment",
        "Speech:Key", "Speech:Region", "Speech:RecognitionLanguage",
        "Microsoft365:SignInMode", "Microsoft365:TenantId", "Microsoft365:ClientId", "Microsoft365:ClientSecret", "Microsoft365:CalendarUser",
        "Email:Host", "Email:Port", "Email:Security", "Email:Username", "Email:Password", "Email:FromAddress", "Email:FromName", "Email:OwnerAddress",
        "Meta:AppSecret", "Meta:VerifyToken",
        "Meta:Facebook:PageAccessToken",
        "Meta:Instagram:AccessToken", "Meta:Instagram:ApiBaseUrl", "Meta:Instagram:AppSecret",
        "Meta:WhatsApp:AccessToken", "Meta:WhatsApp:PhoneNumberId", "Meta:WhatsApp:BusinessAccountId",
        "Twitter:ConsumerKey", "Twitter:ConsumerSecret", "Twitter:AccessToken", "Twitter:AccessTokenSecret",
    };

    private readonly string _connectionString;
    private readonly IDataProtector _protector;
    private readonly object _lock = new();

    public IntegrationSettingsStore(string databasePath, IDataProtectionProvider dataProtection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        _protector = dataProtection.CreateProtector(Purpose);
        Execute("CREATE TABLE IF NOT EXISTS IntegrationSettings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL, UpdatedUtc TEXT NOT NULL)");
    }

    /// <summary>Creates the store beside the main database, with its own key ring in a <c>keys</c> folder there.</summary>
    public static IntegrationSettingsStore CreateBeside(string mainDatabasePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(mainDatabasePath))!;
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "keys")),
            b => b.SetApplicationName("AiReceptionist"));
        return new IntegrationSettingsStore(Path.Combine(dir, "integrations.db"), protection);
    }

    public event Action? Changed;

    /// <summary>All stored values, decrypted. Values that can no longer be decrypted (lost key ring) are skipped.</summary>
    public IReadOnlyDictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Key, Value FROM IntegrationSettings";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try { result[reader.GetString(0)] = _protector.Unprotect(reader.GetString(1)); }
                catch (System.Security.Cryptography.CryptographicException) { /* re-enter it on the Connections page */ }
            }
        }
        return result;
    }

    /// <summary>Saves the given values; a null or blank value removes the key (falling back to server settings).</summary>
    public void Save(IReadOnlyDictionary<string, string?> changes)
    {
        foreach (var key in changes.Keys)
            if (!AllowedKeys.Contains(key)) throw new ArgumentException($"'{key}' can't be set from the dashboard.");

        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            foreach (var (key, value) in changes)
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                if (string.IsNullOrWhiteSpace(value))
                {
                    command.CommandText = "DELETE FROM IntegrationSettings WHERE Key = $key";
                }
                else
                {
                    command.CommandText = "INSERT INTO IntegrationSettings (Key, Value, UpdatedUtc) VALUES ($key, $value, $now) " +
                                          "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedUtc = excluded.UpdatedUtc";
                    command.Parameters.AddWithValue("$value", _protector.Protect(value.Trim()));
                    command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                }
                command.Parameters.AddWithValue("$key", key);
                command.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Changed?.Invoke();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>Makes the stored integration settings part of <see cref="IConfiguration"/> (added last, so they win).</summary>
public sealed class IntegrationSettingsSource(IntegrationSettingsStore store) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(store);

    private sealed class Provider : ConfigurationProvider
    {
        private readonly IntegrationSettingsStore _store;

        public Provider(IntegrationSettingsStore store)
        {
            _store = store;
            _store.Changed += () => { Load(); OnReload(); };
        }

        public override void Load() => Data = new Dictionary<string, string?>(
            _store.Load().Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Services read their settings once at start-up, so new connection settings take effect after a restart.
/// On Azure App Service the platform starts the app again automatically after it stops; locally it must be started again.
/// </summary>
public sealed class AppRestarter(IHostApplicationLifetime lifetime, ILogger<AppRestarter> log)
{
    public static bool CanRestartItself => Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is not null;

    public void RestartSoon()
    {
        if (!CanRestartItself) return;
        log.LogWarning("Connection settings changed; restarting to apply them.");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            lifetime.StopApplication();
        });
    }
}
