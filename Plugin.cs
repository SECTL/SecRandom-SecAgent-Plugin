using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SecRandom.PluginSdk;

namespace SecRandom.SecAgentPlugin;

public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddHostedService<SecAgentHttpHostedService>();
        services.AddHostedService<SecAgentPluginBootstrapHostedService>();
    }
}
