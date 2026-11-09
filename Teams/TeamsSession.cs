using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.MSTeams.Teams;

/// <summary>
/// Watches the Microsoft Teams desktop client through UI Automation and performs meeting actions.
/// </summary>
/// <remarks>
/// All UI Automation work runs on one dedicated background thread: the state poll and every
/// action are serialized there, so an action never races a poll and the host's threads never
/// block on a cross-process COM call. Actions are queued with <see cref="Enqueue{T}"/> and
/// awaited by the commands.
/// </remarks>
internal sealed class TeamsSession : IDisposable
{
    private const string TeamsProcessName = "ms-teams";
    private const string RenderWidgetClass = "Chrome_RenderWidgetHostHWND";

    private static readonly TimeSpan MeetingPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AccessibilityNudgeInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How long the meeting is kept alive while its toolbar is missing: an open flyout (share
    /// tray, reactions) removes the toolbar from the accessibility tree for a moment.
    /// </summary>
    private static readonly TimeSpan ToolbarGracePeriod = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan MenuTimeout = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(10);

    private readonly TeamsSelectors _selectors;
    private readonly IPluginLogger _logger;
    private readonly BlockingCollection<Action> _work = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    // Worker-thread state.
    private UIA3Automation? _automation;
    private nint _meetingWindow;
    private AutomationElement? _meetingRoot;
    private readonly Dictionary<string, AutomationElement> _cache = new(StringComparer.Ordinal);
    private DateTime _lastToolbarSeen = DateTime.MinValue;
    private DateTime _lastNudge = DateTime.MinValue;
    private DateTime _lastDiscovery = DateTime.MinValue;
    private DateTime _lastPollError = DateTime.MinValue;
    private bool? _blurOn;

    private TeamsState _state = TeamsState.NoMeeting;

    public TeamsSession(TeamsSelectors selectors, IPluginLogger logger)
    {
        _selectors = selectors;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "MSTeams UI Automation" };
        // UI Automation clients should not run in an STA that pumps no messages.
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Raised on the worker thread with (previous, current) when the meeting state changed.</summary>
    public event Action<TeamsState, TeamsState>? StateChanged;

    public TeamsState State => Volatile.Read(ref _state);

    public void Start() => _thread.Start();

    // ---- Actions (called from any thread) ----

    public Task<bool> ToggleMicAsync() => Enqueue(() => ClickToolbarButton(_selectors.Mic.Id));

    public Task<bool> ToggleCameraAsync() => Enqueue(() => ClickToolbarButton(_selectors.Camera.Id));

    public Task<bool> ToggleHandAsync() => Enqueue(ToggleHand);

    public Task<bool> LeaveAsync() => Enqueue(() => ClickToolbarButton(_selectors.HangupId));

    public Task<bool> ReactAsync(string reaction) => Enqueue(() => React(reaction));

    public Task<bool> ToggleBlurAsync() => Enqueue(ToggleBlur);

    /// <param name="monitor">1-based monitor index used when sharing starts.</param>
    public Task<bool> ToggleShareAsync(int monitor) => Enqueue(() => ToggleShare(monitor));

    /// <summary>Polls once and reports whether Teams runs and the resulting meeting state.</summary>
    public Task<(bool TeamsRunning, TeamsState State)> CheckAsync() => Enqueue(() =>
    {
        Poll();
        return (GetTeamsProcessIds().Count > 0, State);
    });

    private Task<T> Enqueue<T>(Func<T> action)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task.WaitAsync(ActionTimeout);
    }

    // ---- Worker loop ----

