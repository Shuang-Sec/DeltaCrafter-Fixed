using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1.Win32;

namespace DeltaCrafter.Core.L1;

public sealed record GameWindowInfo(nint Hwnd, string Title, string ClassName, int ProcessId);

/// <summary>屏幕物理像素矩形(客户区在屏幕上的位置与尺寸)。</summary>
public sealed record PixelRect(int Left, int Top, int Width, int Height);

/// <summary>窗口查找、客户区几何与前台化。所有坐标均为物理像素(应用为 PerMonitorV2)。</summary>
public sealed class GameWindowBrick
{
    public IReadOnlyList<GameWindowInfo> ListCandidates()
    {
        var result = new List<GameWindowInfo>();
        NativeWindowApi.EnumWindows((hwnd, _) =>
        {
            if (!NativeWindowApi.IsWindowVisible(hwnd)) return true;
            string title = ReadText(hwnd, isClass: false);
            if (title.Length == 0) return true;
            NativeWindowApi.GetWindowThreadProcessId(hwnd, out uint pid);
            result.Add(new GameWindowInfo(hwnd, title, ReadText(hwnd, isClass: true), (int)pid));
            return true;
        }, 0);
        return result;
    }

    /// <summary>
    /// 找游戏客户端窗口:规则匹配 且 客户区为 16:9 大窗。启动器与客户端同名,
    /// 但启动器是固定比例小窗(约 1.94:1),用比例即可区分,不猜窗口类名。
    /// 找不到返回 null(交由流程层决定走启动器或超时),不抛异常。
    /// </summary>
    public GameWindowInfo? FindGameClient(WindowMatchRule rule)
    {
        foreach (var w in ListCandidates())
        {
            if (!MatchesRule(w, rule)) continue;
            if (TryGetClient(w.Hwnd, out var rect) && rect.Width >= 960 && IsAspect16By9(rect))
                return w;
        }
        return null;
    }

    /// <summary>找启动器窗口:仅按标题包含匹配,并跳过 16:9 大窗(那是游戏客户端)。</summary>
    public GameWindowInfo? FindLauncher(string titleContains)
    {
        foreach (var w in ListCandidates())
        {
            if (!w.Title.Contains(titleContains, StringComparison.Ordinal)) continue;
            if (TryGetClient(w.Hwnd, out var rect) && rect.Width >= 960 && IsAspect16By9(rect))
                continue;
            return w;
        }
        return null;
    }

    internal static bool IsWeGameLauncherCandidate(string processName, string title) =>
        string.Equals(processName, "wegame", StringComparison.OrdinalIgnoreCase)
        && string.Equals(title.Trim(), "WeGame", StringComparison.OrdinalIgnoreCase);

