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
/// and only a second press within <see cref="ConfirmWindow"/> leaves.
/// </summary>
internal sealed class TeamsLeaveCommand(TeamsSession session, Func<bool> confirmEnabled) : IPluginCommand
{
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private DateTime _armedUntil = DateTime.MinValue;

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "MSTeams.Leave",
        DisplayName = "Leave Meeting",
        Group = MSTeamsPlugin.GroupName,
        Icon = TeamsStates.HangupGlyph,
        Description = "Hang up (press twice when confirmation is enabled)",
        ButtonLayout = TeamsStates.ActionLayout(TeamsStates.HangupGlyph, "Leave", alert: true)
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton | ButtonTargets.SimpleButton;

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            if (!session.State.InMeeting)
            {
                return;
            }

            if (confirmEnabled() && !ConsumeArm())
            {
                if (ctx.Target == ButtonTargets.TouchButton && ctx.SourceIndex is int slot)
                {
                    ctx.Host.OverlayTouchText(slot, ctx.Host.Tr("Press again to leave"), ConfirmWindow);
                }

                return;
            }

            await session.LeaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error($"{Descriptor.CommandName} failed", ex);
        }
    }

    /// <summary>True when the command was armed (second press); otherwise arms it and returns false.</summary>
    private bool ConsumeArm()
    {
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            if (now <= _armedUntil)
            {
                _armedUntil = DateTime.MinValue;
                return true;
            }

            _armedUntil = now + ConfirmWindow;
            return false;
        }
    }
}
