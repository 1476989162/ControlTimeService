# Android客户端实施方案

## 项目概述

为现有的时间控制系统(ControlTimeService)添加Android移动端支持，实现：
- Android设备的时间使用控制
- 与控制端(WPF应用)的远程通信
- 应用使用监控和限制
- 统一的HTTP API接口

---

## 一、技术架构设计

### 1.1 整体架构

```
┌─────────────────┐         HTTP/JSON          ┌──────────────────┐
│  Android客户端   │ ◄──────────────────────►  │  Windows控制端    │
│  (Kotlin原生)    │      端口: 9528           │  (WPF/.NET 8)    │
└─────────────────┘                            └──────────────────┘
       │                                                │
       │ 本地存储(Room)                                 │ 注册表存储
       └────────────────────────────────────────────────┘
                    共享API协议
```

### 1.2 技术选型

**推荐方案: Kotlin原生开发**

理由:
- ✅ 性能最优，系统权限访问更直接
- ✅ Android官方推荐语言
- ✅ 与Windows服务端解耦，独立维护
- ✅ 便于后续扩展iOS版本(Flutter方案)

**核心技术栈:**
```gradle
dependencies {
    // HTTP通信
    implementation 'com.squareup.okhttp3:okhttp:4.12.0'
    implementation 'com.google.code.gson:gson:2.10.1'
    
    // 本地数据存储
    implementation 'androidx.room:room-runtime:2.6.0'
    implementation 'androidx.room:room-ktx:2.6.0'
    
    // 后台任务调度
    implementation 'androidx.work:work-runtime-ktx:2.9.0'
    
    // 通知服务
    implementation 'androidx.core:core-ktx:1.12.0'
    
    // 依赖注入(可选)
    implementation 'org.koin:koin-android:3.5.0'
}
```

---

## 二、核心功能模块

### 2.1 网络通信层(Network Layer)

**文件结构:**
```
com.controltime.android.network/
├── ApiService.kt              # API接口定义
├── ApiClient.kt               # OkHttp客户端封装
├── RequestModels.kt           # 请求数据模型
└── ResponseModels.kt          # 响应数据模型
```

**关键API映射:**

| 功能 | Windows API | Android实现 |
|------|------------|-------------|
| 设备注册 | `POST /api/clients/register` | 首次启动时调用 |
| 心跳保持 | `POST /api/clients/heartbeat` | WorkManager每30秒执行 |
| 获取命令 | `GET /api/clients/{id}/commands` | 轮询或长连接 |
| 状态上报 | `POST /api/clients/{id}/status` | 定时上报使用情况 |
| 配置更新 | `POST /api/clients/{id}/config` | 接收并保存配置 |

**代码示例 - ApiService:**
```kotlin
interface ApiService {
    @POST("api/clients/register")
    suspend fun register(@Body request: RegisterRequest): Response<Void>
    
    @POST("api/clients/heartbeat")
    suspend fun heartbeat(@Body request: HeartbeatRequest): Response<Void>
    
    @GET("api/clients/{clientId}/commands")
    suspend fun getCommands(@Path("clientId") clientId: String): Response<List<RemoteCommand>>
    
    @POST("api/clients/{clientId}/status")
    suspend fun updateStatus(
        @Path("clientId") clientId: String,
        @Body status: StatusUpdate
    ): Response<Void>
}
```

### 2.2 时间控制引擎(Time Control Engine)

**核心组件:**
```
com.controltime.android.timecontrol/
├── TimeController.kt          # 主控制器
├── UsageTracker.kt            # 使用时长追踪
├── LockScreenManager.kt       # 锁屏管理器
└── ScheduleChecker.kt         # 日程检查器
```

**功能实现:**

1. **使用时长统计**
   - 使用 `UsageStatsManager` 获取应用使用时长
   - 每分钟更新一次统计数据
   - 存储到Room数据库

2. **时间规则执行**
   ```kotlin
   class ScheduleChecker {
       fun checkCurrentSchedule(): ScheduleResult {
           val currentDay = DayOfWeek.now()
           val config = getConfigForDay(currentDay)
           
           return when {
               isLunchBreak(config) -> ScheduleResult.RESTING
               isEveningRestriction(config) -> ScheduleResult.LIMITED
               isNightShutdown(config) -> ScheduleResult.SHUTDOWN
               else -> ScheduleResult.USING
           }
       }
   }
   ```