    internal static bool IsWeGameBrowserCandidate(string processName, string title, string? actualPath, string configuredPath) =>
        string.Equals(processName,"browser",StringComparison.OrdinalIgnoreCase)
        && string.Equals(title.Trim(),"WeGame",StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(actualPath) && !string.IsNullOrEmpty(Path.GetDirectoryName(configuredPath))
        && string.Equals(actualPath,Path.Combine(Path.GetDirectoryName(configuredPath)!,"QBBlinkTrial","browser.exe"),StringComparison.OrdinalIgnoreCase);

    // 枚举仅采集快照；身份过滤和排序由生产、离线测试共用的纯函数完成。
    internal sealed record WeGameLauncherWindow(GameWindowInfo Window, string ProcessName,
        string? ProcessPath, PixelRect ClientRect, bool IsVisible);

    internal static GameWindowInfo? SelectWeGameLauncher(
        IEnumerable<WeGameLauncherWindow> candidates, string configuredPath)
    {
        // 可见登录窗优先于隐藏旧窗；同可见性优先真实browser，再比较同类面积。
        // 全部隐藏时仍保留候选，以便托盘恢复最终选中的主窗。
        return candidates.Where(candidate =>
                (IsWeGameLauncherCandidate(candidate.ProcessName, candidate.Window.Title)
                    || IsWeGameBrowserCandidate(candidate.ProcessName, candidate.Window.Title,
                        candidate.ProcessPath, configuredPath))
                && candidate.ClientRect.Width >= 960 && candidate.ClientRect.Height >= 500)
            .OrderByDescending(candidate => candidate.IsVisible)
            .ThenByDescending(candidate => IsWeGameBrowserCandidate(candidate.ProcessName,
                candidate.Window.Title, candidate.ProcessPath, configuredPath))
            .ThenByDescending(candidate => (long)candidate.ClientRect.Width * candidate.ClientRect.Height)
            .Select(candidate => candidate.Window)
            .FirstOrDefault();
    }

    /// <summary>按进程身份识别WeGame主窗；只还原最终选中窗，身份复核可关闭还原。</summary>
    internal GameWindowInfo? FindWeGameLauncher(string configuredPath, bool restore = true)
    {
        var candidates = new List<WeGameLauncherWindow>();
        NativeWindowApi.EnumWindows((hwnd, _) =>
        {
            string title = ReadText(hwnd, isClass: false);
            if (!string.Equals(title.Trim(), "WeGame", StringComparison.OrdinalIgnoreCase)) return true;
            NativeWindowApi.GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                if (!TryGetClient(hwnd, out var rect)) return true;
                string processName = process.ProcessName;
                string? processPath = string.Equals(processName, "browser", StringComparison.OrdinalIgnoreCase)
                    ? process.MainModule?.FileName : null;
                candidates.Add(new WeGameLauncherWindow(
                    new GameWindowInfo(hwnd, title, ReadText(hwnd, isClass: true), (int)pid),
                    processName, processPath, rect, NativeWindowApi.IsWindowVisible(hwnd)));
            }
            catch (ArgumentException) { } // 枚举期间进程已退出。
            catch (System.ComponentModel.Win32Exception) { } // 无权核对的其他进程不参与匹配。
            return true;
        }, 0);
        var best = SelectWeGameLauncher(candidates, configuredPath);
        if (restore && best is not null) NativeWindowApi.ShowWindow(best.Hwnd, NativeWindowApi.SW_RESTORE);
        return best;
    }

    /// <summary>把规则匹配且处于最小化的候选窗口还原。最小化窗口取不到客户区几何,
    /// FindGameClient 的 16:9 判定必然漏过它——「保持运行(最小化)」收尾后再次执行前
    /// 必须先还原。误还原了同名启动器也无碍:比例判定仍会把它排除。</summary>
    public bool TryRestoreMinimizedCandidate(WindowMatchRule rule)
    {
        bool restored = false;
        foreach (var w in ListCandidates())
        {
            if (!MatchesRule(w, rule) || !NativeWindowApi.IsIconic(w.Hwnd)) continue;
            NativeWindowApi.ShowWindow(w.Hwnd, NativeWindowApi.SW_RESTORE);
            restored = true;
        }
        return restored;
    }

    /// <summary>只读复核恢复后是否仍有匹配的最小化候选，不还原窗口。</summary>
    internal bool HasMinimizedCandidate(WindowMatchRule rule) =>
        ListCandidates().Any(window => MatchesRule(window, rule) && NativeWindowApi.IsIconic(window.Hwnd));

    private bool TryGetClient(nint hwnd, out PixelRect rect)
    {
        try
        {
            rect = ClientRectOnScreen(hwnd);
            return true;
        }
        catch (InvalidOperationException)
        {
            rect = new PixelRect(0, 0, 0, 0); // 窗口瞬时失效,按“取不到”处理并跳过该候选
            return false;
        }
    }

    private static bool MatchesRule(GameWindowInfo w, WindowMatchRule rule)
    {
        bool titleOk = !string.IsNullOrEmpty(rule.ExactTitle)
            ? string.Equals(w.Title, rule.ExactTitle, StringComparison.Ordinal)
            : w.Title.Contains(rule.TitleContains, StringComparison.Ordinal);
        bool classOk = string.IsNullOrEmpty(rule.ClassName)
            || string.Equals(w.ClassName, rule.ClassName, StringComparison.Ordinal);
        return titleOk && classOk;
    }

