using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.MSTeams;

public sealed class MSTeamsPlugin : LoupixPlugin
{
    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "msteams",
        Name = "MSTeams",
        Version = new Version(1, 0, 0),
        SdkVersion = SdkInfo.Version,
        Author = "",
        Description = ""
    };

    public override void Initialize(IPluginHost host)
    {
    }

    public override IEnumerable<IPluginCommand> GetCommands() => [];
}
