using DeltaCrafter.Core.L3;
using Serilog;

namespace DeltaCrafter.App.Services;

public sealed class UpdateService
{
    private readonly ILogger _log;

    public UpdateService(AppHost host, UpdateCoordinator updates, ILogger log)
    {
        _log = log.ForContext<UpdateService>();
    }

    public Task CheckOnStartupAsync()
    {
        _log.Information("修复版已禁用自动覆盖更新，请手动下载修复版 Release。");
        return Task.CompletedTask;
    }

    public Task<string> CheckFromSettingsAsync() => Task.FromResult(
        "修复版已停用自动覆盖更新，请从 Shuang-Sec/DeltaCrafter-Fixed 的 Releases 手动下载。");
}
