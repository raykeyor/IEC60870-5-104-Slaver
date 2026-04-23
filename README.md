# IEC 60870-5-104 Slave Simulator

基于 **lib60870.NET** 构建的 IEC 60870-5-104 从站（Slave / RTU）模拟器，具备完整的 Web 管理界面，支持多实例并发运行。

> An IEC 60870-5-104 slave (RTU) simulator built on lib60870.NET with a full-featured web UI and multi-instance support.

---

## 功能特性 / Features

- **多从站实例** — 可在不同端口同时运行多个 104 从站，Web UI 标签页独立管理
- **数据点管理** — 支持监视量（遥测/遥信）与控制量（遥控/遥调），可实时修改值与品质位
- **ASDU 类型全覆盖** — M_SP, M_DP, M_ME_NA/NB/NC/ND, M_ST, M_BO, M_ME_TD/TE/TF, M_SP_TB, M_DP_TB 等 16+ 种遥测遥信，C_SC, C_DC, C_RC, C_SE_NA/NB/NC, C_BO 等命令类型
- **总召唤 / 分组召唤** — 完整支持 QOI=20 全站总召与 QOI=21–36 分组召唤
- **命令执行控制** — 支持直接执行（Direct Execute）与先选后执（Select-Before-Execute），可配置超时
- **用户决策模式** — 命令到达时暂停等待用户在 Web UI 确认或拒绝（最长 30 秒超时）
- **背景扫描** — 可配置周期性自动上报所有监视量（COT=2）
- **TLS 加密** — 支持服务端证书（PFX/P12），可选要求客户端证书验证
- **通信记录** — 完整的 APDU 解析日志（TX/RX），支持 ASDU 结构解码显示
- **APCI 参数** — 可自定义 k / w / T0 / T1 / T2 / T3 参数
- **调试输出** — 可选开启 lib60870 底层帧级调试（I/S/U 帧收发细节）
- **暗色 Web UI** — 基于 SignalR 实时推送，无需刷新页面

---

## 技术栈 / Tech Stack

| 组件 | 版本 |
|------|------|
| .NET | 8.0 |
| ASP.NET Core (Web + SignalR) | 8.0 |
| [lib60870.NET](https://github.com/mz-automation/lib60870) | 2.3.0 |

---

## 快速开始 / Quick Start

### 前置要求

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)

### 运行

```powershell
cd IEC104Simulator
dotnet run
```

启动后访问 **http://localhost:5010** 打开 Web 管理界面。

默认会自动创建一个从站实例：端口 **2404**，公共地址（CA）**1**。

### 发布（自包含可执行文件）

```powershell
dotnet publish IEC104Simulator -c Release -r win-x64 --self-contained true -o ./publish
```

---

## 项目结构 / Project Structure

```
iec04.sln
IEC104Simulator/
├── Program.cs                  # 应用入口，ASP.NET Core 配置
├── IEC104Simulator.csproj
├── Hubs/
│   └── SimulatorHub.cs         # SignalR Hub，Web UI ↔ 后端桥接
├── Protocol/
│   ├── IEC104SlaveServer.cs    # 核心从站服务（lib60870 封装）
│   ├── SlaveManager.cs         # 多实例生命周期管理
│   ├── DataPointManager.cs     # 数据点 CRUD
│   └── DataTypes.cs            # DTO 数据模型
└── wwwroot/
    ├── index.html              # Web 管理界面
    └── manual.html             # 使用手册
```

---

## 使用说明 / Usage

1. 打开 **http://localhost:5010**
2. 默认 **slave-1** 已自动启动，监听 2404 端口
3. 在「数据点」标签页增删改数据点，双击值单元格可实时修改
4. 点击「+」新建更多从站实例（不同端口）
5. 「通信记录」标签页查看完整 APDU 帧解析
6. 「命令历史」标签页查看主站下发的所有命令

详细功能说明请访问 **http://localhost:5010/manual.html**。

---

## 证书测试 / Certificate Testing

`generate_test_certs.ps1` 可生成三类测试证书（需 Windows PowerShell 5.1+）：

```powershell
.\generate_test_certs.ps1
```

- `expired.pfx` — 已过期证书（用于测试证书验证失败场景）
- `expiring-soon.pfx` — 24 小时内到期证书
- `invalid.pfx` — 损坏的证书文件

> 证书文件含私钥，已加入 `.gitignore`，不会提交到仓库。

---

## License

GNU General Public License (GPL)
