# 第三方组件声明

DeltaCrafter 直接使用以下 NuGet 软件包。其版权与许可证归各自权利人所有。

| 组件 | 版本 | 许可证 | 项目 |
|---|---:|---|---|
| Microsoft.WindowsAppSDK | 1.7.250606001 | Microsoft Windows App SDK License Terms | <https://github.com/microsoft/WindowsAppSDK> |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | <https://github.com/CommunityToolkit/dotnet> |
| H.NotifyIcon.WinUI | 2.3.0 | MIT | <https://github.com/HavenDV/H.NotifyIcon> |
| Serilog | 4.2.0 | Apache-2.0 | <https://github.com/serilog/serilog> |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 | <https://github.com/serilog/serilog-sinks-file> |
| System.Text.Encoding.CodePages | 8.0.0 | MIT | <https://github.com/dotnet/runtime> |

测试工程还使用：

| 组件 | 版本 | 许可证 |
|---|---:|---|
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |

本修复分支仅发布 ZIP，不使用或分发 Inno Setup 安装器及其语言文件。

自包含包还携带 .NET 8.0.32 运行时、部分 9.0.1 系列 .NET 库及 WebView2
1.0.2903.40 原生加载组件。对应官方许可和通知正文原样收录在 `licenses/`；
不包含 WebView2 浏览器运行时安装器。NuGet 的完整直接/传递依赖版本见各项目
`packages.lock.json`，测试依赖不进入运行包。

自包含发布目录中会包含 Windows App SDK 运行组件及其上游依赖。对应的许可证正文和上游声明位于发布包的 `licenses/` 目录；本文件不替代这些正文。