3. **锁屏实现**
   - 使用 `DevicePolicyManager` 实现设备管理员锁屏
   - 需要用户授权设备管理员权限
   - 支持临时解锁(输入密码)

### 2.3 应用监控模块(App Monitor)

**挑战:** Android无法像Windows那样直接监控进程

**解决方案:**

1. **前台应用检测**
   ```kotlin
   val activityManager = getSystemService(ACTIVITY_SERVICE) as ActivityManager
   val foregroundApp = activityManager.runningAppProcesses.firstOrNull {
       it.importance == IMPORTANCE_FOREGROUND
   }?.processName
   ```

2. **使用统计查询**
   ```kotlin
   val usageStatsManager = getSystemService(USAGE_STATS_SERVICE) as UsageStatsManager
   val stats = usageStatsManager.queryUsageStats(
       INTERVAL_DAILY,
       startTime,
       endTime
   )
   ```

3. **AccessibilityService辅助监控**
   - 检测特定应用的窗口标题
   - 识别微信小程序游戏
   - 需要用户手动启用辅助功能

**监控目标:**
- 微信小游戏(通过包名和窗口标题)
- 常见游戏应用(王者荣耀、和平精英等)
- 短视频应用(抖音、快手)
- 小说应用(番茄小说)

### 2.4 后台服务层(Background Service)

**架构设计:**
```
com.controltime.android.service/
├── ControlTimeService.kt      # 前台服务
├── HeartbeatWorker.kt         # 心跳任务
├── CommandPollerWorker.kt     # 命令轮询
└── NotificationHelper.kt      # 通知管理
```

**保活策略:**

1. **前台服务(Foreground Service)**
   - 显示持续通知
   - 降低被系统杀死的概率
   - Android 10+ 需要特殊处理

2. **WorkManager定期任务**
   ```kotlin
   val heartbeatWork = PeriodicWorkRequest.Builder(
       HeartbeatWorker::class.java,
       30, TimeUnit.SECONDS
   ).setConstraints(
       Constraints.Builder()
           .setRequiredNetworkType(NetworkType.CONNECTED)
           .build()
   ).build()
   
   WorkManager.getInstance(context).enqueueUniquePeriodicWork(
       "heartbeat",
       ExistingPeriodicWorkPolicy.KEEP,
       heartbeatWork
   )
   ```

3. **电池优化白名单**
   - 引导用户将应用加入电池优化白名单
   - 不同品牌手机需要不同的设置方式

### 2.5 数据存储层(Data Layer)

**Room数据库设计:**
```kotlin
@Entity(tableName = "time_config")
data class TimeConfigEntity(
    @PrimaryKey val dayOfWeek: String,
    val useStartTime: String,
    val useEndTime: String,
    val restDurationMinutes: Int,
    val allowVideo: Boolean,
    val allowWeChatMiniGames: Boolean,
    // ... 其他配置字段
)

@Entity(tableName = "usage_record")
data class UsageRecordEntity(
    @PrimaryKey(autoGenerate = true) val id: Long,
    val packageName: String,
    val appName: String,
    val usageSeconds: Long,
    val date: String,
    val timestamp: Long
)

@Entity(tableName = "client_info")
data class ClientInfoEntity(
    @PrimaryKey val clientId: String,
    val deviceName: String,
    val serverUrl: String,
    val lastHeartbeat: Long
)
```

---

## 三、权限与安全

### 3.1 必需权限

```xml
<!-- AndroidManifest.xml -->
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.PACKAGE_USAGE_STATS" />
<uses-permission android:name="android.permission.SYSTEM_ALERT_WINDOW" />
<uses-permission android:name="android.permission.REQUEST_IGNORE_BATTERY_OPTIMIZATIONS" />
<uses-permission android:name="android.permission.RECEIVE_BOOT_COMPLETED" />
```

### 3.2 特殊权限申请

1. **设备管理员(Device Admin)**
   - 用于实现屏幕锁定
   - 需要创建 `DeviceAdminReceiver`

2. **无障碍服务(Accessibility Service)**
   - 用于应用窗口监控
   - 需要用户手动在设置中启用

