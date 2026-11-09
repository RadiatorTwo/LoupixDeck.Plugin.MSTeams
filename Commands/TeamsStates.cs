using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.MSTeams.Commands;

/// <summary>
/// The button states the Teams toggle commands declare, and the layers each one starts with.
/// </summary>
/// <remarks>
/// State names are persisted in the button config and used to select the active state, so they
/// must not change once shipped. Index 0 is the resting state. The layouts are only the starting
/// point: the host creates them once when the command is put on a button.
/// </remarks>
internal static class TeamsStates
{
    // Microphone.
    public const string Unmuted = "Unmuted";
    public const string Muted = "Muted";

    // Camera, blur.
    public const string Off = "Off";
    public const string On = "On";

    // Raise hand.
    public const string Down = "Down";
    public const string Up = "Up";

    // Screen sharing.
    public const string Idle = "Idle";
    public const string Sharing = "Sharing";

    public const string GroupGlyph = "\U000F0849"; // mdi-account-group

    private const string Foreground = "#FFFFFF";
    private const string InactiveBackground = "#2B2F36";
    private const string AlertBackground = "#B3261E";
    private const string EnabledBackground = "#1F6E43";
    private const string HandBackground = "#8A6100";
    private const string TeamsBackground = "#464EB8";

    public const string MicrophoneGlyph = "\U000F036C"; // mdi-microphone
    private const string MicrophoneOffGlyph = "\U000F036D"; // mdi-microphone-off
    public const string VideoGlyph = "\U000F0567"; // mdi-video
    private const string VideoOffGlyph = "\U000F0568"; // mdi-video-off
    public const string BlurGlyph = "\U000F00B5"; // mdi-blur
    private const string BlurOffGlyph = "\U000F00B7"; // mdi-blur-off
    public const string HandGlyph = "\U000F0E47"; // mdi-hand-back-right
    private const string HandOutlineGlyph = "\U000F182D"; // mdi-hand-back-right-outline
    public const string ShareGlyph = "\U000F1483"; // mdi-monitor-share
    public const string HangupGlyph = "\U000F03F5"; // mdi-phone-hangup
    public const string LikeGlyph = "\U000F0513"; // mdi-thumb-up
    public const string LoveGlyph = "\U000F02D1"; // mdi-heart
    public const string ApplauseGlyph = "\U000F194B"; // mdi-hand-clap
    public const string LaughGlyph = "\U000F1214"; // mdi-emoticon-lol
    public const string WowGlyph = "\U000F0C70"; // mdi-emoticon-excited

    public static IReadOnlyList<ButtonStateDescriptor> Mic { get; } =
    [
        State(Unmuted, "Microphone on", MicrophoneGlyph, "Mic", InactiveBackground),
        State(Muted, "Microphone muted", MicrophoneOffGlyph, "Muted", AlertBackground)
    ];

    public static IReadOnlyList<ButtonStateDescriptor> Camera { get; } =
    [
        State(Off, "Camera off", VideoOffGlyph, "Camera", InactiveBackground),
        State(On, "Camera on", VideoGlyph, "Camera", EnabledBackground)
    ];

    public static IReadOnlyList<ButtonStateDescriptor> Blur { get; } =
    [
        State(Off, "No background effect", BlurOffGlyph, "Blur", InactiveBackground),
        State(On, "Background blurred", BlurGlyph, "Blur", EnabledBackground)
    ];

    public static IReadOnlyList<ButtonStateDescriptor> Hand { get; } =
    [
        State(Down, "Hand lowered", HandOutlineGlyph, "Hand", InactiveBackground),
        State(Up, "Hand raised", HandGlyph, "Hand up", HandBackground)
    ];

    public static IReadOnlyList<ButtonStateDescriptor> Share { get; } =
    [
        State(Idle, "Not sharing", ShareGlyph, "Share", InactiveBackground),
        State(Sharing, "Sharing the screen", ShareGlyph, "Sharing", AlertBackground)
    ];

    /// <summary>Look of a stateless action key (reactions, leave).</summary>
    public static ButtonLayoutDescriptor ActionLayout(string glyph, string caption, bool alert = false)
        => Layout(glyph, caption, alert ? AlertBackground : TeamsBackground);

    private static ButtonStateDescriptor State(string name, string description, string glyph, string caption, string background)
        => new()
        {
            Name = name,
            Description = description,
            Layout = Layout(glyph, caption, background)
        };

    /// <summary>An icon over a caption on a coloured background — the host's standard geometry.</summary>
    private static ButtonLayoutDescriptor Layout(string glyph, string caption, string background) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        BackgroundColor = background,
        Layers =
        [
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Symbol, Name = "Icon", Glyph = glyph,
                IconScale = 46 / 90.0, OffsetY = -8, Color = Foreground
            },
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Text, Name = "Caption", Text = caption, TextSize = 11,
                OffsetY = 27, BoxWidth = 88, BoxHeight = 22, Color = Foreground
            }
        ]
    };
}
