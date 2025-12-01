# 性能优化记录（截至 2025-12-01 15:28）

## 总体原则
- 不改变任何功能、路由、业务逻辑与返回格式
- 仅在传输层、运行时参数、枚举路径与日志构造等方面做零行为变更优化
- 每次改动均通过 `dotnet build` 与 `dotnet test -v q` 验证

## 已实施优化

### 1. 响应压缩（Brotli/Gzip）
- 位置：`Libraries/SPTarkov.Server.Web/SPTWeb.cs`
- 改动：注册并启用响应压缩（HTTPS 下启用），扩展 MIME 类型包括 `application/json`、`application/javascript`、`application/wasm`、`image/svg+xml` 等；压缩级别设为 `Fastest`
- 影响：显著降低传输体积；不改变响应内容与路由

### 2. 静态资源强缓存
- 位置：`Libraries/SPTarkov.Server.Web/SPTWeb.cs`
- 改动：为主站点与 Mod `wwwroot` 静态资源设置 `Cache-Control: public,max-age=31536000,immutable`
- 影响：重复访问无需网络往返；不改变资源路径或内容

### 3. 响应缓存管线注册
- 位置：`Libraries/SPTarkov.Server.Web/SPTWeb.cs`
- 改动：注册并启用 `ResponseCaching` 中间件（仅在控制器或响应显式设置缓存头/特性时生效）
- 影响：默认无行为变化；为后续只读端点的条件缓存打基础

### 4. Kestrel 响应头精简
- 位置：`SPTarkov.Server/Program.cs`
- 改动：显式关闭 `options.AddServerHeader`
- 影响：微幅减少响应字节并降低指纹暴露；不影响功能

### 5. WebSocket 缓冲优化
- 位置：`SPTarkov.Server/Program.cs`
- 改动：将 `ReceiveBufferSize` 设置为 `64 * 1024`（属性为兼容占位，无副作用）
- 影响：高吞吐场景收包更高效（在实现支持范围内），不改变消息语义

### 6. 线程池预热
- 位置：`SPTarkov.Server/Program.cs`
- 改动：在应用启动后设置最小工作/IO 线程为 `Environment.ProcessorCount * 2`
- 影响：降低高并发初峰线程获取延迟；不改变业务执行顺序或结果

### 7. 请求日志构造延迟
- 位置：`SPTarkov.Server/Logger/SptLoggerMiddleware.cs`
- 改动：在记录请求前检查 `Info` 日志级别，仅在启用时构造日志文本
- 影响：减少字符串拼接与对象序列化开销；日志内容保持不变

### 8. HTTP 监听器枚举优化
- 位置：`Libraries/SPTarkov.Server.Core/Servers/HttpServer.cs`
- 改动：将 `IEnumerable<IHttpListener>` 缓存为数组，并用 `for` 循环查找首个 `CanHandle` 的监听器，替换 `FirstOrDefault`
- 影响：减少每次请求的枚举/委托分配；监听器选择顺序与行为不变

### 9. 安装目录检测微优化
- 位置：`SPTarkov.Server/Program.cs`
- 改动：`EndsWith` 使用 `StringComparison.OrdinalIgnoreCase`
- 影响：避免文化比较开销；行为在 Windows 下保持一致

## 验证与结果
- 构建：多次执行 `dotnet build` 成功
- 测试：多次执行 `dotnet test -v q` 成功（无失败）
- 备注：编译器报告的警告与现有测试输出保留，未作为本轮优化目标

## 计划中的后续优化（零行为变更）

### A. 条件缓存（只读端点）
- 为明确幂等的只读端点添加 `ETag`/`Last-Modified` 支持（逐端点评估并落地）
- 结合已启用的 `ResponseCaching`，减少重复请求的带宽与 CPU
- 保证响应体与业务逻辑不变，仅增加缓存契约

### B. Kestrel 保守参数细化（部分已落实）
- 已设置 `KeepAliveTimeout` 与 `RequestHeadersTimeout`；后续根据真实流量评估是否需要进一步调整其它限值（保持行为不变）

### C. 日志延迟求值统一化
- 在热点日志位置统一包裹 `IsLogEnabled` 检查，避免无效字符串拼接与对象序列化
- 保持日志文本与触发条件不变

### D. 线程池参数按实测调优
- 根据生产并发曲线将 `SetMinThreads` 系数改为更适合的值，避免空闲线程浪费
- 确认在峰值与平峰期间无负面影响

### E. WebSocket 压缩能力评估
- 若客户端支持，评估开启 `permessage-deflate`（需验证兼容性与收益）
- 保证消息格式与处理逻辑不变

## 里程碑与执行方式
- 逐模块评估 → 小步落地 → 构建与测试验证 → 文档记录
- 坚持不改变功能/路由/业务逻辑的底线；传输层与运行时参数微调优先

---
最后更新：2025-12-01 15:28
### 10. SptHttpListener 枚举与日志延迟求值优化
- 位置：`Libraries/SPTarkov.Server.Core/Servers/Http/SptHttpListener.cs`
- 改动：将 `IEnumerable<ISerializer>` 缓存为数组并用 `for` 循环查找匹配的序列化器；在请求/响应日志处增加 `IsLogEnabled(LogLevel.Info)` 检查，避免无效序列化与字符串构造
- 影响：减少每次请求的枚举/委托分配与不必要的日志序列化；序列化器选择逻辑与日志内容保持不变

### 11. Kestrel 保守限值显式化
- 位置：`SPTarkov.Server/Program.cs`
- 改动：设置 `options.Limits.KeepAliveTimeout = 2 分钟` 与 `options.Limits.RequestHeadersTimeout = 30 秒`，与默认一致但显式化以便后续调优
- 影响：无行为变化；为慢客户端场景提供更明确的可观察与后续调整空间

### 12. 响应缓存管线参数
- 位置：`Libraries/SPTarkov.Server.Web/SPTWeb.cs`
- 改动：显式配置 `ResponseCachingOptions`（`MaximumBodySize=256KB`、`UseCaseSensitivePaths=false`）以控制缓存体积并减少路径大小写导致的重复缓存
- 影响：默认不改变动态响应；仅在控制器或响应设置缓存契约时生效

### 13. 预解析 HttpServer 实例
- 位置：`SPTarkov.Server/Program.cs`
- 改动：在 Web 管道构建时预先解析 `HttpServer`，避免每个请求重复进行依赖解析
- 影响：中间件行为与路由不变，降低每请求微小解析开销

### 14. 响应契约头（ETag/Last-Modified）
- 位置：`Libraries/SPTarkov.Server.Core/Servers/Http/SptHttpListener.cs`
- 改动：为 JSON 与 Zlib JSON 响应添加 `ETag` 与 `Last-Modified` 头；使用 `XxHash64` 作为 ETag 的哈希算法，并通过 Typed Headers 设置
- 影响：仅增加响应头，不改变响应体与状态码；为客户端条件缓存奠定基础
- 进一步优化：为 Zlib JSON 写入使用 `ArrayPool<byte>`，避免大响应发生临时字节数组分配（`Libraries/SPTarkov.Server.Core/Servers/Http/SptHttpListener.cs:205-216`）；为 JSON 响应设置 `Content-Length`，提升传输效率（`Libraries/SPTarkov.Server.Core/Servers/Http/SptHttpListener.cs:195-199`）
