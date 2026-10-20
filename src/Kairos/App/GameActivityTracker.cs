namespace Kairos.App;

/// <summary>A retained handle identifies the original process even if Windows later reuses its PID.</summary>
internal interface IGameProcessSession : IDisposable
{
    int ProcessId { get; }
    string ExecutablePath { get; }
    bool IsAlive { get; }
}

/// <summary>Keeps replay active across focus changes for the lifetime of detected game processes.</summary>
internal sealed class GameActivityTracker(Func<int, IGameProcessSession?> openProcess, string? ownPath) : IDisposable
{
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(15);
    static readonly HashSet<string> ForegroundOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "iexplore",
        "explorer", "ApplicationFrameHost", "SystemSettings", "ShellExperienceHost", "SearchHost",
        "StartMenuExperienceHost", "LockApp", "WindowsTerminal", "OpenConsole", "cmd", "powershell", "pwsh",
        "vlc", "mpv", "wmplayer", "Video.UI", AppInfo.ExecutableName,
    };
    readonly Dictionary<int, IGameProcessSession> _sessions = [];
    readonly List<int> _exited = [];
    DateTime? _lastSeenUtc;
    bool _disposed;

    public bool GameActive { get; private set; }

    public bool Update(int? fullscreenProcessId, DateTime now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _exited.Clear();
        foreach (var (id, session) in _sessions)
            if (!session.IsAlive)
                _exited.Add(id);
        foreach (int id in _exited)
        {
            _sessions[id].Dispose();
            _sessions.Remove(id);
        }

        bool fullscreen = false;
        if (fullscreenProcessId is int pid && pid > 0)
        {
            if (_sessions.ContainsKey(pid))
                fullscreen = true;
            else
            {
                var session = openProcess(pid);
                // A protected or already-gone process keeps the previous bounded foreground/grace behavior.
                if (session == null)
                    fullscreen = true;
                else if (ForegroundApp.IsSameApp(session.ExecutablePath, ownPath))
                    session.Dispose();
                else
                {
                    fullscreen = true;
                    if (session.IsAlive && !ForegroundOnly.Contains(Path.GetFileNameWithoutExtension(session.ExecutablePath)))
                        _sessions.Add(pid, session);
                    else
                        session.Dispose();
                }
            }
        }
        if (fullscreen || _sessions.Count > 0)
            _lastSeenUtc = now;
        GameActive = fullscreen || _sessions.Count > 0 || _lastSeenUtc is { } seen && now - seen < ExitGrace;
        return GameActive;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        GameActive = false;
    }
}
