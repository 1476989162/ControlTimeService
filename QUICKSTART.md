# 快速开始指南

## 编译项目

```bash
cd g:\ControlTimeService
dotnet build
```

## 运行客户端（被控端）

1. 进入客户端目录：
   ```bash
   cd ControlTimeService\bin\Debug\net8.0-windows
   ```

2. 运行程序：
   ```bash
   .\ControlTimeService.exe
   ```

3. （可选）如需连接控制端，在程序目录下创建 `control_config.json`：
   ```json
   {
     "server_url": "http://控制端IP地址:8080"
   }
   ```

## 运行控制端（管理端）

1. 进入控制端目录：
   ```bash
   cd ControlCenter\bin\Debug\net8.0-windows
   ```

2. 运行程序：
   ```bash
   .\ControlCenter.exe
   ```

3. 服务器会自动启动并监听 8080 端口

## 测试功能

### 1. 测试暂停功能
- 打开客户端
- 点击"暂停"按钮
- 应该立即进入锁屏状态

### 2. 测试配置功能
- 点击"时间配置"按钮
- 为每天设置不同的时间规则
- 保存后生效

### 3. 测试应用监控
- 打开微信小程序游戏
- 系统应该检测到并弹出警告
- 然后进入锁屏状态

### 4. 测试远程控制（需要配置控制端）
- 启动控制端
- 在另一台电脑上启动客户端并配置 control_config.json
- 控制端应该能看到客户端
- 右键点击客户端可以进行远程操作

## 常见问题

### Q: 编译时提示 System.Text.Json 有安全漏洞
A: 这只是警告，不影响使用。如需修复，可以升级到更新版本：
```bash
dotnet add package System.Text.Json --version 8.0.4
```

### Q: 控制端看不到客户端
A: 检查以下几点：
1. 确认 control_config.json 中的 IP 地址正确
2. 确认防火墙允许 8080 端口
3. 确认两台电脑在同一局域网内

### Q: 应用检测不准确
A: 可以在 AppMonitor.cs 中调整检测关键词列表

## 管理员密码

默认密码：`lnbxSoftLizhenNiping`

建议在正式使用时修改此密码（在 MainWindow.xaml.cs 中修改 `_adminPass` 变量）
