# TestS7CommPlusDriver.GUI

WinForms 示例程序，演示如何使用 S7CommPlusDriver 库动态浏览 PLC 符号并读写变量值。

要点：
- 窗口控件创建与事件订阅全部在 `Form1.Designer.cs`（WinForms 设计器规范）中完成
- 支持在线连接浏览与离线 TIA 工程加载（`LoadSymbolsFromFile`）
- 驱动日志经 `ControlDiagLogger`（ILogger 实现）转发到日志窗口

注意：本程序仅作演示用途，不应在生产环境中使用。
