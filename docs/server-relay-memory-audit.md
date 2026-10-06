# Server / Relay 内存与资源生命周期排查

排查日期：2026-10-06。范围：ConnectX.Server、ConnectX.Relay、共享 ConnectX.Actors，以及当前 Hive.Framework TCP / Pipe 生命周期的调用方式。

## 结论与修复

| 问题 | 原因与影响 | 修复 |
| --- | --- | --- |
| 历史记录的 EF 实体长期保留 | RoomCreationRecordService、RoomJoinRecordService 在整个后台服务生命周期使用同一 DbContext。SaveChanges 后实体仍被跟踪，随历史写入持续增长。失败重试还会不断添加新实体。 | 每次数据库尝试创建独立 DI scope / DbContext，并通过 await using 释放。记录主键跨重试固定，并检查是否已落库，避免数据库已提交但确认失败时再次插入。 |
| 历史队列无容量限制 | 数据库故障或 ZeroTier 路径迟迟不可用时，ConcurrentQueue 持续接收事件；原失败分支还可能无延迟忙循环。 | 两个队列分别限制为 1024 条。数据库失败最多尝试三次、间隔五秒；Join 等待路径信息最多保留五分钟。停止/释放服务时关闭并清空队列。 |
| 空闲时限频缓存不清理 | RoomJoinRecordService 只在队列有数据时清理 _lastRefreshTimes，最后一批用户可能一直残留。原删除方式还可能移除并发更新后的时间。 | 在空闲轮询中同样清理五分钟前的缓存；按键与旧值比较删除，保留并发刷新。 |
| 重复挂接留下孤立索引 | GroupManager、P2PManager、RelayServerManager 的两个 TryAdd 通过短路表达式串联。第一个成功、第二个失败时没有回滚，第一次索引会遗留新用户或会话。 | 在同一个 Actor turn 内先检查两个键，再写入两个索引。拒绝重复挂接，不产生局部注册。 |
| 未配对 Relay Worker 长期存活 | Worker 已通过房间身份检查，但另一方向永不连接时，没有配对超时。借用缓冲区接收可能一直等待路由，并持有 Pipe read。 | 用单调时钟设置 30 秒未配对期限，由原有每秒清理循环关闭连接、解除接收回调、清空缓冲区并唤醒等待者。已配对 Worker 不套用该期限。 |
| 旧式 Worker 缓冲区仅限制条数 | 128 个帧的限制不等于字节限制，且原实现先复制再检查容量。 | 缓冲前检查最多 128 帧、合计 1 MiB；单帧超过 1 MiB 同样拒绝并关闭连接。当前 TCP 使用借用缓冲区路径，不增加转发复制。 |
| 失败发送队列与停机引用残留 | Outbox 发送失败/取消后尚未消费的委托仍可能持有报文和 byte[]，直到 Outbox 本身回收；Transport.Stop 后字典仍引用 Outbox。 | Outbox finally 清空未发送委托。Transport 停止时先快照等待任务，再清空活跃与退休集合并等待完成。 |
| 事件订阅缺少对应解除 | 管理器通过 ClientManager 的事件注册自身；当前正常运行是同生命周期单例，未发现按消息重复订阅，但提前释放管理器会被发布者引用。 | 源生成器提供可选 DisposeActorResources 钩子，六个管理器在 Dispose 中解除自身事件订阅。已有 Dispatcher handler 清理继续保留。 |

## 本轮检查中已有的保护

- Actor 邮箱容量 4096，网络入口 TryPost 拒绝过载；外部任务并发限制 32。
- 每会话发送队列容量 128，发送超时五秒；活跃会话观察任务和退休 Outbox 由监听器每秒清理。
- Server / Relay 的 ClientManager 每 500 ms 检查心跳或 TCP 断开并触发幂等 Detach；房间、Relay 地址、负载和 P2P 用户映射随断线清理。
- P2P 请求已有总量 4096 / 单用户 256 的限制及 30 秒过期处理。
- Relay 待握手映射已有总量 1024 / 五秒过期处理；Worker 替换和用户退出会移除旧回调与路由。
- Dispatcher 的单次响应处理在取消/完成时解除注册；每会话请求门使用 ConditionalWeakTable，不将历史会话作为强键永久保存。
- TCP 接收观察器在 StartAsync 结束后等待 Outbox 完成，再调用 session.Dispose；当前 Hive AbstractSession.Dispose 完成 Pipe 两端，释放 Pipe 池内存。借用缓冲区在接收回调完成前不允许提前归还。
- ZeroTier HTTP 请求/响应、临时 DI scopes、PeriodicTimer 和局部超时 CTS 已通过 using / await using 管理。

## 验证

执行方式：

```sh
dotnet test ConnectX.Tests/ConnectX.Tests.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tools/BorrowedBufferTests/BorrowedBufferTests.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
```

主测试集：24 项通过，包括原有真实 TCP 登录、房间、Relay datagram / Worker 转发、停机，以及本轮新增的 11 个测试用例：

- 连续 128 次创建历史写入，128 个独立 DbContext 全部释放，每次最多跟踪一个实体。
- 注入两次写入失败，第三次成功，仅生成一条历史，三个尝试的 DbContext 均释放。
- 4096 条创建事件入队时仅保留 1024 条，Dispose 后清空并拒绝新事件。
- 64 次 Join 写入的独立上下文释放；覆盖一次数据库失败重试，及空闲时过期缓存清理。
- 每个管理器重复挂接 128 次后断开会话，确认三类用户/会话索引清空。
- 取消阻塞发送后 Outbox 中委托清空。
- 未配对 Worker 的字节上限、30 秒期限、清理后输入回调移除及 Dispose 解除事件订阅。
- 关闭未配对的借用缓冲区 Worker 后，原未完成的 ReceiveAsync 正常返回，允许底层 Pipe 推进/释放。
- 256 个活跃/退休 Outbox 在 Transport 停止后集合清空。

借用缓冲区回归集：14 项全部通过，覆盖借用生命周期、取消、部分发送、真实 TCP、池所有权、队列重试与分配测量。

## 行为边界与后续观测

历史记录仍是进程内的尽力写入队列：队列满、达到数据库重试上限、Join 超期或进程停止时可能丢失尚未保存的记录。溢出、失败和过期均有日志；停止时主动释放剩余队列。若业务要求历史绝不丢失，需要另行设计磁盘队列或事务性 outbox，不能依靠无限增加内存队列。

本轮证明具体对象生命周期、容量限制和异常路径的回收，并未进行生产进程的长时间 heap dump / GC root 对比。因此不能据此保证生产所有工作负载都不存在泄漏。活跃连接和房间数量仍会影响总内存；数量上限也不等于对所有用户字符串的全局字节配额。正常 GC、JIT 和池预热后的 RSS 不立即下降，也不能单独判定为泄漏。

生产复核应在预热后重复相同的登录/退出、建房/解散、双向 Relay 连接/关闭周期，待心跳和过期清理完成，比较托管存活堆、连接数量、未配对 Worker 数量与历史积压；如果存活堆仍增长，再比较 heap dump 的对象数量与 GC roots，重点关注 RoomCreateHistory / RoomJoinHistory、TcpSession、RelayWorkerInput 和队列委托。

实现依据：[EF Core change tracking](https://learn.microsoft.com/en-us/ef/core/change-tracking/) 建议按短生命周期工作单元创建/释放 DbContext；[.NET Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels) 提供有界容量与显式过载处理机制。
