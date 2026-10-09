# 碧蓝档案启动器

一个面向《碧蓝档案》玩家的 Windows 桌面启动器。它把国际服、国服、日服的常用信息放在同一个界面里，用来查看卡池、总力战、活动和兑换码，并连接 MuMu 模拟器准备启动游戏。

## 直接下载

Windows 64 位用户可以直接下载已经打包好的程序：

[下载 BlueArchiveLauncher.exe](https://github.com/ccaixukun118-eng/BlueArchiveLauncher/releases/download/v1.0.0/BlueArchiveLauncher.exe)

下载后双击运行即可，不需要自己编译源码。也可以在页面右侧的 `Releases` 里打开 `碧蓝档案启动器 v1.0.0`，然后下载 `BlueArchiveLauncher.exe`。

## 能做什么

- 在国际服、国服、日服之间切换，每个服务器的数据和缓存分开保存。
- 查看当前卡池、总力战信息、活动公告和公开兑换码。
- 数据优先读取 Kivo Wiki 和 GameKee；网络不可用时回退到该服务器最后一次成功缓存。
- 自动查找已安装的 MuMu 模拟器。没有安装时，可以下载官方安装器，安装完成后继续连接。
- 启动 MuMu 的 0 号安卓实例，并通过 ADB 检查连接状态。
- 将运行日志保存到 `%LOCALAPPDATA%\BlueArchiveLauncher\logs`。

## 运行环境

- Windows 10 或 Windows 11，64 位。
- .NET 6 Desktop Runtime。项目同时保留了 .NET 8 WPF 的开发目标。
- 可选：MuMu 模拟器 12。未安装时由启动器引导下载官方安装包。

## 本地运行

```powershell
dotnet restore -p:TargetFramework=net6.0-windows
dotnet build -c Debug -f net6.0-windows
dotnet run -c Debug -f net6.0-windows
```

## 说明

启动器目前提供总览、数据查询和模拟器连接。角色数据库、总力战推荐、设置和诊断等独立页面仍在继续完善。MuMu 官方安装器没有静默安装参数，下载后需要在安装窗口中确认安装。
