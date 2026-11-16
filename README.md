# UDI Scanner Demo — MD6100S 扫码枪 C# WinForm MDI 示例

一个面向医疗器械 UDI（GS1）扫码场景的 C# WinForm MDI 示例项目。
主控类 `AutoComScannerManager` 负责扫码枪串口通信（USB 虚拟串口 / CDC-ACM）、数据分包、GS1 解析、线程安全状态与订阅分发；`MainMdiForm` / `ChildUdiForm` 演示 MDI 架构下串口"只开一次、窗体按需订阅"的正确用法。

> 本项目重点不在于"能扫出码"，而在于把 UDI 扫码在 WinForm 桌面端落地时最容易翻车的几个点（GS 分隔符、HID/VCP 差异、串口生命周期、跨线程回调、脏包）写成可复用的正确实现。

---

## 功能特性

- **MD6100S USB 虚拟串口（VCP）接入**：基于 `System.IO.Ports.SerialPort`，不依赖厂商私有 SDK。
- **串口全局单例化生命周期**：主窗体启动打开、关闭释放，子窗体切换不重复启停，规避 COM 端口占用冲突。
- **数据分包器与解析器职责分离**：以 `0x0D(CR)` 为帧结束符分包，解析器只接收单帧完整数据。
- **GS1 UDI 解析**：支持 AI `01`（GTIN/DI）、`11`（生产日期）、`17`（失效日期）、`10`（批号）、`21`（序列号），输出统一的 `(01)xxxx(11)xxxx...` 格式。
- **混合格式自动分流**：自动识别"厂家固件已格式化的括号文本"与"标准 GS1 裸流"，避免括号包再次进入裸流解析导致错位。
- **线程安全**：缓冲区锁、订阅集合锁分离；回调推送采用订阅者快照，避免并发修改异常。
- **订阅式分发 + 窗体激活自动订阅**：`Activated` 订阅、`Deactivate` 退订，实现"切换窗口扫码跟随当前窗口"。
- **状态机**：`Idle / Busy / Error`，供 UI 判断扫码枪是否就绪。

---

## 目录结构

```
UdiScannerDemo/
├── README.md
└── src/
    └── UdiScannerDemo/
        ├── UdiScannerDemo.csproj        # .NET Framework 4.8 WinForms 项目
        ├── Program.cs                   # 入口
        ├── AutoComScannerManager.cs     # 扫码核心：串口 + 分包 + 状态 + 订阅分发
        ├── Gs1UdiParser.cs              # 裸 GS1 流解析器（可独立复用，无 UI/串口依赖）
        ├── IScannerSubscriber.cs        # 订阅者接口 + ScannerStatus 枚举
        ├── MainMdiForm.cs               # MDI 主窗体：创建/启动/释放扫码管理器
        └── ChildUdiForm.cs              # MDI 子窗体：激活订阅、失活退订
```

源码已完整包含，用 VS 打开 `src/UdiScannerDemo/UdiScannerDemo.csproj` 即可编译运行；两个窗体均为纯代码构建，不依赖 `.designer.cs` 文件。

---

## 环境与依赖

| 项 | 要求 |
|---|---|
| 运行时 | .NET Framework 4.8（Win10/11 自带；Win7 需安装 4.8 运行时） |
| 开发工具 | Visual Studio 2019 / 2022 |
| 项目类型 | Windows 窗体应用（.NET Framework） |
| 程序集引用 | `System`（提供 `System.IO.Ports.SerialPort`）、`System.Windows.Forms`、`System.Drawing` |
| 硬件 | 民德 Mindeo MD6100S（或同规范 GS1 二维扫码枪） |
| 驱动 | 民德 USB CDC (VCP) 驱动；Win7 必须手动安装，Win10/11 多数可免驱 |

---

## 快速开始

### 1. 扫码枪切换到虚拟串口模式

用设备自带的编程手册，依次扫描：

1. `进入设置`
2. `USB Virtual COM Port`（切换为 VCP 模式）
3. `GS1 分隔符：输出（0x1D）`
4. `GS1 自动添加括号：关闭` ← **关键，关闭后输出才是标准裸流**
5. `后缀：CR（0x0D）` ← 本项目以 CR 作为分包结束符
6. `退出设置`

配置完成后，扫码枪重新上电。

> 若手册中找不到对应设置码，可使用民德上位机配置工具，以 VCP 方式连接设备后图形化修改。

### 2. 安装驱动并确认端口

设备管理器 → `端口 (COM 和 LPT)` → 出现 `Mindeo CDC` 对应的 `COMx`。
若显示为带黄色感叹号的未知设备，说明驱动未安装，此时不会生成 COM 口。

串口参数固定：`9600, 8, N, 1`（MD6100S VCP 默认值）。

### 3. 修改端口号并运行