3. **使用统计权限(Package Usage Stats)**
   - 引导用户跳转到设置页面授权
   ```kotlin
   val intent = Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS)
   startActivity(intent)
   ```

### 3.3 安全措施

- HTTPS通信(生产环境必须)
- 设备ID加密存储
- API请求签名验证(可选)
- 敏感数据不写入日志

---

## 四、UI界面设计

### 4.1 主要界面

**1. 主界面(MainActivity)**
```
┌─────────────────────────────┐
│  📱 设备名称                 │
│  ⏰ 剩余时间: 02:35:20       │
│  📊 今日已用: 3.5小时        │
│                             │
│  [暂停计时] [查看配置]       │
│                             │
│  状态: ● 正常使用            │
└─────────────────────────────┘
```

**2. 配置界面(ConfigActivity)**
- 显示当前周的时间规则
- 编辑每天的使用时段
- 应用权限开关

**3. 锁屏界面(LockScreenActivity)**
- 全屏覆盖
- 显示锁定原因
- 密码输入框
- 临时使用时长按钮

**4. 权限引导界面(PermissionGuideActivity)**
- 逐步引导用户授权
- 每个权限的说明
- 一键跳转设置

### 4.2 Material Design规范

- 使用Material 3设计规范
- 深色模式支持
- 响应式布局(适配平板)

---

## 五、实施阶段规划

### 阶段一: 基础框架搭建(预计2周)

**目标:** 实现基本的通信和注册功能

**任务清单:**
- [ ] 创建Android项目结构
- [ ] 配置Gradle依赖
- [ ] 实现OkHttp网络层
- [ ] 实现设备注册功能
- [ ] 实现心跳机制
- [ ] 创建Room数据库
- [ ] 基础UI框架

**交付物:**
- 可运行的Android应用
- 能成功注册到Windows控制端
- 心跳正常发送

### 阶段二: 时间控制核心(预计2周)

**目标:** 实现时间统计和锁屏功能

**任务清单:**
- [ ] 实现UsageStatsManager集成
- [ ] 使用时长统计逻辑
- [ ] DevicePolicyManager锁屏
- [ ] 时间规则解析和执行
- [ ] 锁屏界面开发
- [ ] 临时解锁功能

**交付物:**
- 能够统计应用使用时间
- 到达限制时间自动锁屏
- 支持密码解锁

### 阶段三: 远程控制功能(预计1周)

**目标:** 实现与服务端的命令交互

**任务清单:**
- [ ] 命令轮询机制
- [ ] 远程锁定/解锁
- [ ] 配置更新接收
- [ ] 状态上报
- [ ] 消息通知

**交付物:**
- 控制端可以远程锁定设备
- 配置可以实时同步
- 双向消息通信

### 阶段四: 应用监控(预计2周)

**目标:** 实现应用使用监控和限制

**任务清单:**
- [ ] AccessibilityService开发
- [ ] 微信小游戏检测
- [ ] 游戏应用识别
- [ ] 违规应用拦截
- [ ] 监控日志记录

**交付物:**
- 检测到违规应用自动锁屏
- 支持自定义黑名单
- 监控数据统计

### 阶段五: 优化与测试(预计1周)

**目标:** 完善用户体验和稳定性

**任务清单:**
- [ ] 后台保活优化
- [ ] 电池优化处理
- [ ] 不同品牌手机适配
- [ ] 性能优化
- [ ] UI美化
- [ ] 完整测试

**交付物:**
- 稳定的Release版本
- 完整的测试报告
- 用户使用文档

---

## 六、技术难点与解决方案

### 6.1 后台保活问题

**问题:** Android系统会杀死后台进程

**解决方案:**
1. 使用前台服务 + 持续通知
2. WorkManager定期重启服务
3. 引导用户关闭电池优化
4. 针对不同品牌做适配(小米、华为、OPPO等)

### 6.2 应用监控准确性

**问题:** Android无法精确监控所有应用

**解决方案:**
1. 结合UsageStatsManager和AccessibilityService
2. 建立应用特征库(包名、窗口标题关键词)
3. 定期更新监控规则
4. 提供手动添加黑名单功能

### 6.3 权限申请困难

