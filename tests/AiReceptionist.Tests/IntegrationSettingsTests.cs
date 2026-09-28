using AiReceptionist.Web.Configuration;
using Microsoft.Extensions.Configuration;

namespace AiReceptionist.Tests;

public sealed class IntegrationSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "air-integrations-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Values_are_encrypted_at_rest_and_override_other_configuration()
    {
        var store = IntegrationSettingsStore.CreateBeside(Path.Combine(_dir, "receptionist.db"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "from-server", ["Speech:Region"] = "eastus" })
            .Add(new IntegrationSettingsSource(store))
            .Build();

        store.Save(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "sk-dashboard-secret", ["Email:Host"] = " smtp.gmail.com " });

        Assert.Equal("sk-dashboard-secret", config["OpenAI:ApiKey"]); // reloaded without a restart of the configuration
        Assert.Equal("smtp.gmail.com", config["Email:Host"]);
        Assert.Equal("eastus", config["Speech:Region"]);
        var raw = File.ReadAllBytes(Path.Combine(_dir, "integrations.db"));
        Assert.DoesNotContain("sk-dashboard-secret", System.Text.Encoding.UTF8.GetString(raw));

        // A second instance (after a restart) reads the same values with the persisted key ring.
        Assert.Equal("sk-dashboard-secret", IntegrationSettingsStore.CreateBeside(Path.Combine(_dir, "receptionist.db")).Load()["OpenAI:ApiKey"]);

        store.Save(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = null });
        Assert.Equal("from-server", config["OpenAI:ApiKey"]); // removed: falls back to the server setting
    }

    [Fact]
    public void Only_connection_settings_can_be_stored()
    {
        var store = IntegrationSettingsStore.CreateBeside(Path.Combine(_dir, "receptionist.db"));
        Assert.Throws<ArgumentException>(() => store.Save(new Dictionary<string, string?> { ["ConnectionStrings:Receptionist"] = "Data Source=/etc/x" }));
        Assert.Empty(store.Load());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