    private void Run()
    {
        try
        {
            _automation = new UIA3Automation();
            while (!_cts.IsCancellationRequested)
            {
                Poll();
                TimeSpan wait = State.InMeeting ? MeetingPollInterval : IdlePollInterval;
                if (_work.TryTake(out Action? item, (int)wait.TotalMilliseconds, _cts.Token))
                {
                    item(); // The next iteration polls right after the action.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.Error("Teams UI Automation worker stopped", ex);
        }
        finally
        {
            _automation?.Dispose();
        }
    }

    private void Poll()
    {
        try
        {
            PollCore();
        }
        catch (Exception ex)
        {
            // Teams closing mid-read throws COM errors; log at most once a minute.
            ForgetMeeting();
            if (DateTime.UtcNow - _lastPollError > TimeSpan.FromMinutes(1))
            {
                _lastPollError = DateTime.UtcNow;
                _logger.Warn($"Reading the Teams meeting state failed: {ex.Message}");
            }
        }
    }

    private void PollCore()
    {
        DateTime now = DateTime.UtcNow;
        HashSet<uint> pids = GetTeamsProcessIds();
        if (pids.Count == 0)
        {
            ForgetMeeting();
            _blurOn = null;
            Publish(TeamsState.NoMeeting);
            return;
        }

        List<nint> windows = NativeMethods.GetVisibleTopLevelWindows(pids);
        if (now - _lastNudge > AccessibilityNudgeInterval)
        {
            _lastNudge = now;
            foreach (nint window in windows)
            {
                RequestAccessibilityTree(window);
            }
        }

        if (_meetingWindow != 0 && !windows.Contains(_meetingWindow))
        {
            // The meeting window closed (or was hidden): the meeting is over.
            ForgetMeeting();
        }

        AutomationElement? mic = FindCached(_selectors.Mic.Id);

        // While sharing, Teams minimizes the meeting window and moves the toolbar into a compact
        // window; a minimized meeting window cannot be clicked, so look for a better one.
        bool meetingMinimized = _meetingWindow != 0 && NativeMethods.IsIconic(_meetingWindow);
        if ((mic is null || meetingMinimized) && now - _lastDiscovery > DiscoveryInterval)
        {
            _lastDiscovery = now;
            DiscoverMeetingWindow(windows);
            mic = FindCached(_selectors.Mic.Id);
        }

        if (mic is null)
        {
            if (State.InMeeting && _meetingWindow != 0 && now - _lastToolbarSeen < ToolbarGracePeriod)
            {
                return; // A flyout hides the toolbar; keep the last state.
            }

            ForgetMeeting();
            _blurOn = null;
            Publish(TeamsState.NoMeeting);
            return;
        }

        _lastToolbarSeen = now;
        AutomationElement? camera = FindCached(_selectors.Camera.Id);
        AutomationElement? hand = FindCached(_selectors.Hand.Id);
        AutomationElement? share = FindCached(_selectors.Share.Id);

        TeamsState previous = State;
        Publish(new TeamsState
        {
            InMeeting = true,
            MicMuted = ReadToggle(_selectors.Mic, mic) ?? previous.MicMuted,
            CameraOn = camera is null ? previous.CameraOn : ReadToggle(_selectors.Camera, camera),
            CameraAvailable = camera is not null && SafeIsEnabled(camera),
            HandRaised = hand is null ? previous.HandRaised : ReadToggle(_selectors.Hand, hand),
            Sharing = share is null ? previous.Sharing : ReadToggle(_selectors.Share, share),
            BlurOn = _blurOn
        });
    }

    private void Publish(TeamsState next)
    {
        TeamsState previous = Interlocked.Exchange(ref _state, next);
        if (previous == next)
        {
            return;
        }

        if (previous.InMeeting != next.InMeeting)
        {
            _logger.Info(next.InMeeting ? "Teams meeting detected" : "Teams meeting ended");
        }

        try
        {
            StateChanged?.Invoke(previous, next);
        }
        catch (Exception ex)
        {
            _logger.Error("Teams state change handler failed", ex);
        }
    }

    // ---- Meeting window discovery ----

    private static HashSet<uint> GetTeamsProcessIds()
    {
        HashSet<uint> pids = [];
        foreach (Process process in Process.GetProcessesByName(TeamsProcessName))
        {
            pids.Add((uint)process.Id);
            process.Dispose();
        }

        return pids;
    }

    private static void RequestAccessibilityTree(nint window)
    {
        NativeMethods.RequestAccessibility(window);
        foreach (nint child in NativeMethods.GetChildWindows(window))
        {
            NativeMethods.RequestAccessibility(child);
        }
    }

    /// <summary>
    /// Picks the Teams window that hosts the meeting toolbar. A window that is not minimized wins
    /// (it can be clicked without activating Teams); among those, the full toolbar wins over the
    /// compact meeting view.
    /// </summary>
    private void DiscoverMeetingWindow(List<nint> windows)
    {
        nint best = 0;
        int bestScore = -1;
        AutomationElement? bestRoot = null;
        AutomationElement? bestMic = null;

        foreach (nint window in windows)
        {
            AutomationElement root;
            AutomationElement? mic;
            try
            {
                root = _automation!.FromHandle(window);
                mic = root.FindFirstDescendant(cf => cf.ByAutomationId(_selectors.Mic.Id));
            }
            catch (Exception)
            {
                continue;
            }

            if (mic is null)
            {
                continue;
            }

            bool fullToolbar = root.FindFirstDescendant(cf => cf.ByAutomationId(_selectors.FullToolbarId)) is not null;
            int score = (NativeMethods.IsIconic(window) ? 0 : 2) + (fullToolbar ? 1 : 0);
            if (score > bestScore)
            {
                best = window;
                bestScore = score;
                bestRoot = root;
                bestMic = mic;
            }

            if (score == 3)
            {
                break;
            }
        }

        if (best == 0)
        {
            return;
        }

        if (best != _meetingWindow)
        {
            _cache.Clear();
        }

        _meetingWindow = best;
        _meetingRoot = bestRoot;
        _cache[_selectors.Mic.Id] = bestMic!;
    }

    private void ForgetMeeting()
    {
        _meetingWindow = 0;
        _meetingRoot = null;
        _cache.Clear();
    }

    /// <summary>
    /// Returns a toolbar element of the meeting window by AutomationId, from the cache when the
    /// cached element is still alive.
    /// </summary>
    private AutomationElement? FindCached(string automationId)
    {
        if (_meetingRoot is null)
        {
            return null;
        }

        if (_cache.TryGetValue(automationId, out AutomationElement? cached))
        {
            try
            {
                if (cached.Properties.AutomationId.ValueOrDefault == automationId)
                {
                    return cached;
                }
            }
            catch (Exception)
            {
                // Element is gone (re-rendered or removed); look it up again.
            }

            _cache.Remove(automationId);
        }

        AutomationElement? found;
        try
        {
            found = _meetingRoot.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch (Exception)
        {
            return null;
        }

        if (found is not null)
        {
            _cache[automationId] = found;
        }

        return found;
    }

    private static bool? ReadToggle(TeamsSelectors.ToggleSelector selector, AutomationElement element)
    {
        try
        {
            if (selector.ExpandedMeansActive
                && element.Patterns.ExpandCollapse.PatternOrDefault is { } expandCollapse)
            {
                return expandCollapse.ExpandCollapseState.Value == ExpandCollapseState.Expanded;
            }

            string? label = selector.UsesDescription
                ? element.Properties.FullDescription.ValueOrDefault
                : element.Name;
            if (selector.UsesDescription && string.IsNullOrEmpty(label))
            {
                label = element.Name;
            }

            return selector.Evaluate(label);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SafeIsEnabled(AutomationElement element)
    {
        try
        {
            return element.IsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- Actions (worker thread) ----

    private bool ClickToolbarButton(string automationId)
    {
        Poll();
        AutomationElement? button = FindCached(automationId);
        if (button is null)
        {
            _logger.Info($"Teams control '{automationId}' not found (no meeting?)");
            return false;
        }

        bool clicked = Click(button);
        Thread.Sleep(150);
        return clicked;
    }

    private bool ToggleHand()
    {
        Poll();
        AutomationElement? hand = FindCached(_selectors.Hand.Id);
        if (hand is null)
        {
            // Older layouts keep "raise hand" inside the reactions flyout.
            AutomationElement? menu = FindCached(_selectors.ReactionMenuId);
            if (menu is null || !Click(menu))
            {
                return false;
            }

            hand = WaitFor(() => FindInMeeting(_selectors.Hand.Id), MenuTimeout);
            if (hand is null)
            {
                CloseIfExpanded(menu);
                return false;
            }
        }

        bool clicked = Click(hand);
        Thread.Sleep(150);
        return clicked;
    }

    private bool React(string reaction)
    {
        if (!_selectors.Reactions.TryGetValue(reaction, out string? reactionId))
        {
            _logger.Warn($"Unknown Teams reaction '{reaction}'");
            return false;
        }

        Poll();
        AutomationElement? menu = FindCached(_selectors.ReactionMenuId);
        if (menu is null || !Click(menu))
        {
            return false;
        }

        AutomationElement? item = WaitFor(() => FindInMeeting(reactionId), MenuTimeout);
        if (item is null && FindInMeeting(_selectors.ReactionMenuId) is { } retryMenu && !IsExpanded(retryMenu))
        {
            // The first click can be swallowed (e.g. it only closed another popover): open again.
            Click(retryMenu);
            item = WaitFor(() => FindInMeeting(reactionId), MenuTimeout);
        }

        if (item is null)
        {
            _logger.Warn($"Teams reaction '{reactionId}' did not appear");
            CloseIfExpanded(FindInMeeting(_selectors.ReactionMenuId));
            return false;
        }

        bool clicked = Click(item);
        Thread.Sleep(300);
        CloseIfExpanded(FindInMeeting(_selectors.ReactionMenuId));
        return clicked;
    }

    private bool ToggleBlur()
    {
        Poll();
        AutomationElement? options = FindCached(_selectors.VideoOptionsId);
        if (options is null || !SafeIsEnabled(options) || !Click(options))
        {
            return false;
        }

        AutomationElement? blur = WaitFor(() => FindMenuCheckBox(_selectors.BlurOn), MenuTimeout);
        if (blur is null)
        {
            _logger.Warn("Teams background blur option not found in the video options");
            CloseIfExpanded(FindInMeeting(_selectors.VideoOptionsId));
            return false;
        }

        bool target = !IsChecked(blur);
        AutomationElement? choice = target ? blur : FindMenuCheckBox(_selectors.BlurOff);
        bool clicked = choice is not null && Click(choice);
        Thread.Sleep(300);
        if (clicked)
        {
            _blurOn = target;
        }

        CloseIfExpanded(FindInMeeting(_selectors.VideoOptionsId));
        return clicked;
    }

    private bool ToggleShare(int monitor)
    {
        Poll();
        AutomationElement? share = FindCached(_selectors.Share.Id);
        if (share is null)
        {
            return false;
        }

        if (ReadToggle(_selectors.Share, share) == true)
        {
            return Click(share); // Stop sharing.
        }

        HashSet<string> before = RuntimeIds(FindListItems());
        if (!Click(share))
        {
            return false;
        }

        List<AutomationElement>? screens = WaitFor(() =>
        {
            List<AutomationElement> items = FindListItems()
                .Where(e => !before.Contains(RuntimeId(e)) && !_selectors.ShareWindowList.IsMatch(SafeName(e)))
                .ToList();
            return items.Count > 0 ? items : null;
        }, MenuTimeout);

        if (screens is null)
        {
            _logger.Warn("No screen found in the Teams share tray");
            CloseIfExpanded(FindInMeeting(_selectors.Share.Id));
            return false;
        }

        AutomationElement screen = screens[Math.Clamp(monitor - 1, 0, screens.Count - 1)];
        _logger.Info($"Sharing screen '{SafeName(screen)}'");
        return Click(screen);
    }

    // ---- UI Automation helpers (worker thread) ----

    private AutomationElement? FindInMeeting(string automationId)
    {
        _cache.Remove(automationId);
        return FindCached(automationId);
    }

    private AutomationElement? FindMenuCheckBox(TeamsSelectors.PatternSet names)
    {
        if (_meetingRoot is null)
        {
            return null;
        }

        try
        {
            return _meetingRoot
                .FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                .FirstOrDefault(e => names.IsMatch(SafeName(e)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private AutomationElement[] FindListItems()
    {
        if (_meetingRoot is null)
        {
            return [];
        }

        try
        {
            return _meetingRoot.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
        }
        catch (Exception)
        {
            return [];
        }
    }

    private T? WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            T? result = probe();
            if (result is not null)
            {
                return result;
            }

            Thread.Sleep(80);
            if (_meetingWindow != 0)
            {
                RequestAccessibilityTree(_meetingWindow);
            }
        }

        return null;
    }

    private void CloseIfExpanded(AutomationElement? menuButton)
    {
        if (menuButton is not null && IsExpanded(menuButton))
        {
            Click(menuButton);
        }
    }

    private static bool IsExpanded(AutomationElement element)
    {
        try
        {
            return element.Patterns.ExpandCollapse.PatternOrDefault?.ExpandCollapseState.Value == ExpandCollapseState.Expanded;
        }
        catch (Exception)
        {
            return false; // Element already gone.
        }
    }

    private static bool IsChecked(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Toggle.PatternOrDefault is { } toggle)
            {
                return toggle.ToggleState.Value == ToggleState.On;
            }

            string aria = element.Properties.AriaProperties.ValueOrDefault ?? string.Empty;
            return aria.Contains("checked=true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Clicks an element without activating Teams: posts the mouse messages straight to the
    /// Chromium render widget under the element. Falls back to the Invoke pattern when the window
    /// is minimized or the element has no on-screen bounds (that path may bring Teams forward).
    /// </summary>
    private bool Click(AutomationElement element)
    {
        try
        {
            Rectangle bounds = element.BoundingRectangle;
            if (_meetingWindow != 0 && !NativeMethods.IsIconic(_meetingWindow) && bounds.Width > 0 && bounds.Height > 0)
            {
                NativeMethods.POINT point = new() { X = bounds.Left + (bounds.Width / 2), Y = bounds.Top + (bounds.Height / 2) };
                nint target = FindRenderWidgetAt(_meetingWindow, point);
                if (target != 0 && NativeMethods.ScreenToClient(target, ref point))
                {
                    nint lParam = NativeMethods.MakeLParam(point.X, point.Y);
                    NativeMethods.PostMessageW(target, NativeMethods.WM_MOUSEMOVE, 0, lParam);
                    NativeMethods.PostMessageW(target, NativeMethods.WM_LBUTTONDOWN, NativeMethods.MK_LBUTTON, lParam);
                    NativeMethods.PostMessageW(target, NativeMethods.WM_LBUTTONUP, 0, lParam);
                    return true;
                }
            }

            return InvokeElement(element);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Clicking a Teams control failed: {ex.Message}");
            return false;
        }
    }

    private static bool InvokeElement(AutomationElement element)
    {
        if (element.Patterns.Invoke.PatternOrDefault is { } invoke)
        {
            invoke.Invoke();
            return true;
        }

        if (element.Patterns.Toggle.PatternOrDefault is { } toggle)
        {
            toggle.Toggle();
            return true;
        }

        if (element.Patterns.SelectionItem.PatternOrDefault is { } selection)
        {
            selection.Select();
            return true;
        }

        if (element.Patterns.LegacyIAccessible.PatternOrDefault is { } legacy)
        {
            legacy.DoDefaultAction();
            return true;
        }

        return false;
    }

    private static nint FindRenderWidgetAt(nint window, NativeMethods.POINT screenPoint)
    {
        nint fallback = 0;
        foreach (nint child in NativeMethods.GetChildWindows(window))
        {
            if (NativeMethods.GetClassName(child) != RenderWidgetClass || !NativeMethods.IsWindowVisible(child))
            {
                continue;
            }

            fallback = fallback == 0 ? child : fallback;
            if (NativeMethods.GetWindowRect(child, out NativeMethods.RECT r)
                && screenPoint.X >= r.Left && screenPoint.X < r.Right
                && screenPoint.Y >= r.Top && screenPoint.Y < r.Bottom)
            {
                return child;
            }
        }

        return fallback;
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string RuntimeId(AutomationElement element)
    {
        try
        {
            return string.Join('.', element.Properties.RuntimeId.Value);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static HashSet<string> RuntimeIds(IEnumerable<AutomationElement> elements)
        => elements.Select(RuntimeId).ToHashSet(StringComparer.Ordinal);

    public void Dispose()
    {
        _cts.Cancel();
        _work.CompleteAdding();
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _cts.Dispose();
    }
}