`MainMdiForm.cs`：

```csharp
private void MainMdiForm_Load(object sender, EventArgs e)
{
    ScannerManager.PortName = "COM3";   // 改成设备管理器中的实际端口
    ScannerManager.StartScan();
}

private void MainMdiForm_FormClosing(object sender, FormClosingEventArgs e)
{
    ScannerManager.StopScan();
}
```

### 4. 打开子窗体测试

```csharp
var frm = new ChildUdiForm(ScannerManager);
frm.MdiParent = this;
frm.Show();
```

扫码后，`(01)...(11)...(17)...(10)...(21)...` 会写入当前激活子窗体的文本框，同时显示原始十六进制报文。

---

## 核心类说明

### AutoComScannerManager

| 成员 | 说明 |
|---|---|
| `PortName / BaudRate / DataBits / Parity / StopBits` | 串口参数，`StartScan` 前赋值 |
| `StartScan()` | 打开串口并启动接收。**仅允许主窗体调用**，已打开时直接返回 `true` |
| `StopScan()` | 解绑事件、关闭并释放串口、清空缓冲区。**仅允许主窗体调用** |
| `Subscribe(IScannerSubscriber)` | 注册扫码结果接收者，可重复调用（内部为 `HashSet`，天然去重） |
| `Unsubscribe(IScannerSubscriber)` | 注销接收者，`-=` 语义等价，不存在多播委托重复绑定问题 |
| `Status` | `Idle`（可扫码）/ `Busy`（刚出结果，防重入）/ `Error`（串口异常） |

### IScannerSubscriber

```csharp
public interface IScannerSubscriber
{
    void OnScanReceived(string gs1Formatted, string rawHex);
    void OnScannerError(string msg);
}
```

- `gs1Formatted`：统一格式化文本，如 `(01)06949862515502(11)251210(17)281209(10)JDYL10A(21)JD00003371740`
- `rawHex`：原始报文十六进制，用于审计、问题复盘、跨模式比对

**回调在串口接收线程（线程池线程）中触发**，实现方必须自行 `Invoke` 回到 UI 线程。

### 数据流

```
扫码枪
  │  USB CDC 字节流
  ▼
SerialPort.DataReceived        ← 线程池线程
  │  追加到 _buffer（加锁）
  ▼
TryParseGs1Packet()            ← 分包器：按 0x0D 切出单帧
  │
  ├─ 包内含 '(' 与 ')'  → 清理 0x0A / 0x1D → 直接作为格式化文本
  └─ 否则               → Gs1UdiParser.Format() → 组装 (AI)value 串
  │
  ▼
NotifyScanReceived()           ← 订阅者快照遍历
  │
  ▼
ChildUdiForm.OnScanReceived()  ← Invoke 回 UI 线程
```

---

## GS1 解析规则（本示例实现的 AI 子集）

| AI | 含义 | 长度 | 终止方式 |
|---|---|---|---|
| `01` | GTIN / UDI-DI | 固定 14 位 | 定长 |
| `11` | 生产日期 YYMMDD | 固定 6 位 | 定长 |
| `17` | 失效日期 YYMMDD | 固定 6 位 | 定长 |
| `10` | 批号 | 变长 1~20 | `0x1D`（GS）或串尾 |
| `21` | 序列号 | 变长 1~20 | `0x1D`（GS）或串尾 |

规则要点：

1. **只有变长 AI 需要 `0x1D` 作为终止符**，定长 AI 之间不插 GS。因此一条常见 UDI 码里可能只有一个 GS（位于 `(10)` 之后），也可能有两个（`(10)` 与 `(21)` 之后各一个）。
2. 解析必须**按 AI 驱动顺序读取**，不能按固定偏移截取。字段顺序、字段是否出现由条码印刷内容决定，不同厂家不同；缺少 `(11)` 是合法的。
3. 批号允许字母与符号，不是纯数字。
4. `0x1D` 本身不是 AI 前缀，只是分隔符，解析时应当消费掉，不能当作数据字符参与拼装。

---

## 关键设计决策与踩坑记录

这些是项目在真实环境中验证过的结论，也是本示例存在的主要价值。

### 1. HID 键盘模式拿不到 GS（0x1D）

USB HID 键盘仿真（键盘楔入、往光标处打字）经过 Windows 键盘输入栈，`0x1D` 这类控制字符会被丢弃或被固件改写成固定格式，应用层无法取得原始 GS1 字节流。
USB HID 还有另一种 **Raw HID 数据接口模式**，走 HID 数据端点可以拿到完整字节，但需要引入 `HidSharp` 等库、自行处理报告分包与断线重连，开发成本明显高于 `SerialPort`。
因此对于需要标准 GS1 解析的场景，VCP 是更合理的取舍。

### 2. VCP 模式也可能吐出"带括号脏包"

