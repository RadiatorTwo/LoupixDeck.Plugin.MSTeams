using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.MSTeams.Teams;

/// <summary>
/// Element ids and localized label patterns of the Teams meeting UI, loaded from the embedded
/// <c>selectors.json</c>. A <c>selectors.json</c> next to the plugin DLL replaces the embedded
/// one, so a Teams update can be followed without a new plugin release.
/// </summary>
internal sealed class TeamsSelectors
{
    private const string ResourceName = "LoupixDeck.Plugin.MSTeams.selectors.json";
    private const string OverrideFileName = "selectors.json";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public required string MeetingProbeId { get; init; }
    public required string FullToolbarId { get; init; }
    public required ToggleSelector Mic { get; init; }
    public required ToggleSelector Camera { get; init; }
    public required ToggleSelector Hand { get; init; }
    public required ToggleSelector Share { get; init; }
    public required string HangupId { get; init; }
    public required string ReactionMenuId { get; init; }
    public required IReadOnlyDictionary<string, string> Reactions { get; init; }
    public required string VideoOptionsId { get; init; }
    public required PatternSet BlurOn { get; init; }
    public required PatternSet BlurOff { get; init; }
    public required PatternSet ShareWindowList { get; init; }

    /// <summary>Where the selectors came from, for the log.</summary>
    public string Origin { get; private init; } = "embedded";

    /// <summary>Loads the override file when present and valid, otherwise the embedded selectors.</summary>
    public static TeamsSelectors Load(Action<string> warn)
    {
        string? dir = Path.GetDirectoryName(typeof(TeamsSelectors).Assembly.Location);
        string? overridePath = dir is null ? null : Path.Combine(dir, OverrideFileName);
        if (overridePath is not null && File.Exists(overridePath))
        {
            try
            {
                using FileStream file = File.OpenRead(overridePath);
                return Parse(file, overridePath);
            }
            catch (Exception ex)
            {
                warn($"Ignoring invalid {overridePath}: {ex.Message}");
            }
        }

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return Parse(stream, "embedded");
    }

    private static TeamsSelectors Parse(Stream json, string origin)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = doc.RootElement;

        return new TeamsSelectors
        {
            MeetingProbeId = root.GetProperty("meetingProbeId").GetString()!,
            FullToolbarId = root.GetProperty("fullToolbarId").GetString()!,
            Mic = ToggleSelector.From(root.GetProperty("mic")),
            Camera = ToggleSelector.From(root.GetProperty("camera")),
            Hand = ToggleSelector.From(root.GetProperty("hand")),
            Share = ToggleSelector.From(root.GetProperty("share")),
            HangupId = root.GetProperty("hangupId").GetString()!,
            ReactionMenuId = root.GetProperty("reactionMenuId").GetString()!,
            Reactions = root.GetProperty("reactions").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase),
            VideoOptionsId = root.GetProperty("videoOptionsId").GetString()!,
            BlurOn = PatternSet.From(root.GetProperty("blurOnPatterns")),
            BlurOff = PatternSet.From(root.GetProperty("blurOffPatterns")),
            ShareWindowList = PatternSet.From(root.GetProperty("shareWindowListPatterns")),
            Origin = origin
        };
    }

    /// <summary>A list of case-insensitive patterns; matches when any of them does.</summary>
    internal sealed class PatternSet(IReadOnlyList<Regex> patterns)
    {
        public bool IsMatch(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (Regex regex in patterns)
            {
                try
                {
                    if (regex.IsMatch(text))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological pattern must not stall the worker; treat it as no match.
                }
            }

            return false;
        }

        public static PatternSet From(JsonElement array) => new(array.EnumerateArray()
            .Select(e => new Regex(e.GetString()!,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
            .ToList());
    }

    /// <summary>A toolbar toggle whose state is read from a localized label.</summary>
    internal sealed class ToggleSelector
    {
        public required string Id { get; init; }

        /// <summary>True when the state is in FullDescription rather than Name (raise hand).</summary>
        public required bool UsesDescription { get; init; }

        /// <summary>
        /// True when the button reports its active state as aria-expanded (raise hand: expanded
        /// while the hand is up). Language independent, so it wins over the label patterns.
        /// </summary>
        public bool ExpandedMeansActive { get; init; }

        public required PatternSet Active { get; init; }
        public required PatternSet Inactive { get; init; }

        /// <summary>True/false when the label matches an active/inactive pattern, null when neither does.</summary>
        public bool? Evaluate(string? label)
        {
            if (Active.IsMatch(label))
            {
                return true;
            }

            return Inactive.IsMatch(label) ? false : null;
        }

        public static ToggleSelector From(JsonElement e) => new()
        {
            Id = e.GetProperty("id").GetString()!,
            UsesDescription = string.Equals(e.GetProperty("source").GetString(), "FullDescription", StringComparison.OrdinalIgnoreCase),
            ExpandedMeansActive = e.TryGetProperty("expandedMeansActive", out JsonElement expanded) && expanded.GetBoolean(),
            Active = PatternSet.From(e.GetProperty("activePatterns")),
            Inactive = PatternSet.From(e.GetProperty("inactivePatterns"))
        };
    }
}
