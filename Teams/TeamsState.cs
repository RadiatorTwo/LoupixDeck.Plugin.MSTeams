namespace LoupixDeck.Plugin.MSTeams.Teams;

/// <summary>
/// Snapshot of the Teams meeting as read from its UI. A <c>null</c> flag means the label could
/// not be interpreted (unknown language, control missing).
/// </summary>
internal sealed record TeamsState
{
    public static TeamsState NoMeeting { get; } = new();

    public bool InMeeting { get; init; }
    public bool? MicMuted { get; init; }
    public bool? CameraOn { get; init; }
    public bool CameraAvailable { get; init; }
    public bool? HandRaised { get; init; }
    public bool? Sharing { get; init; }

    /// <summary>
    /// Last known background blur state. Teams only exposes it while the video options menu is
    /// open, so it is updated when the plugin toggles blur, not live.
    /// </summary>
    public bool? BlurOn { get; init; }
}