部分固件在 VCP 模式下仍会开启"GS1 自动添加括号"，输出形如：

```
0A 28 30 31 29 30 36 39 ... 1D 28 32 31 29 ...
```

即 **前置 0x0A + `(01)` 括号文本 + 中间夹杂 0x1D**。
这种数据若直接送入裸流解析器，表现为首字符不是 `(01)`、`(21)` 被拆成 `((2)1)` 等错位。
本项目处理方式：**在字节层面检测包内是否同时存在 `0x28`、`0x29`，命中则清理 `0x0A`/`0x1D` 后直接输出，不再解析**。
根本解决方式仍是关闭枪端"自动括号"设置。

> 不要用 `packetStr.StartsWith("(01)")` 作为判据：前置控制字符、`Trim()` 对控制字符的处理差异都会导致误判。

### 3. 串口不能由子窗体反复启停

COM 端口为独占资源。若每个子窗体都调用 `StartScan()`，切窗时会出现"忙碌/不可扫码"以及端口无法释放。
本项目约束：**`StartScan` / `StopScan` 只在主窗体 `Load` / `FormClosing` 调用**；子窗体只做订阅与退订。

### 4. 串口关闭必须在 UI 线程，且先解绑事件

在 `DataReceived` 回调内部直接 `Close()` 容易触发死锁或抛出异常。`SafeClosePort()` 的顺序固定为：

```
解绑 DataReceived → Close() → Dispose() → 置 null
```

### 5. 订阅集合代替多播委托

`event += handler` 对同一实例重复挂载会重复执行，且解绑依赖委托实例相等性，lambda 场景无法可靠移除。
本项目改用 `HashSet<IScannerSubscriber>`，`Subscribe` / `Unsubscribe` 幂等，推送前取快照，避免遍历过程中集合被修改。

### 6. 锁分离

- `_bufferLock`：缓冲区、状态、串口启停、分包解析
- `_subscriberLock`：订阅集合

两把锁互不嵌套，串口高频回调不会阻塞订阅注册。

### 7. 状态机用于防抖

`Busy` 状态在出结果后保持 300ms（`_idleTimer`），期间可拦截重复触发；超时回落到 `Idle`。业务层如需更严格的去重（同一码短时间重复扫），应在订阅者侧自行按 `rawHex` + 时间窗实现。

---

## 常见问题

**Q：`StartScan()` 没有抛异常，是否代表扫码枪就绪？**
不是。无异常只说明串口句柄打开成功。就绪应以 `Status == Idle` 判断；要确认枪端真的在通信，需要实际扫码验证。

**Q：扫码没有反应，`_buffer` 一直不触发解析？**
先确认枪端后缀是否为 CR。若设备无法输出 CR，可改为超时分包：收到数据后启动/重置一个 ~80ms 定时器，超时即认为一帧结束。

**Q：Win7 上扫码出现乱序、换行、丢字符？**
该现象主要出现在 **HID 键盘模式**。成因是 Win7 的 HID 输入栈缓冲与调度能力弱于 Win10/11，长报文（UDI 常 50~80 字符）易溢出。
缓解手段：将枪的"字符间延时（Inter-Character Delay）"设为 2~5ms，可显著降低出错率但无法根除；彻底方案是改用 VCP 模式，绕开系统键盘输入栈。

**Q：HID 与 VCP 两种采集源写入数据库的文本如何保证一致可比对？**
统一以 `(01)xxx(11)xxx...` 格式化文本为准：VCP 侧由 `Gs1UdiParser.Format()` 产出，HID 键盘侧直接使用枪固件输出的括号文本（清理掉 `0x1D`/换行）。入库时同时保留 `rawHex` 便于追溯。

**Q：以后要接第二把扫码枪怎么办？**
`AutoComScannerManager` 是实例类而非静态类，多把枪即多个实例、多个 `PortName`，各自独立订阅分发，无需改造。

---

## 已知限制

- 仅实现 UDI 常用 AI 子集（`01/11/17/10/21`），未覆盖 `240`、`710` 等扩展 AI；扩展需在 `Gs1UdiParser` 中补充 AI 元数据表。
- 分包依赖 `0x0D` 结束符，无结束符的枪需改用超时分包。
- 日期字段原样输出 6 位字符串，不做 `DateTime` 转换；转换与合法性校验由业务层负责。
- 示例仅演示单 COM 单设备，不含多枪并发与自动端口枚举（可按需扩展）。
- 未包含日志落盘、扫码去重、离线缓存等生产配套模块，接入业务系统时需自行补齐。

---

## 许可

MIT License。

## 贡献

欢迎提交 Issue / PR，尤其是不同型号扫码枪的报文样本（请提供 `rawHex`，不要只给解析后的文本），这有助于扩展解析兼容性。
</content>