    /// <summary>客户区的屏幕矩形。窗口失效或尺寸为零时抛错——继续截图/点击毫无意义。</summary>
    public PixelRect ClientRectOnScreen(nint hwnd)
    {
        if (!NativeWindowApi.GetClientRect(hwnd, out RECT rc) || rc.Right <= 0 || rc.Bottom <= 0)
            throw new InvalidOperationException("无法取得游戏窗口客户区(窗口可能已关闭或最小化)。");
        var origin = new POINT { X = 0, Y = 0 };
        if (!NativeWindowApi.ClientToScreen(hwnd, ref origin))
            throw new InvalidOperationException("客户区坐标换算失败(窗口可能已关闭)。");
        return new PixelRect(origin.X, origin.Y, rc.Right, rc.Bottom);
    }

    /// <summary>
    /// 前台化并确认。Windows 限制后台进程抢前台,失败时用一次 ALT 键触发解除限制后重试;
    /// 仍失败返回 false,由调用方按步骤失败处理(截屏点击的前提就是前台且不被遮挡)。
    /// </summary>
    public bool TryEnsureForeground(nint hwnd, TimeSpan patience)
    {
        if (NativeWindowApi.IsIconic(hwnd))
        {
            NativeWindowApi.ShowWindow(hwnd, NativeWindowApi.SW_RESTORE);
            Thread.Sleep(400);
        }
        if (Attempt()) return true;

        NativeWindowApi.keybd_event(NativeWindowApi.VK_MENU, 0, 0, 0);
        NativeWindowApi.keybd_event(NativeWindowApi.VK_MENU, 0, NativeWindowApi.KEYEVENTF_KEYUP_LEGACY, 0);
        return Attempt();

        bool Attempt() => WaitForForeground(
            () => { NativeWindowApi.SetForegroundWindow(hwnd); },
            () => (IsForeground(hwnd), TryGetClient(hwnd, out _)),
            patience, () => Environment.TickCount64, Thread.Sleep);
    }

    // 实际激活等待边界可注入状态与时钟，离线回归不操作桌面。
    internal static bool WaitForForeground(Action activate,
        Func<(bool IsForeground, bool HasClientArea)> observe, TimeSpan patience,
        Func<long> now, Action<int> delay)
    {
        activate();
        var deadline = now() + (long)patience.TotalMilliseconds;
        while (now() < deadline)
        {
            if (IsReady()) return true;
            delay(100);
        }
        return IsReady();

        bool IsReady()
        {
            var state = observe();
            return state.IsForeground && state.HasClientArea;
        }
    }

    public void Minimize(nint hwnd) => NativeWindowApi.ShowWindow(hwnd, NativeWindowApi.SW_MINIMIZE);

    public bool IsAlive(nint hwnd) => NativeWindowApi.IsWindow(hwnd);

    /// <summary>只检查当前前台归属，不夺取焦点或发送按键。</summary>
    public bool IsForeground(nint hwnd) => NativeWindowApi.IsWindow(hwnd)
        && !NativeWindowApi.IsIconic(hwnd) && NativeWindowApi.GetForegroundWindow() == hwnd;

    /// <summary>16:9 断言(容差默认 2%)。锚点按 16:9 标定,其他比例直接拒绝而不是错位乱点。</summary>
    public static bool IsAspect16By9(PixelRect r, double tolerance = 0.02)
    {
        if (r.Width <= 0 || r.Height <= 0) return false;
        return Math.Abs((double)r.Width / r.Height - 16.0 / 9.0) <= tolerance;
    }

    private static string ReadText(nint hwnd, bool isClass)
    {
        var buf = new char[512];
        int len = isClass
            ? NativeWindowApi.GetClassName(hwnd, buf, buf.Length)
            : NativeWindowApi.GetWindowText(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }
}
