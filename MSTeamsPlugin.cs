using LoupixDeck.Plugin.MSTeams.Commands;
using LoupixDeck.Plugin.MSTeams.Teams;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.MSTeams;

/// <summary>
/// Controls Microsoft Teams meetings through the UI Automation tree of the Teams desktop client.
/// Microsoft retired the local third-party app API (ws://localhost:8124) on 2026-06-30, so the
/// meeting toolbar itself is read and pressed instead.
/// </summary>
public sealed class MSTeamsPlugin : LoupixPlugin, IPluginSettingsPage, IPluginRequirements
{
    internal const string GroupName = "Microsoft Teams";
    private const string LeaveConfirmKey = "leaveConfirm";

    private IPluginHost? _host;
    private TeamsSession? _session;
    private readonly List<IPluginCommand> _commands = [];
    private bool _leaveConfirm = true;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "msteams",
        Name = GroupName,
        Version = new Version(1, 0, 0),
        SdkVersion = SdkInfo.Version,
        Author = "RadiatorTwo",
        Description = "Control Microsoft Teams meetings with live state.",
        Icon = LoadIcon()
    };

    /// <summary>The plugin icon (icon.png, embedded). Missing data only costs the icon.</summary>
    private static byte[]? LoadIcon()
    {
        using Stream? stream = typeof(MSTeamsPlugin).Assembly.GetManifestResourceStream("LoupixDeck.Plugin.MSTeams.icon.png");
        if (stream is null)
        {
            return null;
        }

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _leaveConfirm = host.Settings.Get(LeaveConfirmKey, true);

        TeamsSelectors selectors = TeamsSelectors.Load(host.Logger.Warn);
        host.Logger.Info($"Teams selectors loaded ({selectors.Origin})");

        _session = new TeamsSession(selectors, host.Logger);
        BuildCommands(_session);
        _session.StateChanged += OnStateChanged;
        ReportAvailability(false);
        _session.Start();
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = GroupName,
            Description = "Mic, camera, blur, reactions, raise hand, sharing and leave in Teams meetings",
            Icon = TeamsStates.GroupGlyph
        }
    ];

    public override void Shutdown()
    {
        if (_session is not null)
        {
            _session.StateChanged -= OnStateChanged;
            _session.Dispose();
            _session = null;
        }

        foreach (IDisposable disposable in _commands.OfType<IDisposable>())
        {
            disposable.Dispose();
        }
    }

    private void BuildCommands(TeamsSession session)
    {
        _commands.Add(new TeamsToggleCommand(
            new CommandDescriptor
            {
                CommandName = "MSTeams.ToggleMic",
                DisplayName = "Toggle Microphone",
                Group = GroupName,
                Icon = TeamsStates.MicrophoneGlyph,
                Description = "Mute or unmute yourself in the meeting",
                States = TeamsStates.Mic,
                ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
            },
            _ => session.ToggleMicAsync(),
            s => s.MicMuted switch { true => TeamsStates.Muted, false => TeamsStates.Unmuted, null => null },
            session));

        _commands.Add(new TeamsToggleCommand(
            new CommandDescriptor
            {
                CommandName = "MSTeams.ToggleCamera",
                DisplayName = "Toggle Camera",
                Group = GroupName,
                Icon = TeamsStates.VideoGlyph,
                Description = "Turn your camera on or off",
                States = TeamsStates.Camera,
                ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
            },
            _ => session.ToggleCameraAsync(),
            s => s.CameraOn switch { true => TeamsStates.On, false => TeamsStates.Off, null => null },
            session));

        _commands.Add(new TeamsToggleCommand(
            new CommandDescriptor
            {
                CommandName = "MSTeams.ToggleBlur",
                DisplayName = "Toggle Background Blur",
                Group = GroupName,
                Icon = TeamsStates.BlurGlyph,
                Description = "Switch between standard blur and no background effect",
                States = TeamsStates.Blur,
                ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
            },
            _ => session.ToggleBlurAsync(),
            s => s.BlurOn switch { true => TeamsStates.On, false => TeamsStates.Off, null => null },
            session));

        _commands.Add(new TeamsToggleCommand(
            new CommandDescriptor
            {
                CommandName = "MSTeams.ToggleHand",
                DisplayName = "Raise / Lower Hand",
                Group = GroupName,
                Icon = TeamsStates.HandGlyph,
                Description = "Raise or lower your hand",
                States = TeamsStates.Hand,
                ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
            },
            _ => session.ToggleHandAsync(),
            s => s.HandRaised switch { true => TeamsStates.Up, false => TeamsStates.Down, null => null },
            session));

        _commands.Add(new TeamsToggleCommand(
            new CommandDescriptor
            {
                CommandName = "MSTeams.ToggleShare",
                DisplayName = "Share Screen",
                Group = GroupName,
                Icon = TeamsStates.ShareGlyph,
                Description = "Share a screen, or stop sharing",
                ParameterTemplate = "({Monitor})",
                Parameters = [new CommandParameter("Monitor", typeof(int)) { DefaultValue = "1" }],
                States = TeamsStates.Share,
                ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
            },
            ctx => session.ToggleShareAsync(ParseMonitor(ctx)),
            s => s.Sharing switch { true => TeamsStates.Sharing, false => TeamsStates.Idle, null => null },
            session));

        _commands.Add(new TeamsReactionCommand("Like", "Like", TeamsStates.LikeGlyph, session));
        _commands.Add(new TeamsReactionCommand("Love", "Love", TeamsStates.LoveGlyph, session));
        _commands.Add(new TeamsReactionCommand("Applause", "Applause", TeamsStates.ApplauseGlyph, session));
        _commands.Add(new TeamsReactionCommand("Laugh", "Laugh", TeamsStates.LaughGlyph, session));
        _commands.Add(new TeamsReactionCommand("Wow", "Wow", TeamsStates.WowGlyph, session));

        _commands.Add(new TeamsLeaveCommand(session, () => _leaveConfirm));
    }

    private static int ParseMonitor(CommandContext ctx)
        => ctx.Parameters.Length > 0 && int.TryParse(ctx.Parameters[0], out int monitor) && monitor > 0 ? monitor : 1;

    private void OnStateChanged(TeamsState previous, TeamsState current)
    {
        if (previous.InMeeting != current.InMeeting)
        {
            ReportAvailability(current.InMeeting);
        }

        bool meetingChanged = previous.InMeeting != current.InMeeting;
        RefreshIf(meetingChanged || previous.MicMuted != current.MicMuted, "MSTeams.ToggleMic");
        RefreshIf(meetingChanged || previous.CameraOn != current.CameraOn, "MSTeams.ToggleCamera");
        RefreshIf(meetingChanged || previous.BlurOn != current.BlurOn, "MSTeams.ToggleBlur");
        RefreshIf(meetingChanged || previous.HandRaised != current.HandRaised, "MSTeams.ToggleHand");
        RefreshIf(meetingChanged || previous.Sharing != current.Sharing, "MSTeams.ToggleShare");
    }

    private void RefreshIf(bool changed, string commandName)
    {
        if (!changed || _host is null)
        {
            return;
        }

        try
        {
            _host.RefreshButtonStates(commandName);
        }
        catch (MissingMethodException)
        {
            // Host older than SDK 1.34: no per-button states.
        }
    }

    private void ReportAvailability(bool available)
    {
        try
        {
            _host?.SetAvailable(available);
        }
        catch (MissingMethodException)
        {
            // Host older than SDK 1.36: the buttons stay unmarked.
        }
    }

    // ---- IPluginSettingsPage ----

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema { get; } =
    [
        new PluginSettingDescriptor
        {
            Key = LeaveConfirmKey,
            Label = "Confirm leaving",
            Kind = PluginSettingKind.Toggle,
            DefaultValue = true,
            Description = "Leave Meeting needs a second press within 3 seconds."
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
    [
        new PluginSettingAction
        {
            Label = "Check Teams",
            Invoke = async () =>
            {
                if (_session is null || _host is null)
                {
                    return "Not initialized";
                }

                try
                {
                    (bool running, TeamsState state) = await _session.CheckAsync().ConfigureAwait(false);
                    if (!running)
                    {
                        return _host.Tr("Teams is not running");
                    }

                    if (!state.InMeeting)
                    {
                        return _host.Tr("Teams is running, no meeting found");
                    }

                    return string.Format(_host.Tr("Meeting found (mic: {0}, camera: {1})"),
                        _host.Tr(state.MicMuted == true ? "muted" : "on"),
                        _host.Tr(state.CameraOn == true ? "on" : "off"));
                }
                catch (Exception ex)
                {
                    return string.Format(_host.Tr("Failed: {0}"), ex.Message);
                }
            }
        }
    ];

    public void OnSettingsSaved()
    {
        if (_host is not null)
        {
            _leaveConfirm = _host.Settings.Get(LeaveConfirmKey, true);
        }
    }

    // ---- IPluginRequirements ----

    public IReadOnlyList<PluginRequirement> GetRequirements() =>
    [
        new PluginRequirement
        {
            Id = "ms-teams",
            Name = "Microsoft Teams (new)",
            IsMet = IsTeamsInstalled(),
            Message = "The new Microsoft Teams desktop app is not installed.",
            InstallHint = "Install Microsoft Teams from https://www.microsoft.com/microsoft-teams/download-app."
        }
    ];

    private static bool IsTeamsInstalled()
    {
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        return Directory.Exists(Path.Combine(packages, "MSTeams_8wekyb3d8bbwe"));
    }
}
