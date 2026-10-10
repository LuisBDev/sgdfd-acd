using ACD.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ACD.Tests.Configuration;

public sealed class DocumentWorkspaceOptionsTests
{
    [Fact]
    public void Binds_from_Acd_DocumentWorkspace_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Acd:DocumentWorkspace:RootDirectory"] = @"C:\x",
                ["Acd:DocumentWorkspace:WatchDebounceMilliseconds"] = "250"
            })
            .Build();
        var services = new ServiceCollection();
        services.Configure<AcdOptions>(configuration.GetSection("Acd"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AcdOptions>>().Value.DocumentWorkspace;

        Assert.Equal(@"C:\x", options.RootDirectory);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.GetWatchDebounce());
    }

    [Fact]
    public void Watch_debounce_defaults_to_one_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), new DocumentWorkspaceOptions().GetWatchDebounce());
    }
}
