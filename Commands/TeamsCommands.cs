using LoupixDeck.Plugin.MSTeams.Teams;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.MSTeams.Commands;

/// <summary>
/// A Teams toggle with declared button states. The state shown is taken from the live meeting
/// state the <see cref="TeamsSession"/> reads; outside a meeting the button rests in state 0.
/// </summary>
internal sealed class TeamsToggleCommand(
    CommandDescriptor descriptor,
    Func<CommandContext, Task<bool>> toggle,
    Func<TeamsState, string?> stateOf,
    TeamsSession session) : IButtonStateCommand
{
    public CommandDescriptor Descriptor { get; } = descriptor;

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton | ButtonTargets.SimpleButton;

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            if (!await toggle(ctx).ConfigureAwait(false))
            {
                ctx.Host.Logger.Info($"{Descriptor.CommandName}: nothing to do (no Teams meeting or control not found)");
            }
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error($"{Descriptor.CommandName} failed", ex);
        }
    }

    public string? GetButtonState(CommandContext ctx)
    {
        TeamsState state = session.State;
        return state.InMeeting ? stateOf(state) : Descriptor.States[0].Name;
    }
}

/// <summary>Sends one meeting reaction.</summary>
internal sealed class TeamsReactionCommand(string reaction, string displayName, string glyph, TeamsSession session) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = $"MSTeams.React{reaction}",
        DisplayName = displayName,
        Group = MSTeamsPlugin.GroupName,
        Icon = glyph,
        Description = "Send a reaction in the current meeting",
        ButtonLayout = TeamsStates.ActionLayout(glyph, displayName)
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton | ButtonTargets.SimpleButton;

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            await session.ReactAsync(reaction).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error($"{Descriptor.CommandName} failed", ex);
        }
    }
}

/// <summary>
/// Leaves the meeting. With confirmation enabled (the default), the first press arms the command
/// — the button switches to its <see cref="TeamsStates.Confirm"/> state — and only a second press
/// within <see cref="ConfirmWindow"/> leaves. Without a second press the button falls back.
/// </summary>
internal sealed class TeamsLeaveCommand(TeamsSession session, Func<bool> confirmEnabled) : IButtonStateCommand, IDisposable
{
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private DateTime _armedUntil = DateTime.MinValue;
    private Timer? _disarmTimer;

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "MSTeams.Leave",
        DisplayName = "Leave Meeting",
        Group = MSTeamsPlugin.GroupName,
        Icon = TeamsStates.HangupGlyph,
        Description = "Hang up (press twice when confirmation is enabled)",
        States = TeamsStates.Leave,
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton | ButtonTargets.SimpleButton;

    public string? GetButtonState(CommandContext ctx) => IsArmed ? TeamsStates.Confirm : TeamsStates.Ready;

    private bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return DateTime.UtcNow <= _armedUntil;
            }
        }
    }

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            if (!session.State.InMeeting)
            {
                return;
            }

            if (confirmEnabled() && !ConsumeArm(ctx.Host))
            {
                RefreshState(ctx.Host);
                return;
            }

            RefreshState(ctx.Host);
            await session.LeaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error($"{Descriptor.CommandName} failed", ex);
        }
    }

    /// <summary>True when the command was armed (second press); otherwise arms it and returns false.</summary>
    private bool ConsumeArm(IPluginHost host)
    {
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            _disarmTimer?.Dispose();
            _disarmTimer = null;

            if (now <= _armedUntil)
            {
                _armedUntil = DateTime.MinValue;
                return true;
            }

            _armedUntil = now + ConfirmWindow;
            // Fall back to the ready state when no second press comes.
            _disarmTimer = new Timer(_ => RefreshState(host), null, ConfirmWindow + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
            return false;
        }
    }

    private void RefreshState(IPluginHost host)
    {
        try
        {
            host.RefreshButtonStates(Descriptor.CommandName);
        }
        catch (MissingMethodException)
        {
            // Host older than SDK 1.34: no per-button states.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disarmTimer?.Dispose();
            _disarmTimer = null;
        }
    }
}
