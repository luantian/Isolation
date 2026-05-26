# Isolation Leakage Management

智能安全壳隔离阀泄漏率数据管理软件。

## 技术选型

- .NET 8
- WPF
- MVVM: CommunityToolkit.Mvvm
- Database: SQL Server Express
- ORM: Entity Framework Core
- Charts: OxyPlot.Wpf
- Icons: MahApps.Metro.IconPacks
- Logging: Serilog
- Excel export: ClosedXML
- PDF report: QuestPDF
- Installer: Inno Setup

## 项目结构

```text
src/
  IsolationLeakage.App/        WPF 桌面客户端
doc/                           需求、规程、方案资料
tools/                         文档和辅助脚本
```

## 当前状态

已创建 WPF 项目骨架，后续将按工业主控软件风格完善界面、导航、数据模型和业务模块。