**问题:** 用户可能拒绝授予敏感权限

**解决方案:**
1. 分步引导，解释每个权限的用途
2. 提供详细的图文教程
3. 部分功能降级运行(无权限时仅基础计时)
4. 使用教育性文案提高授权率

### 6.4 不同Android版本兼容

**问题:** Android 8-14行为差异大

**解决方案:**
1. 最低支持Android 8.0(API 26)
2. 针对不同版本做条件判断
3. 使用AndroidX兼容库
4. 充分测试各版本

---

## 七、项目结构

```
ControlTimeAndroid/
├── app/
│   ├── src/main/
│   │   ├── java/com/controltime/android/
│   │   │   ├── network/          # 网络层
│   │   │   ├── timecontrol/      # 时间控制
│   │   │   ├── appmonitor/       # 应用监控
│   │   │   ├── service/          # 后台服务
│   │   │   ├── data/             # 数据层
│   │   │   ├── ui/               # 界面层
│   │   │   ├── receiver/         # 广播接收器
│   │   │   └── util/             # 工具类
│   │   ├── res/                  # 资源文件
│   │   └── AndroidManifest.xml
│   └── build.gradle
├── gradle/
├── build.gradle
├── settings.gradle
└── README.md
```

---

## 八、测试计划

### 8.1 单元测试
- 网络请求Mock测试
- 时间计算逻辑测试
- 数据序列化测试

### 8.2 集成测试
- 与Windows控制端联调
- 端到端流程测试
- 异常场景测试

### 8.3 兼容性测试
- Android 8.0 - 14.0
- 主流品牌: 小米、华为、OPPO、vivo、三星
- 不同屏幕尺寸

### 8.4 性能测试
- 内存占用 < 100MB
- CPU占用 < 5%
- 电量消耗 < 5%/天
- 网络流量 < 10MB/天

---

## 九、风险评估

| 风险项 | 可能性 | 影响 | 缓解措施 |
|--------|--------|------|----------|
| Android权限限制 | 高 | 高 | 提前调研各版本权限政策 |
| 后台被杀 | 高 | 中 | 多重保活策略 |
| 应用监控不准 | 中 | 中 | 多方案结合 |
| 品牌适配复杂 | 高 | 中 | 优先主流品牌 |
| 用户接受度低 | 中 | 高 | 优化UX，简化配置 |

---

## 十、成功标准

### 技术指标
- ✅ 与控制端稳定通信(成功率 > 99%)
- ✅ 时间统计误差 < 1分钟/天
- ✅ 锁屏响应时间 < 2秒
- ✅ 应用检测准确率 > 90%

### 用户体验指标
- ✅ 首次配置时间 < 5分钟
- ✅ 日常使用无需干预
- ✅ 电池消耗可接受
- ✅ 界面友好易懂

### 业务指标
- ✅ 支持至少10台设备同时在线
- ✅ 7×24小时稳定运行
- ✅ 故障自动恢复

---

## 十一、后续扩展方向

1. **iOS版本**: 使用Flutter重构，同时支持iOS
2. **Web管理端**: 基于现有web_index.html增强
3. **数据分析**: 使用习惯分析报告
4. **家长控制**: 多子女差异化配置
5. **云端同步**: 配置云端备份
6. **智能推荐**: AI分析最佳使用时间

---

## 十二、资源需求

### 人力资源
- Android开发工程师: 1人(全职)
- UI设计师: 0.5人(兼职)
- 测试工程师: 0.5人(兼职)

### 时间投入
- 总工期: 8周(2个月)
- 每周工作: 40小时

### 硬件需求
- 测试设备: 5-10台不同品牌手机
- 开发机器: Mac/Windows均可

---

## 总结

本方案通过Kotlin原生开发Android客户端，复用现有HTTP API，实现了与Windows控制端的无缝集成。采用分阶段实施策略，从基础通信到高级监控，逐步完善功能。重点解决Android平台的后台保活、权限申请和应用监控等技术难点，确保最终产品的稳定性和可用性。

**核心价值:**
- 🎯 扩展现有系统的设备覆盖范围
- 🔄 保持API一致性，降低维护成本
- 📱 满足移动设备管理需求
- 🚀 为未来多平台扩展奠定基础
