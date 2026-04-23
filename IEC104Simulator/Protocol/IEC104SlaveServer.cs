using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using lib60870;
using lib60870.CS101;
using lib60870.CS104;

namespace IEC104Simulator.Protocol;

public class IEC104SlaveServer
{
    private Server? _server;

    public string InstanceId { get; set; } = "";
    public DataPointManager DataPoints { get; } = new();
    public int CommonAddress { get; set; } = 1;
    public int Port { get; set; } = 2404;
    public bool IsListening { get; private set; }
    public TlsConfig TlsConfig { get; set; } = new();
    public ApciConfig ApciConfig { get; set; } = new();
    public int PeriodicIntervalSec { get; set; } = 0;  // 0=禁用; >0 每N秒背景扫描所有监视量(COT=2)
    public bool DebugOutput { get; set; } = true;     // lib60870 底层帧级调试输出 (APCI I/S/U帧收发细节→控制台)

    // Pending selects: key = ioa -> CommandRecord
    private readonly ConcurrentDictionary<int, CommandRecord> _pendingSelects = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _selectTimers = new();

    // Logs & command history
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private readonly ConcurrentQueue<CommandRecord> _commandHistory = new();
    private readonly ConcurrentQueue<CommRecord> _commRecords = new();
    private readonly ConcurrentQueue<CommRecord> _commNotifyQueue = new();
    private readonly SemaphoreSlim _commNotifySignal = new(0);
    private readonly object _commNotifyLock = new();
    private CancellationTokenSource? _commNotifyCts;
    private Task? _commNotifyTask;
    private int _cmdIdSeq;
    private const int MaxLogs = 500;
    private const int MaxCmdHistory = 200;
    private const int MaxComm = 500;

    // Connected clients tracking
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
    private System.Threading.Timer? _connSyncTimer;
    private System.Threading.Timer? _periodicTimer;

    // Debug output — uses shared static interceptor (Console.Out is process-global)
    private string? _debugLogDir;

    // Track which connections have already received M_EI_NA_1 (per connection lifecycle)
    private readonly ConcurrentDictionary<string, byte> _eiSentTo = new();

    // Events for SignalR bridge
    public event Action<LogEntry>? OnLog;
    public event Action<ConnectionInfo[]>? OnConnectionsChanged;
    public event Action<DataPoint>? OnDataPointUpdated;
    public event Action<CommandRecord>? OnCommandReceived;
    public event Action<CommandRecord>? OnCommandPending;  // fired while waiting for user decision
    public event Action<CommRecord>? OnCommRecord;

    // Pending user decisions: pendingId -> TaskCompletionSource<bool>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _userDecisions = new();
    private const int UserDecisionTimeoutMs = 30000;

    public void ResolveCommand(string pendingId, bool accept)
    {
        if (_userDecisions.TryRemove(pendingId, out var tcs))
            tcs.TrySetResult(accept);
    }

    public void ClearEvents()
    {
        OnLog = null;
        OnConnectionsChanged = null;
        OnDataPointUpdated = null;
        OnCommandReceived = null;
        OnCommandPending = null;
        OnCommRecord = null;
    }

    // ─── Start / Stop ─────────────────────────────────────

    private bool _initialized = true;  // InitDefaults called by SlaveManager.CreateInstance

    public Task StartAsync()
    {
        if (IsListening) return Task.CompletedTask;

        if (!_initialized)
        {
            DataPoints.InitDefaults();
            _initialized = true;
        }

        _server?.Stop();

        try
        {
            _server = BuildServer();
            _server.SetLocalAddress("0.0.0.0");
            _server.SetLocalPort(Port);
            _server.MaxQueueSize = 200;
            _server.ServerMode = ServerMode.CONNECTION_IS_REDUNDANCY_GROUP;

            // APCI 参数必须在 Start() 前设置
            var apci = _server.GetAPCIParameters();
            apci.K  = ApciConfig.K;
            apci.W  = ApciConfig.W;
            apci.T0 = ApciConfig.T0;
            apci.T1 = ApciConfig.T1;
            apci.T2 = ApciConfig.T2;
            apci.T3 = ApciConfig.T3;

            // Register handlers
            _server.SetConnectionRequestHandler(OnConnectionRequest, null);
            _server.SetConnectionEventHandler(OnConnectionEvent, null);
            _server.SetInterrogationHandler(OnInterrogation, null);
            _server.SetCounterInterrogationHandler(OnCounterInterrogation, null);
            _server.SetASDUHandler(OnAsduReceived, null);
            _server.SetClockSynchronizationHandler(OnClockSync, null);
            _server.SetReadHandler(OnReadCommand, null);

            _server.DebugOutput = DebugOutput;

            if (DebugOutput)
            {
                _debugLogDir = Path.Combine(Directory.GetCurrentDirectory(), "debug_logs", InstanceId);
                DebugLogInterceptor.Register(InstanceId, _debugLogDir, _server);
            }

            _server.Start();
            IsListening = true;
            EnsureCommNotifyLoopStarted();
            AddLog("started", $"IEC104 Slave 已启动 (lib60870)，监听端口 {Port}");
            // Start OS-level TCP polling to track master connections reliably
            _connSyncTimer?.Dispose();
            _connSyncTimer = new System.Threading.Timer(SyncConnectionsFromTcp, null, 1000, 2000);
            // 周期背景扫描
            _periodicTimer?.Dispose();
            _periodicTimer = null;
            if (PeriodicIntervalSec > 0)
            {
                int periodMs = PeriodicIntervalSec * 1000;
                _periodicTimer = new System.Threading.Timer(SendPeriodicData, null, periodMs, periodMs);
                AddLog("periodic", $"周期背景扫描已启动, 间隔 {PeriodicIntervalSec}s (COT=2)");
            }
        }
        catch (Exception ex)
        {
            AddLog("error", $"启动失败: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (!IsListening) return;
        _connSyncTimer?.Dispose();
        _connSyncTimer = null;
        _periodicTimer?.Dispose();
        _periodicTimer = null;
        _server?.Stop();
        StopCommNotifyLoop();
        IsListening = false;
        _connections.Clear();
        _eiSentTo.Clear();
        // 取消所有挂起SELECT的计时器，防止重启后出现过期回调
        foreach (var kv in _selectTimers)
            kv.Value.Cancel();
        _pendingSelects.Clear();
        _selectTimers.Clear();
        // 立即拒绝所有挂起的用户决策，防止连接线程在Stop()后继续阻塞最多30s
        foreach (var kvp in _userDecisions.ToArray())
            kvp.Value.TrySetResult(false);
        _userDecisions.Clear();
        if (_debugLogDir != null)
        {
            DebugLogInterceptor.Unregister(InstanceId);
            _debugLogDir = null;
        }
        AddLog("server", "IEC104 Slave 已停止");
        OnConnectionsChanged?.Invoke(GetConnections());
    }

    private void EnsureCommNotifyLoopStarted()
    {
        lock (_commNotifyLock)
        {
            if (_commNotifyTask is { IsCompleted: false })
                return;

            _commNotifyCts?.Dispose();
            _commNotifyCts = new CancellationTokenSource();
            var token = _commNotifyCts.Token;

            _commNotifyTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await _commNotifySignal.WaitAsync(token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    while (_commNotifyQueue.TryDequeue(out var rec))
                    {
                        var handler = OnCommRecord;
                        if (handler == null) continue;
                        try { handler(rec); } catch { }
                    }
                }
            }, token);
        }
    }

    private void StopCommNotifyLoop()
    {
        Task? taskToWait = null;

        lock (_commNotifyLock)
        {
            if (_commNotifyCts == null)
                return;

            _commNotifyCts.Cancel();
            _commNotifySignal.Release();
            taskToWait = _commNotifyTask;

            _commNotifyTask = null;
            _commNotifyCts.Dispose();
            _commNotifyCts = null;
        }

        try { taskToWait?.Wait(1000); } catch { }

        while (_commNotifyQueue.TryDequeue(out _)) { }
    }

    // ─── Connection Handlers ──────────────────────────────

    private bool OnConnectionRequest(object? parameter, IPAddress ipAddress)
    {
        AddLog("connect", $"主站连接请求: {ipAddress}", ipAddress.ToString());
        return true; // Accept all connections
    }

    // Polls OS-level TCP state every 2 s — catches connections lib60870 events may miss
    private void SyncConnectionsFromTcp(object? _)
    {
        try
        {
            if (!IsListening) return;
            var tcpConns = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            var active = tcpConns
                .Where(c => c.LocalEndPoint.Port == Port && c.State == TcpState.Established)
                .Select(c => c.RemoteEndPoint.ToString())
                .ToHashSet();

            bool changed = false;

            // Add newly discovered connections
            foreach (var r in active)
            {
                if (!_connections.ContainsKey(r))
                {
                    _connections[r] = new ConnectionInfo { RemoteAddress = r, Active = true };
                    AddLog("connect", $"主站已连接: {r}", r);
                    changed = true;
                    // TCP poll backup: fires only if no ASDU handler triggered EnsureEISent yet
                    Task.Delay(400).ContinueWith(_ => EnsureEISentEnqueue(r));
                }
            }

            // Remove connections no longer present
            foreach (var key in _connections.Keys.ToArray())
            {
                if (!active.Contains(key))
                {
                    _connections.TryRemove(key, out ConnectionInfo __ci);
                    _eiSentTo.TryRemove(key, out byte __ei);
                    CleanupSelectsForConnection(key); // 清除该连接残留的挂起SELECT
                    AddLog("disconnect", $"主站已断开: {key}", key);
                    changed = true;
                }
            }

            if (changed) OnConnectionsChanged?.Invoke(GetConnections());
        }
        catch { /* swallow — don't crash the timer thread */ }
    }

    private void OnConnectionEvent(object? parameter, ClientConnection connection, ClientConnectionEvent eventType)
    {
        string addr = "unknown";
        try
        {
            addr = connection.RemoteEndpoint?.ToString() ?? "unknown";
        }
        catch { }

        try
        {
            switch (eventType)
            {
                case ClientConnectionEvent.OPENED:
                    _connections[addr] = new ConnectionInfo { RemoteAddress = addr, Active = false };
                    AddLog("connect", $"主站已连接: {addr}", addr);
                    break;
                case ClientConnectionEvent.ACTIVE:
                    if (_connections.TryGetValue(addr, out var ci)) ci.Active = true;
                    else _connections[addr] = new ConnectionInfo { RemoteAddress = addr, Active = true };
                    AddLog("started", $"数据传输已激活: {addr}", addr);
                    // M_EI_NA_1 is handled precisely by EnsureEISent() on first ASDU from this connection
                    break;
                case ClientConnectionEvent.INACTIVE:
                    if (_connections.TryGetValue(addr, out var ci2)) ci2.Active = false;
                    AddLog("stopped", $"数据传输已停止: {addr}", addr);
                    break;
                case ClientConnectionEvent.CLOSED:
                    _connections.TryRemove(addr, out _);
                    _eiSentTo.TryRemove(addr, out _); // 清除EI标记，确保重连时重发M_EI_NA_1
                    CleanupSelectsForConnection(addr);
                    AddLog("disconnect", $"主站已断开: {addr}", addr);
                    break;
            }
        }
        catch (Exception ex)
        {
            AddLog("error", $"⚠️ OnConnectionEvent异常({eventType}/{addr}): {ex.Message}", addr);
        }
        OnConnectionsChanged?.Invoke(GetConnections());
    }

    // ─── Send helpers ─────────────────────────────────────

    /// <summary>
    /// 尝试发送一帧 ASDU；若连接已断开则记录日志并返回 false，调用方应立即停止后续发送。
    /// </summary>
    private bool TrySendASDU(IMasterConnection connection, ASDU asdu, string scope)
    {
        try
        {
            connection.SendASDU(asdu);
            return true;
        }
        catch (lib60870.ConnectionException ex)
        {
            AddLog("tx", $"{scope} 发送中断，连接已断开: {ex.Message}");
            return false;
        }
    }

    // ─── Interrogation Handler ────────────────────────────

    private bool OnInterrogation(object? parameter, IMasterConnection connection, ASDU asdu, byte qoi)
    {
        string connAddr = GetConnAddr(connection);
        EnsureEISent(connection, connAddr);
        if (CheckCaAndReject(connection, asdu, connAddr)) return true;
        string qoiDesc = (qoi == 0 || qoi == 20) ? "全站总召" : $"第{qoi - 20}组召唤";
        AddLog("gi", $"{qoiDesc} QOI={qoi}", connAddr);
        AddComm("rx", "C_IC_NA_1", 0, "", "ACT", $"{qoiDesc} QOI={qoi}", connAddr, asdu);

        // QOI=20: 总站召唤（标准）; QOI=0: 部分SCADA等同总站召唤使用; QOI=21-36: 分组1-16（标准）
        // QOI=0 treated as equivalent to QOI=20 for inter-operability
        if (qoi != 0 && qoi != 20 && !(qoi >= 21 && qoi <= 36))
        {
            connection.SendACT_CON(asdu, true); // negative — unknown QOI
            AddComm("tx", "C_IC_NA_1", 0, "", "ACT_CON", $"否认总召唤(-) QOI={qoi} 非标准值", connAddr, asdu);
            AddLog("gi", $"否认非标准总召唤 QOI={qoi}", connAddr);
            return true;
        }

        // ACT_CON
        connection.SendACT_CON(asdu, false);
        AddComm("tx", "C_IC_NA_1", 0, "", "ACT_CON", "总召唤确认(+)", connAddr, asdu);

        // Determine scope: QOI=20/0=全站; QOI=21-36=第1-16组
        var alp = connection.GetApplicationLayerParameters();
        CauseOfTransmission giCot;
        IEnumerable<DataPoint> giPoints;
        string giScope;
        if (qoi == 0 || qoi == 20)
        {
            giCot    = CauseOfTransmission.INTERROGATED_BY_STATION;
            // M_IT_NA_1(15) 和 M_IT_TB_1(37) 不得在总召唤响应中发送 (IEC 60870-5-101 Table 8)
            giPoints = DataPoints.GetMonitoringPoints().Where(d => d.TypeId != 15 && d.TypeId != 37);
            giScope  = "全站";
        }
        else
        {
            int groupId = qoi - 20; // QOI=21→group1, QOI=36→group16
            giCot    = (CauseOfTransmission)(int)qoi;
            // M_IT_NA_1(15) 和 M_IT_TB_1(37) 同样不得在分组召唤响应中发送
            giPoints = DataPoints.GetMonitoringPoints().Where(d => d.GroupId == groupId && d.TypeId != 15 && d.TypeId != 37);
            giScope  = $"第{groupId}组";
        }

        string cotLabel = qoi <= 20 ? "INROGEN" : $"INROGEN{qoi - 20}";
        int giCount = 0;
        bool giOk = true;
        foreach (var dp in giPoints)
        {
            var respAsdu = CreateMonitoringAsdu(alp, dp, giCot);
            if (respAsdu == null) continue;
            if (!TrySendASDU(connection, respAsdu, giScope)) { giOk = false; break; }
            giCount++;
            AddComm("tx", dp.TypeName, dp.Ioa, dp.Value.ToString("G6"), cotLabel, $"{giScope}召唤响应 IOA={dp.Ioa} QDS={dp.Quality}{DpTimeTag(dp)}", connAddr, respAsdu);
        }

        if (giOk)
        {
            connection.SendACT_TERM(asdu);
            AddComm("tx", "C_IC_NA_1", 0, $"{giCount}个数据点", "ACT_TERM", $"{giScope}召唤完成", connAddr, asdu);
        }
        return true;
    }

    private bool OnCounterInterrogation(object? parameter, IMasterConnection connection, ASDU asdu, byte qcc)
    {
        string connAddr = GetConnAddr(connection);
        EnsureEISent(connection, connAddr);
        if (CheckCaAndReject(connection, asdu, connAddr)) return true;

        // QCC: bits 0-5 = RQT (请求限定词), bits 6-7 = FRZ (冻结限定词)
        // RQT: 0=无请求, 1-4=第1-4组计数量, 5=总计数量
        // FRZ: 0=读(不冻结), 1=冻结不复位, 2=冻结并复位, 3=计数量复位
        int rqt = qcc & 0x3F;      // 低6位: 请求限定词
        int frz = (qcc >> 6) & 0x03; // 高2位: 冻结限定词

        string rqtDesc = rqt switch { 0 => "无请求", 5 => "总计数量", _ when rqt >= 1 && rqt <= 4 => $"第{rqt}组", _ => $"未知RQT={rqt}" };
        string frzDesc = frz switch { 0 => "读", 1 => "冻结不复位", 2 => "冻结并复位", 3 => "计数量复位", _ => "" };
        AddLog("ci", $"电能量召唤 QCC=0x{qcc:X2} RQT={rqt}({rqtDesc}) FRZ={frz}({frzDesc})", connAddr);
        AddComm("rx", "C_CI_NA_1", 0, "", "ACT", $"电能量召唤 QCC=0x{qcc:X2} RQT={rqt}({rqtDesc}) FRZ={frz}({frzDesc})", connAddr, asdu);

        // RQT 合法范围: 1-5 (标准); 0=无请求应否认; >5 不合法
        if (rqt == 0 || rqt > 5)
        {
            connection.SendACT_CON(asdu, true); // negative
            string rqtErrDesc = rqt == 0 ? "RQT=0 无请求" : $"RQT={rqt}非标准";
            AddComm("tx", "C_CI_NA_1", 0, "", "ACT_CON", $"否认电能量召唤(-) {rqtErrDesc}", connAddr, asdu);
            return true;
        }

        connection.SendACT_CON(asdu, false);
        AddComm("tx", "C_CI_NA_1", 0, "", "ACT_CON", "电能量召唤确认(+)", connAddr, asdu);

        // COT: RQT=5(总计数量)→COT=37, RQT=1-4→COT=38-41
        CauseOfTransmission ciCot;
        IEnumerable<DataPoint> ciPoints;
        string ciScope;
        if (rqt == 5)
        {
            ciCot = CauseOfTransmission.REQUESTED_BY_GENERAL_COUNTER; // COT=37
            ciPoints = DataPoints.GetAll().Where(d => d.TypeId == 15 || d.TypeId == 37);
            ciScope = "总计数量";
        }
        else
        {
            // RQT=1→COT=38, RQT=2→COT=39, RQT=3→COT=40, RQT=4→COT=41
            ciCot = (CauseOfTransmission)(37 + rqt); // COT=38..41
            ciPoints = DataPoints.GetAll().Where(d => (d.TypeId == 15 || d.TypeId == 37) && d.GroupId == rqt);
            ciScope = $"第{rqt}组计数量";
        }

        var alp = connection.GetApplicationLayerParameters();
        // RQT=5→REQCOGEN; RQT=1→REQCO1 ... RQT=4→REQCO4
        string cotLabel = rqt == 5 ? "REQCOGEN" : $"REQCO{rqt}";
        int ciCount = 0;
        bool ciOk = true;
        foreach (var dp in ciPoints)
        {
            var respAsdu = CreateMonitoringAsdu(alp, dp, ciCot);
            if (respAsdu == null) continue;
            if (!TrySendASDU(connection, respAsdu, ciScope)) { ciOk = false; break; }
            ciCount++;
            AddComm("tx", dp.TypeName, dp.Ioa, dp.Value.ToString("G6"), cotLabel, $"{ciScope}响应 IOA={dp.Ioa}{DpTimeTag(dp)}", connAddr, respAsdu);
        }

        if (ciOk)
        {
            connection.SendACT_TERM(asdu);
            AddComm("tx", "C_CI_NA_1", 0, $"{ciCount}个数据点", "ACT_TERM", $"{ciScope}召唤完成", connAddr, asdu);
        }
        return true;
    }

    // ─── Clock Sync Handler ──────────────────────────────

    // Clock offset applied to all TB-type data point timestamps after sync
    private TimeSpan _clockOffset = TimeSpan.Zero;

    // Returns current time adjusted by last clock sync offset
    public DateTime SyncedNow => DateTime.Now + _clockOffset;

    private bool OnClockSync(object? parameter, IMasterConnection connection, ASDU asdu, CP56Time2a newTime)
    {
        string connAddr = GetConnAddr(connection);
        EnsureEISent(connection, connAddr);
        if (CheckCaAndReject(connection, asdu, connAddr)) return false; // false suppresses lib60870 auto ACT_CON

        // Apply the master's time: treat CP56Time2a as UTC, convert to local time, then compute offset
        try
        {
            var masterUtc = new DateTime(2000 + newTime.Year, newTime.Month, newTime.DayOfMonth,
                newTime.Hour, newTime.Minute, newTime.Second, newTime.Millisecond, DateTimeKind.Utc);
            var masterLocal = masterUtc.ToLocalTime();
            _clockOffset = masterLocal - DateTime.Now;
            AddLog("clockSync", $"时钟同步: UTC {masterUtc:yyyy-MM-dd HH:mm:ss.fff} → 本地 {masterLocal:yyyy-MM-dd HH:mm:ss.fff} (偏差 {_clockOffset.TotalMilliseconds:F0}ms)", connAddr);
        }
        catch
        {
            AddLog("clockSync", $"时钟同步: {newTime}", connAddr);
        }

        AddComm("rx", "C_CS_NA_1", 0, newTime.ToString() ?? "", "ACT", "时钟同步", connAddr, asdu);
        AddComm("tx", "C_CS_NA_1", 0, "", "ACT_CON", "时钟同步确认(+)", connAddr, asdu);
        return true; // SDK auto-sends ACT_CON
    }

    // ─── Read Command Handler ────────────────────────────

    private bool OnReadCommand(object? parameter, IMasterConnection connection, ASDU asdu, int ioa)
    {
        string connAddr = GetConnAddr(connection);
        EnsureEISent(connection, connAddr);
        if (CheckCaAndReject(connection, asdu, connAddr)) return true;

        var dp = DataPoints.Get(ioa);
        if (dp == null)
        {
            // IEC 104: 未知IOA须回复 negative COT=47 (UNKNOWN_INFORMATION_OBJECT_ADDRESS)
            AddComm("rx", "C_RD_NA_1", ioa, "", "REQ", $"读命令 IOA={ioa}不存在", connAddr, asdu);
            asdu.Cot = CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS;
            asdu.IsNegative = true;
            connection.SendASDU(asdu);
            AddComm("tx", "C_RD_NA_1", ioa, "", "UNKNOWN_IOA", $"读命令否认(-) IOA={ioa}不存在", connAddr, asdu);
            return true;
        }

        AddComm("rx", "C_RD_NA_1", ioa, "", "REQ", "读命令", connAddr, asdu);

        // IEC 60870-5-101: C_RD_NA_1 仅对监视量有效；命令点不可读取，响应 COT=47 否认
        if (dp.IsCommand)
        {
            asdu.Cot = CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS;
            asdu.IsNegative = true;
            connection.SendASDU(asdu);
            AddComm("tx", "C_RD_NA_1", ioa, "", "UNKNOWN_IOA", $"读命令否认(-) IOA={ioa}是命令点不可读 (COT=47)", connAddr, asdu);
            return true;
        }

        var alp = connection.GetApplicationLayerParameters();
        var respAsdu = CreateMonitoringAsdu(alp, dp, CauseOfTransmission.REQUEST);
        if (respAsdu != null)
        {
            connection.SendASDU(respAsdu);
            AddComm("tx", dp.TypeName, ioa, dp.Value.ToString("G6"), "REQ", $"读响应{DpTimeTag(dp)}", connAddr, respAsdu);
            return true;
        }

        // TypeId 不在支持列表中 — 回复 COT=47 否认，不能静默丢弃请求
        asdu.Cot = CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS;
        asdu.IsNegative = true;
        connection.SendASDU(asdu);
        AddComm("tx", "C_RD_NA_1", ioa, "", "UNKNOWN_IOA", $"读命令否认(-) IOA={ioa} TypeId={dp.TypeId}不支持读取 (COT=47)", connAddr, asdu);
        return true;
    }

    // ─── ASDU / Command Handler ──────────────────────────

    private bool OnAsduReceived(object? parameter, IMasterConnection connection, ASDU asdu)
    {
        var typeId = asdu.TypeId;
        string connAddr = GetConnAddr(connection);
        EnsureEISent(connection, connAddr);
        if (CheckCaAndReject(connection, asdu, connAddr)) return true;

        switch (typeId)
        {
            case TypeID.C_SC_NA_1: // Single command
            case TypeID.C_SC_TA_1:
                return HandleSingleCommand(connection, asdu, connAddr);
            case TypeID.C_DC_NA_1: // Double command
            case TypeID.C_DC_TA_1:
                return HandleDoubleCommand(connection, asdu, connAddr);
            case TypeID.C_RC_NA_1: // Regulating step command
            case TypeID.C_RC_TA_1:
                return HandleStepCommand(connection, asdu, connAddr);
            case TypeID.C_SE_NA_1: // Setpoint normalised
            case TypeID.C_SE_TA_1:
                return HandleSetpointNorm(connection, asdu, connAddr);
            case TypeID.C_SE_NB_1: // Setpoint scaled
            case TypeID.C_SE_TB_1:
                return HandleSetpointScaled(connection, asdu, connAddr);
            case TypeID.C_SE_NC_1: // Setpoint short float
            case TypeID.C_SE_TC_1:
                return HandleSetpointFloat(connection, asdu, connAddr);
            case TypeID.C_BO_NA_1: // Bitstring command
            case TypeID.C_BO_TA_1:
                return HandleBitstringCommand(connection, asdu, connAddr);
            case TypeID.C_TS_NA_1: // 测试命令
                return HandleTestCommand(connection, asdu, connAddr);
            case TypeID.C_RP_NA_1: // 复位进程命令
                return HandleResetProcess(connection, asdu, connAddr);
            default:
                // IEC 60870-5-101 §7.1: 未知类型标识 → COT=44 (UNKNOWN_TYPE_IDENTIFICATION) 否认
                AddComm("rx", TypeIdHelper.GetName((byte)typeId), 0, "", asdu.Cot.ToString(),
                    $"未知/不支持的类型标识 TI={(byte)typeId}", connAddr, asdu);
                asdu.Cot = CauseOfTransmission.UNKNOWN_TYPE_ID;
                asdu.IsNegative = true;
                try { connection.SendASDU(asdu); } catch { }
                AddComm("tx", TypeIdHelper.GetName((byte)typeId), 0, "", "UNKNOWN_TI",
                    $"否认(-) 未知类型标识 TI={(byte)typeId} (COT=44)", connAddr, asdu);
                AddLog("reject", $"未知类型标识 TI={(byte)typeId}，已回复COT=44否认", connAddr);
                return true;
        }
    }

    // IEC 60870-5-104 §7.1: 公共地址不符 → COT=46 (UNKNOWN_COMMON_ADDRESS_OF_ASDU) 否认
    // Returns true if CA mismatched (caller should return appropriate value); false if CA matches.
    private bool CheckCaAndReject(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        if (asdu.Ca == CommonAddress) return false; // CA matches — proceed normally
        string typeName = TypeIdHelper.GetName((byte)asdu.TypeId);
        int wrongCa = asdu.Ca;
        AddLog("reject", $"CA不符 收到CA={wrongCa}，本站CA={CommonAddress} (COT=46)", connAddr);
        asdu.Cot = (CauseOfTransmission)46; // UNKNOWN_COMMON_ADDRESS_OF_ASDU
        asdu.IsNegative = true;
        try { connection.SendASDU(asdu); } catch { }
        AddComm("tx", typeName, 0, "", "UNKNOWN_CA",
            $"否认(-) CA={wrongCa}≠本站CA={CommonAddress} (COT=46)", connAddr, asdu);
        return true; // CA mismatched — caller should reject
    }

    // IEC 60870-5-101 §7.2.3: 无效传送原因 → COT=45 (UNKNOWN_CAUSE_OF_TRANSMISSION) 否认
    private bool RejectWithUnknownCot(IMasterConnection connection, ASDU asdu, int ioa, string connAddr)
    {
        string typeName = TypeIdHelper.GetName((byte)asdu.TypeId);
        int origCot = (int)asdu.Cot;
        asdu.Cot = (CauseOfTransmission)45; // UNKNOWN_CAUSE_OF_TRANSMISSION
        asdu.IsNegative = true;
        connection.SendASDU(asdu);
        AddComm("tx", typeName, ioa, "", "UNKNOWN_COT",
            $"命令否认(-) COT={origCot}对{typeName}无效 (COT=45)", connAddr, asdu);
        AddLog("reject", $"命令否认 {typeName} COT={origCot}无效 (COT=45)", connAddr);
        return true;
    }

    // IEC 104 §7.2.2: 未知IOA → COT=47 (UNKNOWN_INFORMATION_OBJECT_ADDRESS) 否认
    private bool RejectWithUnknownIoa(IMasterConnection connection, ASDU asdu, int ioa, string connAddr)
    {
        string typeName = TypeIdHelper.GetName((byte)asdu.TypeId);
        asdu.Cot = CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS;
        asdu.IsNegative = true;
        connection.SendASDU(asdu);
        AddComm("tx", typeName, ioa, "", "UNKNOWN_IOA", $"命令否认(-) IOA={ioa}不存在或非命令点 (COT=47)", connAddr, asdu);
        AddLog("reject", $"命令否认 IOA={ioa} 不存在或非命令点 (COT=47)", connAddr);
        return true;
    }

    // ─── Single Command ──────────────────────────────────

    private bool HandleSingleCommand(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        var cmd = (SingleCommand)asdu.GetElement(0);
        int ioa = cmd.ObjectAddress;
        bool select = cmd.Select;
        double value = cmd.State ? 1 : 0;
        int qu = cmd.QU;

        var record = CreateCommandRecord(ioa, (byte)asdu.TypeId, value, qu, select, asdu.Cot, connAddr);
        string timeTag = asdu.TypeId == TypeID.C_SC_TA_1
            ? FormatCP56Time(((SingleCommandWithCP56Time2a)asdu.GetElement(0)).Timestamp) : "";

        AddLog("cmdReceive", $"收到{(select ? "[SELECT]" : "[EXECUTE]")} {record.TypeName} IOA={ioa} SCS={(cmd.State ? "ON" : "OFF")} QU={qu}{timeTag}", connAddr);
        AddComm("rx", record.TypeName, ioa, cmd.State ? "ON(1)" : "OFF(0)", asdu.Cot.ToString(),
            (select ? "SELECT" : "EXECUTE") + $" QU={qu}{timeTag}", connAddr, asdu);

        if (asdu.Cot == CauseOfTransmission.DEACTIVATION)
            return HandleCancel(connection, asdu, record);
        if (asdu.Cot != CauseOfTransmission.ACTIVATION)
            return RejectWithUnknownCot(connection, asdu, ioa, connAddr);
        if (DataPoints.Get(ioa)?.IsCommand != true)
            return RejectWithUnknownIoa(connection, asdu, ioa, connAddr);

        if (select)
            return HandleSelect(connection, asdu, record, connAddr);
        else
            return HandleExecute(connection, asdu, record, value, connAddr);
    }

    // ─── Double Command ──────────────────────────────────

    private bool HandleDoubleCommand(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        var cmd = (DoubleCommand)asdu.GetElement(0);
        int ioa = cmd.ObjectAddress;
        bool select = cmd.Select;
        double value = (double)(int)cmd.State;
        int qu = cmd.QU;

        var record = CreateCommandRecord(ioa, (byte)asdu.TypeId, value, qu, select, asdu.Cot, connAddr);
        string timeTag = asdu.TypeId == TypeID.C_DC_TA_1
            ? FormatCP56Time(((DoubleCommandWithCP56Time2a)asdu.GetElement(0)).Timestamp) : "";

        AddLog("cmdReceive", $"收到{(select ? "[SELECT]" : "[EXECUTE]")} {record.TypeName} IOA={ioa} DCS={cmd.State} QU={qu}{timeTag}", connAddr);
        AddComm("rx", record.TypeName, ioa, cmd.State.ToString(), asdu.Cot.ToString(),
            (select ? "SELECT" : "EXECUTE") + $" QU={qu}{timeTag}", connAddr, asdu);

        if (asdu.Cot == CauseOfTransmission.DEACTIVATION)
            return HandleCancel(connection, asdu, record);
        if (asdu.Cot != CauseOfTransmission.ACTIVATION)
            return RejectWithUnknownCot(connection, asdu, ioa, connAddr);

        if (DataPoints.Get(ioa)?.IsCommand != true)
            return RejectWithUnknownIoa(connection, asdu, ioa, connAddr);

        // IEC 104: DCS=0(不定/中间态) 和 DCS=3(不定/中间态) 不允许作为命令值
        int dcs = (int)cmd.State;
        if (dcs == 0 || dcs == 3)
        {
            record.Status = "rejected";
            connection.SendACT_CON(asdu, true); // negative ACT_CON
            AddComm("tx", record.TypeName, ioa, "", "ACT_CON", $"否认双点命令(-) DCS={dcs}不合法", connAddr, asdu);
            AddLog("reject", $"[拒绝] {record.TypeName} IOA={ioa} DCS={dcs}不合法(标准禁止)", connAddr);
            AddCommandHistory(record);
            OnCommandReceived?.Invoke(record);
            return true;
        }

        if (select)
            return HandleSelect(connection, asdu, record, connAddr);
        else
            return HandleExecute(connection, asdu, record, value, connAddr);
    }

    // ─── Step Command ────────────────────────────────────

    private bool HandleStepCommand(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        var cmd = (StepCommand)asdu.GetElement(0);
        int ioa = cmd.ObjectAddress;
        bool select = cmd.Select;
        double value = (double)(int)cmd.State;
        int qu = cmd.QU;

        var record = CreateCommandRecord(ioa, (byte)asdu.TypeId, value, qu, select, asdu.Cot, connAddr);
        string timeTag = asdu.TypeId == TypeID.C_RC_TA_1
            ? FormatCP56Time(((StepCommandWithCP56Time2a)asdu.GetElement(0)).Timestamp) : "";

        AddLog("cmdReceive", $"收到{(select ? "[SELECT]" : "[EXECUTE]")} {record.TypeName} IOA={ioa} RCS={cmd.State} QU={qu}{timeTag}", connAddr);
        AddComm("rx", record.TypeName, ioa, cmd.State.ToString(), asdu.Cot.ToString(),
            (select ? "SELECT" : "EXECUTE") + $" QU={qu}{timeTag}", connAddr, asdu);

        if (asdu.Cot == CauseOfTransmission.DEACTIVATION)
            return HandleCancel(connection, asdu, record);
        if (asdu.Cot != CauseOfTransmission.ACTIVATION)
            return RejectWithUnknownCot(connection, asdu, ioa, connAddr);

        if (DataPoints.Get(ioa)?.IsCommand != true)
            return RejectWithUnknownIoa(connection, asdu, ioa, connAddr);

        // IEC 104: RCS=0(未定义) 和 RCS=3(未定义) 不允许作为步调命令值
        int rcs = (int)cmd.State;
        if (rcs == 0 || rcs == 3)
        {
            record.Status = "rejected";
            connection.SendACT_CON(asdu, true); // negative ACT_CON
            AddComm("tx", record.TypeName, ioa, "", "ACT_CON", $"否认步调命令(-) RCS={rcs}不合法", connAddr, asdu);
            AddLog("reject", $"[拒绝] {record.TypeName} IOA={ioa} RCS={rcs}不合法(标准禁止)", connAddr);
            AddCommandHistory(record);
            OnCommandReceived?.Invoke(record);
            return true;
        }

        if (select)
            return HandleSelect(connection, asdu, record, connAddr);
        else
            return HandleExecute(connection, asdu, record, value, connAddr);
    }

    // ─── Setpoint Normalized ─────────────────────────────

    private bool HandleSetpointNorm(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        var cmd = (SetpointCommandNormalized)asdu.GetElement(0);
        int ioa = cmd.ObjectAddress;
        bool select = cmd.QOS.Select;
        double value = cmd.NormalizedValue;
        int qu = cmd.QOS.QL;

        var record = CreateCommandRecord(ioa, (byte)asdu.TypeId, value, qu, select, asdu.Cot, connAddr);
        string timeTag = asdu.TypeId == TypeID.C_SE_TA_1
            ? FormatCP56Time(((SetpointCommandNormalizedWithCP56Time2a)asdu.GetElement(0)).Timestamp) : "";

        AddLog("cmdReceive", $"收到{(select ? "[SELECT]" : "[EXECUTE]")} {record.TypeName} IOA={ioa} NVA={value:F4} QL={qu}{timeTag}", connAddr);
        AddComm("rx", record.TypeName, ioa, value.ToString("F4"), asdu.Cot.ToString(),
            (select ? "SELECT" : "EXECUTE") + $" QL={qu}{timeTag}", connAddr, asdu);

        if (asdu.Cot == CauseOfTransmission.DEACTIVATION)
            return HandleCancel(connection, asdu, record);
        if (asdu.Cot != CauseOfTransmission.ACTIVATION)
            return RejectWithUnknownCot(connection, asdu, ioa, connAddr);
        if (DataPoints.Get(ioa)?.IsCommand != true)
            return RejectWithUnknownIoa(connection, asdu, ioa, connAddr);
        if (select)
            return HandleSelect(connection, asdu, record, connAddr);
        else
            return HandleExecute(connection, asdu, record, value, connAddr);
    }

    // ─── Setpoint Scaled ─────────────────────────────────

    private bool HandleSetpointScaled(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        var cmd = (SetpointCommandScaled)asdu.GetElement(0);
        int ioa = cmd.ObjectAddress;
        bool select = cmd.QOS.Select;
        double value = cmd.ScaledValue.Value;
        int qu = cmd.QOS.QL;

        var record = CreateCommandRecord(ioa, (byte)asdu.TypeId, value, qu, select, asdu.Cot, connAddr);
        string timeTag = asdu.TypeId == TypeID.C_SE_TB_1
            ? FormatCP56Time(((SetpointCommandScaledWithCP56Time2a)asdu.GetElement(0)).Timestamp) : "";

        AddLog("cmdReceive", $"收到{(select ? "[SELECT]" : "[EXECUTE]")} {record.TypeName} IOA={ioa} SVA={value} QL={qu}{timeTag}", connAddr);
        AddComm("rx", record.TypeName, ioa, value.ToString(), asdu.Cot.ToString(),
            (select ? "SELECT" : "EXECUTE") + $" QL={qu}{timeTag}", connAddr, asdu);

        if (asdu.Cot == CauseOfTransmission.DEACTIVATION)
            return HandleCancel(connection, asdu, record);
        if (asdu.Cot != CauseOfTransmission.ACTIVATION)
            return RejectWithUnknownCot(connection, asdu, ioa, connAddr);
        if (DataPoints.Get(ioa)?.IsCommand != true)
            return RejectWithUnknownIoa(connection, asdu, ioa, connAddr);
        if (select)
            return HandleSelect(connection, asdu, record, connAddr);
        else
            return HandleExecute(connection, asdu, record, value, connAddr);
    }

    // ─── Setpoint Short Float ────────────────────────────

    private bool HandleSetpointFloat(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        var cmd = (SetpointCommandShort)asdu.GetElement(0);
        int ioa = cmd.ObjectAddress;
        bool select = cmd.QOS.Select;
        double value = cmd.Value;
        int qu = cmd.QOS.QL;

        var record = CreateCommandRecord(ioa, (byte)asdu.TypeId, value, qu, select, asdu.Cot, connAddr);
        string timeTag = asdu.TypeId == TypeID.C_SE_TC_1
            ? FormatCP56Time(((SetpointCommandShortWithCP56Time2a)asdu.GetElement(0)).Timestamp) : "";

        AddLog("cmdReceive", $"收到{(select ? "[SELECT]" : "[EXECUTE]")} {record.TypeName} IOA={ioa} Float={value:F2} QL={qu}{timeTag}", connAddr);
        AddComm("rx", record.TypeName, ioa, value.ToString("F2"), asdu.Cot.ToString(),
            (select ? "SELECT" : "EXECUTE") + $" QL={qu}{timeTag}", connAddr, asdu);

        if (asdu.Cot == CauseOfTransmission.DEACTIVATION)
            return HandleCancel(connection, asdu, record);
        if (asdu.Cot != CauseOfTransmission.ACTIVATION)
            return RejectWithUnknownCot(connection, asdu, ioa, connAddr);
        if (DataPoints.Get(ioa)?.IsCommand != true)
            return RejectWithUnknownIoa(connection, asdu, ioa, connAddr);
        if (select)
            return HandleSelect(connection, asdu, record, connAddr);
        else
            return HandleExecute(connection, asdu, record, value, connAddr);
    }

    // ─── Bitstring Command ───────────────────────────────

    private bool HandleBitstringCommand(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        var cmd = (Bitstring32Command)asdu.GetElement(0);
        int ioa = cmd.ObjectAddress;
        double value = cmd.Value;

        var record = CreateCommandRecord(ioa, (byte)asdu.TypeId, value, 0, false, asdu.Cot, connAddr);
        string timeTag = asdu.TypeId == TypeID.C_BO_TA_1
            ? FormatCP56Time(((Bitstring32CommandWithCP56Time2a)asdu.GetElement(0)).Timestamp) : "";

        AddLog("cmdReceive", $"收到[EXECUTE] {record.TypeName} IOA={ioa} BSI=0x{(uint)value:X8}{timeTag}", connAddr);
        AddComm("rx", record.TypeName, ioa, $"0x{(uint)value:X8}", asdu.Cot.ToString(), $"EXECUTE{timeTag}", connAddr, asdu);

        if (asdu.Cot == CauseOfTransmission.DEACTIVATION)
            return HandleCancel(connection, asdu, record);
        if (asdu.Cot != CauseOfTransmission.ACTIVATION)
            return RejectWithUnknownCot(connection, asdu, ioa, connAddr);
        if (DataPoints.Get(ioa)?.IsCommand != true)
            return RejectWithUnknownIoa(connection, asdu, ioa, connAddr);
        return HandleExecute(connection, asdu, record, value, connAddr);
    }

    // ─── Select / Execute / Cancel Logic ─────────────────

    private bool HandleSelect(IMasterConnection connection, ASDU asdu, CommandRecord record, string connAddr)
    {
        int ioa = record.Ioa;

        // IEC 60870-5-101 §7.3.2.1: While a select is pending from another connection,
        // a new SELECT from a different connection must be immediately rejected.
        if (_pendingSelects.TryGetValue(ioa, out var existingPending) && existingPending.ConnInfo != connAddr)
        {
            record.Status = "rejected";
            connection.SendACT_CON(asdu, true); // negative
            AddComm("tx", record.TypeName, ioa, "", "ACT_CON",
                $"否认SELECT(-) IOA={ioa} 已被连接 {existingPending.ConnInfo} SELECT", connAddr, asdu);
            AddLog("reject", $"[SELECT拒绝] IOA={ioa} 已被其他连接 {existingPending.ConnInfo} SELECT", connAddr);
            AddCommandHistory(record);
            OnCommandReceived?.Invoke(record);
            return true;
        }

        // Ask user to accept/reject
        bool accepted = WaitForUserDecision(record);

        if (!accepted)
        {
            record.Status = "rejected";
            connection.SendACT_CON(asdu, true); // negative
            AddComm("tx", record.TypeName, ioa, "", "ACT_CON", "否认SELECT(-)", connAddr, asdu);
            AddLog("reject", $"[SELECT拒绝] {record.TypeName} IOA={ioa}", connAddr);
            AddCommandHistory(record);
            OnCommandReceived?.Invoke(record);
            return true;
        }

        record.Status = "selected";

        // Cancel existing select for this IOA
        if (_selectTimers.TryRemove(ioa, out var oldCts))
            oldCts.Cancel();

        _pendingSelects[ioa] = record;

        // Select timeout 60s
        var timeoutCts = new CancellationTokenSource();
        _selectTimers[ioa] = timeoutCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60000, timeoutCts.Token);
                if (_pendingSelects.TryRemove(ioa, out var expired))
                {
                    expired.Status = "timeout";
                    AddLog("timeout", $"Select超时 IOA={ioa}", record.ConnInfo);
                    OnCommandReceived?.Invoke(expired);
                }
                _selectTimers.TryRemove(ioa, out _);
            }
            catch (OperationCanceledException) { }
        });

        // ACT_CON positive
        connection.SendACT_CON(asdu, false);
        AddComm("tx", record.TypeName, ioa, "", "ACT_CON", "确认SELECT(+)", connAddr, asdu);

        AddLog("select", $"[SELECT确认] {record.TypeName} IOA={ioa}", connAddr);
        AddCommandHistory(record);
        OnCommandReceived?.Invoke(record);
        return true;
    }

    private bool WaitForUserDecision(CommandRecord record)
    {
        var pendingId = Guid.NewGuid().ToString("N");
        record.PendingId = pendingId;
        record.Status = "pending";

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _userDecisions[pendingId] = tcs;

        // Notify frontend
        OnCommandPending?.Invoke(record);

        // Block this thread (lib60870 connection thread) waiting for user decision
        bool accepted = tcs.Task.Wait(UserDecisionTimeoutMs) && tcs.Task.Result;

        _userDecisions.TryRemove(pendingId, out _);
        record.PendingId = null;

        if (!tcs.Task.IsCompleted)
            AddLog("timeout", $"用户未响应命令确认，已自动拒绝 IOA={record.Ioa}", record.ConnInfo);

        return accepted;
    }

    // IEC 60870-5-101 §7.3.2.2: SELECT and EXECUTE must share the same command type family
    // (NA and TA variants of the same command are considered the same family)
    private static byte GetCommandFamily(byte typeId) => typeId switch
    {
        45 or 58 => 45, // C_SC_NA_1 / C_SC_TA_1 — Single command
        46 or 59 => 46, // C_DC_NA_1 / C_DC_TA_1 — Double command
        47 or 60 => 47, // C_RC_NA_1 / C_RC_TA_1 — Step command
        48 or 61 => 48, // C_SE_NA_1 / C_SE_TA_1 — Setpoint normalised
        49 or 62 => 49, // C_SE_NB_1 / C_SE_TB_1 — Setpoint scaled
        50 or 63 => 50, // C_SE_NC_1 / C_SE_TC_1 — Setpoint short float
        51 or 64 => 51, // C_BO_NA_1 / C_BO_TA_1 — Bitstring
        _ => typeId,
    };

    private bool HandleExecute(IMasterConnection connection, ASDU asdu, CommandRecord record, double value, string connAddr)
    {
        int ioa = record.Ioa;

        // If there's a pending select from a different connection, reject immediately (no user prompt)
        if (_pendingSelects.TryGetValue(ioa, out var pending) && pending.ConnInfo != connAddr)
        {
            record.Status = "rejected";
            connection.SendACT_CON(asdu, true); // negative
            AddComm("tx", record.TypeName, ioa, "", "ACT_CON", $"否认EXECUTE(-) 其他连接已SELECT", connAddr, asdu);
            AddLog("reject", $"Execute被拒绝(不同连接Select) IOA={ioa}", connAddr);
            AddCommandHistory(record);
            OnCommandReceived?.Invoke(record);
            return true;
        }

        // IEC 60870-5-101 §7.3.2.2: EXECUTE type must match SELECT type (same command family)
        bool hasPendingSelect = _pendingSelects.TryGetValue(ioa, out var pendingSelect);
        if (hasPendingSelect && GetCommandFamily(pendingSelect!.TypeId) != GetCommandFamily(record.TypeId))
        {
            record.Status = "rejected";
            connection.SendACT_CON(asdu, true); // negative
            AddComm("tx", record.TypeName, ioa, "", "ACT_CON",
                $"否认EXECUTE(-) 类型不匹配: SELECT={pendingSelect.TypeName} EXECUTE={record.TypeName}", connAddr, asdu);
            AddLog("reject", $"[类型不匹配] SELECT={pendingSelect.TypeName} 但 EXECUTE={record.TypeName} IOA={ioa}", connAddr);
            AddCommandHistory(record);
            OnCommandReceived?.Invoke(record);
            return true;
        }

        bool accepted;
        if (hasPendingSelect)
        {
            // SBO flow: select was already confirmed by user, auto-accept the execute
            accepted = true;
        }
        else
        {
            // Direct execute: ask user
            accepted = WaitForUserDecision(record);
        }

        if (!accepted)
        {
            record.Status = "rejected";
            connection.SendACT_CON(asdu, true); // negative
            AddComm("tx", record.TypeName, record.Ioa, "", "ACT_CON", "否认EXECUTE(-)", connAddr, asdu);
            AddLog("reject", $"[EXECUTE拒绝] {record.TypeName} IOA={ioa}", connAddr);
            AddCommandHistory(record);
            OnCommandReceived?.Invoke(record);
            return true;
        }

        // Clear pending select
        if (_pendingSelects.TryRemove(ioa, out _))
        {
            if (_selectTimers.TryRemove(ioa, out var cts)) cts.Cancel();
        }

        record.Status = "executed";

        // ACT_CON positive
        connection.SendACT_CON(asdu, false);
        AddComm("tx", record.TypeName, record.Ioa, value.ToString("G6"), "ACT_CON", "确认EXECUTE(+)", connAddr, asdu);

        // Apply to data model
        // IEC 60870-5-101 §7.2.1: T=1 (test frame) — respond normally but do NOT update process state
        bool isTestFrame = asdu.IsTest;
        var dp = DataPoints.Get(ioa);
        if (isTestFrame)
        {
            AddLog("test", $"[TEST帧] {record.TypeName} IOA={ioa} 不更新过程值 (T=1)", connAddr);
        }
        else if (dp != null)
        {
            dp.Value = value;
            dp.LastUpdate = SyncedNow;  // use master-synced time
            OnDataPointUpdated?.Invoke(dp);

            // QU=1 Short Pulse: auto-revert after 200ms
            // QU=2 Long Pulse:  auto-revert after 2000ms
            // QU=0/3: no auto-revert (persistent)
            int pulseDurationMs = record.Qu == 1 ? 200 : record.Qu == 2 ? 2000 : 0;
            if (pulseDurationMs > 0)
            {
                // Single (SCS): 0↔1; Double (DCS): 1(OFF)↔2(ON); Step (RCS): 1(DOWN)↔2(UP)
                bool isDoubleOrStep = record.TypeId == 46 || record.TypeId == 59 ||
                                      record.TypeId == 47 || record.TypeId == 60;
                double revertValue = isDoubleOrStep ? (value == 2 ? 1 : 2) : (value == 1 ? 0 : 1);
                var capturedDp = dp;
                string quName = record.Qu == 1 ? "Short Pulse" : "Long Pulse";
                Task.Delay(pulseDurationMs).ContinueWith(_ =>
                {
                    capturedDp.Value = revertValue;
                    capturedDp.LastUpdate = SyncedNow;
                    OnDataPointUpdated?.Invoke(capturedDp);
                    SendSpontaneous(capturedDp.Ioa);
                    var msg = $"脉冲复位 QU={record.Qu}({quName}) IOA={ioa} → {revertValue}";
                    AddLog("pulse", $"[{msg}] {record.TypeName}");
                    AddComm("tx", record.TypeName, ioa, revertValue.ToString("G6"), "SPONT", msg, "", null);
                });
            }
        }

        AddLog("execute", $"[EXECUTE] {record.TypeName} IOA={ioa} Value={value}", connAddr);

        // ACT_TERM
        connection.SendACT_TERM(asdu);
        AddComm("tx", record.TypeName, record.Ioa, value.ToString("G6"), "ACT_TERM", "执行终止", connAddr, asdu);

        AddCommandHistory(record);
        OnCommandReceived?.Invoke(record);
        return true;
    }

    private bool HandleCancel(IMasterConnection connection, ASDU asdu, CommandRecord record)
    {
        int ioa = record.Ioa;
        string connAddr = record.ConnInfo;

        // IEC 104 §7.3.2: DEACTIVATION (取消) 必须来自与 SELECT 相同的连接
        // 若无对应 SELECT 悬挂，或 SELECT 来自不同连接，须回复 negative DEACT_CON
        bool hadPendingSelect = false;
        if (_pendingSelects.TryGetValue(ioa, out var existingSelect))
        {
            if (existingSelect.ConnInfo == connAddr)
            {
                // Same connection — valid cancel
                hadPendingSelect = true;
                _pendingSelects.TryRemove(ioa, out _);
                if (_selectTimers.TryRemove(ioa, out var cts)) cts.Cancel();
            }
            else
            {
                // Different connection — reject cancel, leave the original select intact
                AddLog("reject", $"[CANCEL拒绝] IOA={ioa} SELECT来自 {existingSelect.ConnInfo}，CANCEL来自 {connAddr}，跨连接取消被拒绝", connAddr);
            }
        }

        record.Status = hadPendingSelect ? "cancelled" : "rejected";

        // DEACT_CON — negative if no matching select existed or cross-connection
        asdu.Cot = CauseOfTransmission.DEACTIVATION_CON;
        asdu.IsNegative = !hadPendingSelect;
        connection.SendASDU(asdu);
        string detail = hadPendingSelect ? "取消确认(+)" : "否认取消(-) 无匹配SELECT或跨连接";
        AddComm("tx", record.TypeName, record.Ioa, "", "DEACT_CON", detail, record.ConnInfo, asdu);

        AddLog(hadPendingSelect ? "cancel" : "reject", $"[CANCEL] {record.TypeName} IOA={ioa} {(hadPendingSelect ? "已取消" : "无SELECT可取消")}", connAddr);
        AddCommandHistory(record);
        OnCommandReceived?.Invoke(record);
        return true;
    }

    private void CleanupSelectsForConnection(string connAddr)
    {
        foreach (var kvp in _pendingSelects)
        {
            if (kvp.Value.ConnInfo == connAddr)
            {
                _pendingSelects.TryRemove(kvp.Key, out _);
                if (_selectTimers.TryRemove(kvp.Key, out var cts)) cts.Cancel();
            }
        }
    }

    private bool HandleTestCommand(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        AddLog("test", $"收到测试命令 C_TS_NA_1 COT={asdu.Cot}", connAddr);
        AddComm("rx", "C_TS_NA_1", 0, "", asdu.Cot.ToString(), "测试命令", connAddr, asdu);
        if (asdu.Cot != CauseOfTransmission.ACTIVATION)
            return RejectWithUnknownCot(connection, asdu, 0, connAddr);
        connection.SendACT_CON(asdu, false);
        AddComm("tx", "C_TS_NA_1", 0, "", "ACT_CON", "测试命令确认(+)", connAddr, asdu);
        return true;
    }

    private bool HandleResetProcess(IMasterConnection connection, ASDU asdu, string connAddr)
    {
        AddLog("reset", $"收到复位进程命令 C_RP_NA_1 COT={asdu.Cot}", connAddr);
        AddComm("rx", "C_RP_NA_1", 0, "", asdu.Cot.ToString(), "复位进程", connAddr, asdu);
        if (asdu.Cot != CauseOfTransmission.ACTIVATION)
            return RejectWithUnknownCot(connection, asdu, 0, connAddr);
        connection.SendACT_CON(asdu, false);
        AddComm("tx", "C_RP_NA_1", 0, "", "ACT_CON", "复位进程确认(+)", connAddr, asdu);
        return true;
    }

    // ─── Spontaneous / GI Sending ─────────────────────────

    // Primary path: called at entry of every ASDU handler — sends M_EI_NA_1 exactly once
    // per connection lifecycle, BEFORE the first ASDU response (precise per IEC 104 standard)
    private void EnsureEISent(IMasterConnection connection, string connAddr)
    {
        if (!_eiSentTo.TryAdd(connAddr, 0)) return; // already sent for this connection
        try
        {
            var alp = connection.GetApplicationLayerParameters();
            var asdu = new ASDU(alp, CauseOfTransmission.INITIALIZED, false, false, 0, CommonAddress, false);
            asdu.AddInformationObject(new EndOfInitialization(0)); // COI=0: local power-on
            connection.SendASDU(asdu);  // targeted to THIS connection
            AddComm("tx", "M_EI_NA_1", 0, "COI=0", "INIT", "初始化结束(上电)", connAddr, asdu);
            AddLog("init", $"发送初始化结束帧 M_EI_NA_1 → {connAddr}", connAddr);
        }
        catch (Exception ex)
        {
            _eiSentTo.TryRemove(connAddr, out _); // allow retry on next ASDU
            AddLog("error", $"M_EI_NA_1 发送异常: {ex.Message}", connAddr);
        }
    }

    // Backup path: called from TCP-poll when no ASDU has arrived yet from this connection
    // Uses EnqueueASDU (broadcast) guarded by _eiSentTo for idempotency
    private void EnsureEISentEnqueue(string addr)
    {
        if (!_eiSentTo.TryAdd(addr, 0)) return; // primary path already handled it
        try
        {
            if (_server == null) { _eiSentTo.TryRemove(addr, out _); return; }
            var alp = _server.GetApplicationLayerParameters();
            var asdu = new ASDU(alp, CauseOfTransmission.INITIALIZED, false, false, 0, CommonAddress, false);
            asdu.AddInformationObject(new EndOfInitialization(0));
            _server.EnqueueASDU(asdu);
            AddComm("tx", "M_EI_NA_1", 0, "COI=0", "INIT", "初始化结束(上电)[备用路]", addr, asdu);
            AddLog("init", $"发送初始化结束帧 M_EI_NA_1(Enqueue) → {addr}", addr);
        }
        catch (Exception ex)
        {
            _eiSentTo.TryRemove(addr, out _);
            AddLog("error", $"M_EI_NA_1 Enqueue 异常: {ex.Message}", addr);
        }
    }

    public void SendSpontaneous(int ioa)
    {
        if (_server == null) return;
        var dp = DataPoints.Get(ioa);
        if (dp == null) return;

        var alp = _server.GetApplicationLayerParameters();
        var asdu = CreateMonitoringAsdu(alp, dp, CauseOfTransmission.SPONTANEOUS);
        if (asdu != null)
        {
            _server.EnqueueASDU(asdu);
            AddComm("tx", dp.TypeName, ioa, dp.Value.ToString("G6"), "SPONT", $"自发上送 IOA={ioa} QDS={dp.Quality}{DpTimeTag(dp)}", "", asdu);
        }
    }

    public void TriggerGI()
    {
        if (_server == null) return;
        var alp = _server.GetApplicationLayerParameters();
        int count = 0;
        // COT=SPONTANEOUS (3): 主动推送所有监视量；COT=INROGEN(20)仅允许在C_IC_NA_1响应序列中使用
        foreach (var dp in DataPoints.GetMonitoringPoints())
        {
            var asdu = CreateMonitoringAsdu(alp, dp, CauseOfTransmission.SPONTANEOUS);
            if (asdu != null)
            {
                _server.EnqueueASDU(asdu);
                count++;
                AddComm("tx", dp.TypeName, dp.Ioa, dp.Value.ToString("G6"), "SPONT", $"手动推送 IOA={dp.Ioa} QDS={dp.Quality}{DpTimeTag(dp)}", "", asdu);
            }
        }
        AddLog("gi", $"手动推送全数据 上送{count}个数据点 (COT=3 SPONT)");
    }

    private void SendPeriodicData(object? _)
    {
        try
        {
            if (_server == null || !IsListening) return;
            var alp = _server.GetApplicationLayerParameters();
            int count = 0;
            foreach (var dp in DataPoints.GetMonitoringPoints())
            {
                // M_IT_NA_1(15) 和 M_IT_TB_1(37) 积分量不支持 COT=BACKGROUND_SCAN
                // (IEC 60870-5-101 Table 8: 积分量仅允许 COT=3 自发或 COT=37-41 计数量召唤响应)
                if (dp.TypeId == 15 || dp.TypeId == 37) continue;
                var periodicAsdu = CreateMonitoringAsdu(alp, dp, CauseOfTransmission.BACKGROUND_SCAN);
                if (periodicAsdu != null)
                {
                    _server.EnqueueASDU(periodicAsdu);
                    count++;
                }
            }
            if (count > 0)
                AddLog("periodic", $"周期背景扫描 (COT=2) 上送 {count} 个数据点");
        }
        catch (Exception ex)
        {
            AddLog("error", $"周期上送异常: {ex.Message}");
        }
    }

    // ─── ASDU Construction ────────────────────────────────

    private ASDU? CreateMonitoringAsdu(ApplicationLayerParameters alp, DataPoint dp, CauseOfTransmission cot)
    {
        var qd = new QualityDescriptor
        {
            Overflow    = (dp.Quality & 0x01) != 0,  // OV bit0
            Blocked     = (dp.Quality & 0x10) != 0,  // BL bit4
            Substituted = (dp.Quality & 0x20) != 0,  // SB bit5
            NonTopical  = (dp.Quality & 0x40) != 0,  // NT bit6
            Invalid     = (dp.Quality & 0x80) != 0,  // IV bit7
        };
        var ts = new CP56Time2a(dp.LastUpdate);

        InformationObject? io = dp.TypeId switch
        {
            // Single point
            1 => new SinglePointInformation(dp.Ioa, dp.Value != 0, qd),
            30 => new SinglePointWithCP56Time2a(dp.Ioa, dp.Value != 0, qd, ts),
            // Double point
            3 => new DoublePointInformation(dp.Ioa, (DoublePointValue)(int)dp.Value, qd),
            31 => new DoublePointWithCP56Time2a(dp.Ioa, (DoublePointValue)(int)dp.Value, qd, ts),
            // Step position
            5 => new StepPositionInformation(dp.Ioa, (int)dp.Value, false, qd),
            32 => new StepPositionWithCP56Time2a(dp.Ioa, (int)dp.Value, false, qd, ts),
            // Bitstring 32
            7 => new Bitstring32(dp.Ioa, (uint)dp.Value, qd),
            33 => new Bitstring32WithCP56Time2a(dp.Ioa, (uint)dp.Value, qd, ts),
            // Measured normalised
            9 => new MeasuredValueNormalized(dp.Ioa, (float)dp.Value, qd),
            34 => new MeasuredValueNormalizedWithCP56Time2a(dp.Ioa, (float)dp.Value, qd, ts),
            21 => new MeasuredValueNormalizedWithoutQuality(dp.Ioa, (float)dp.Value),
            // Measured scaled
            11 => new MeasuredValueScaled(dp.Ioa, (int)dp.Value, qd),
            35 => new MeasuredValueScaledWithCP56Time2a(dp.Ioa, (int)dp.Value, qd, ts),
            // Measured short float
            13 => new MeasuredValueShort(dp.Ioa, (float)dp.Value, qd),
            36 => new MeasuredValueShortWithCP56Time2a(dp.Ioa, (float)dp.Value, qd, ts),
            // Integrated totals — propagate IV bit from DataPoint quality (IEC 60870-5-101 §7.2.6.17)
            15 => new IntegratedTotals(dp.Ioa, new BinaryCounterReading { Value = (int)dp.Value, Invalid = (dp.Quality & 0x80) != 0 }),
            37 => new IntegratedTotalsWithCP56Time2a(dp.Ioa, new BinaryCounterReading { Value = (int)dp.Value, Invalid = (dp.Quality & 0x80) != 0 }, ts),
            // Packed single-point with SCD
            20 => new PackedSinglePointWithSCD(dp.Ioa,
                new StatusAndStatusChangeDetection { STn = (ushort)dp.Value, CDn = 0 }, qd),
            _ => null,
        };

        if (io == null) return null;

        var asdu = new ASDU(alp, cot, false, false, 0, CommonAddress, false);
        asdu.AddInformationObject(io);
        return asdu;
    }

    // ─── Helpers ──────────────────────────────────────────

    private CommandRecord CreateCommandRecord(int ioa, byte typeId, double value, int qu, bool select, CauseOfTransmission cot, string connAddr)
    {
        return new CommandRecord
        {
            Id = Interlocked.Increment(ref _cmdIdSeq),
            Ioa = ioa,
            TypeId = typeId,
            TypeName = TypeIdHelper.GetName(typeId),
            Value = value,
            Qu = qu,
            IsSelect = select,
            CotName = cot.ToString(),
            ConnInfo = connAddr,
        };
    }

    private void AddLog(string type, string message, string connAddr = "")
    {
        var entry = new LogEntry { Type = type, Message = message, ConnInfo = connAddr };
        _logs.Enqueue(entry);
        while (_logs.Count > MaxLogs) _logs.TryDequeue(out _);
        OnLog?.Invoke(entry);
    }

    /// <summary>
    /// 获取主站连接的真实 IP:Port 地址。
    /// IMasterConnection 接口本身无 RemoteEndpoint，需转型为 lib60870 具体实现。
    /// </summary>
    private static string GetConnAddr(IMasterConnection connection)
    {
        try
        {
            if (connection is lib60870.CS104.ClientConnection cc)
                return cc.RemoteEndpoint?.ToString() ?? "unknown";
            // Fallback: try reflection for other implementations
            var prop = connection.GetType().GetProperty("RemoteEndpoint");
            if (prop != null)
                return prop.GetValue(connection)?.ToString() ?? "unknown";
            return "unknown";
        }
        catch { return "unknown"; }
    }

    private static readonly Dictionary<int, string> _cotCnMap = new()
    {
        { 1,"周期"}, { 2,"背景扫描"}, { 3,"突发"}, { 4,"初始化"}, { 5,"请求"},
        { 6,"激活"}, { 7,"激活确认"}, { 8,"停止激活"}, { 9,"停止激活确认"}, {10,"激活结束"},
        {11,"远程命令引起"}, {12,"就地命令引起"},
        {20,"总召唤响应"},
        {21,"第1组"}, {22,"第2组"}, {23,"第3组"}, {24,"第4组"},
        {25,"第5组"}, {26,"第6组"}, {27,"第7组"}, {28,"第8组"},
        {29,"第9组"}, {30,"第10组"},{31,"第11组"},{32,"第12组"},
        {33,"第13组"},{34,"第14组"},{35,"第15组"},{36,"第16组"},
        {37,"电能量总召响应"},
        {38,"第1组电能量"},{39,"第2组电能量"},{40,"第3组电能量"},{41,"第4组电能量"},
        {44,"未知类型标识"}, {45,"未知传送原因"}, {46,"未知公共地址"}, {47,"未知信息对象地址"},
    };

    private void AddComm(string direction, string typeName, int ioa, string value, string cot, string detail, string connInfo, ASDU? asdu = null)
    {
        var rec = new CommRecord
        {
            Direction = direction,
            TypeName = typeName,
            Ioa = ioa,
            Value = value,
            Cot = cot,
            Detail = detail,
            ConnInfo = connInfo,
            Ca = CommonAddress,
        };
        if (asdu != null)
        {
            rec.TypeId = (int)asdu.TypeId;
            rec.CotCode = (int)asdu.Cot;
            rec.Ca = asdu.Ca;
            rec.IsNeg = asdu.IsNegative;
            rec.IsTest = asdu.IsTest;
            rec.Oa = asdu.Oa;
            rec.NumberOfElem = asdu.NumberOfElements;
            rec.IsSeq = asdu.IsSequence;

            // Build complete FullDecode — no truncation
            string cotCn = _cotCnMap.TryGetValue(rec.CotCode, out var cn) ? cn : asdu.Cot.ToString();
            var sb = new System.Text.StringBuilder();
            sb.Append($"ASDU: TI={rec.TypeId}({rec.TypeName})");
            sb.Append($"  VSQ: NUM={rec.NumberOfElem} SQ={(rec.IsSeq ? 1 : 0)}");
            sb.Append($"  COT={rec.CotCode}({cotCn})");
            sb.Append($"  T={(rec.IsTest ? 1 : 0)} PN={(rec.IsNeg ? 1 : 0)} OA={rec.Oa} CA={rec.Ca}");
            if (rec.Direction == "rx" && rec.ConnInfo.Length > 0)
                sb.Append($"  [{rec.ConnInfo}]");
            sb.Append('\n');
            sb.Append($"体:   ");
            if (rec.Ioa > 0) sb.Append($"IOA={rec.Ioa}  ");
            if (rec.Value.Length > 0) sb.Append($"Val={rec.Value}  ");
            if (rec.Detail.Length > 0) sb.Append(rec.Detail);
            rec.FullDecode = sb.ToString();
        }
        _commRecords.Enqueue(rec);
        while (_commRecords.Count > MaxComm) _commRecords.TryDequeue(out _);
        _commNotifyQueue.Enqueue(rec);
        _commNotifySignal.Release();
    }

    private void AddCommandHistory(CommandRecord record)
    {
        _commandHistory.Enqueue(record);
        while (_commandHistory.Count > MaxCmdHistory) _commandHistory.TryDequeue(out _);
    }

    private static string FormatCP56Time(CP56Time2a t)
    {
        if (t == null) return "";
        try
        {
            var dt = new DateTime(2000 + t.Year, t.Month, t.DayOfMonth, t.Hour, t.Minute, t.Second, t.Millisecond);
            string validity = t.Invalid ? "TIV=1[时间无效]" : "TIV=0[时间有效]";
            string sub = t.Substituted ? " SU=1" : "";
            return $" ⏱{dt:yyyy-MM-dd HH:mm:ss.fff} {validity}{sub}";
        }
        catch { return ""; }
    }

    // Returns CP56Time2a tag string for monitoring TypeIds that carry a timestamp (TB/TA series)
    private static readonly HashSet<int> TimedMonitorTypeIds = new() { 30, 31, 32, 33, 34, 35, 36, 37 };
    private static string DpTimeTag(DataPoint dp)
    {
        if (!TimedMonitorTypeIds.Contains(dp.TypeId)) return "";
        var dt = dp.LastUpdate;
        return $" ⏱{dt:yyyy-MM-dd HH:mm:ss.fff}";
    }

    public ConnectionInfo[] GetConnections() => _connections.Values.ToArray();
    public LogEntry[] GetLogs() => _logs.ToArray();
    public CommandRecord[] GetCommandHistory() => _commandHistory.ToArray();
    public CommRecord[] GetCommRecords() => _commRecords.ToArray();

    public void ClearCommRecords()    { while (_commRecords.TryDequeue(out _)) {} }
    public void ClearCommandHistory() { while (_commandHistory.TryDequeue(out _)) {} }
    public void ClearLogs()           { while (_logs.TryDequeue(out _)) {} }

    public SlaveInstanceInfo ToInfo() => new()
    {
        Id = InstanceId,
        Port = Port,
        CommonAddress = CommonAddress,
        IsListening = IsListening,
        ConnectionCount = _connections.Count,
        DataPointCount = DataPoints.GetAll().Count,
        TlsEnabled = TlsConfig.Enabled,
    };

    private Server BuildServer()
    {
        if (!TlsConfig.Enabled)
            return new Server();

        if (!File.Exists(TlsConfig.PfxPath))
            throw new FileNotFoundException($"TLS 证书文件不存在: {TlsConfig.PfxPath}");

        var serverCert = new X509Certificate2(TlsConfig.PfxPath, TlsConfig.PfxPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

        var tlsInfo = new TlsSecurityInformation(serverCert)
        {
            ChainValidation = !string.IsNullOrWhiteSpace(TlsConfig.CaCertPath),
            AllowOnlySpecificCertificates = TlsConfig.RequireClientCert,
        };

        if (!string.IsNullOrWhiteSpace(TlsConfig.CaCertPath))
        {
            if (!File.Exists(TlsConfig.CaCertPath))
                throw new FileNotFoundException($"CA 证书文件不存在: {TlsConfig.CaCertPath}");
            var caCert = new X509Certificate2(TlsConfig.CaCertPath);
            tlsInfo.AddCA(caCert);
        }

        return new Server(tlsInfo);
    }
}

// ─── Debug Log Interceptor (Process-global singleton) ─────────────────────────
// Console.Out is process-global, so we use ONE interceptor no matter how many
// slave instances have DebugOutput=true. Each instance registers its logDir;
// we track which lib60870 connection-number belongs to which instance by
// observing "CS104 SLAVE: New connection" → next "CS104 SLAVE CONNECTION N:"
// sequence.
//
// Log layout:  debug_logs/{instanceId}/connection_{N}.log
//                                      general.log

internal sealed class DebugLogInterceptor : TextWriter
{
    // ── Singleton plumbing ───────────────────────────────
    private static readonly object _lock = new();
    private static DebugLogInterceptor? _instance;
    private static TextWriter? _originalOut;
    private static int _refCount;

    /// <summary>Register an instance's log dir. First caller installs the interceptor.</summary>
    public static void Register(string instanceId, string logDir, object server)
    {
        lock (_lock)
        {
            if (_instance == null)
            {
                _originalOut = Console.Out;
                _instance = new DebugLogInterceptor(_originalOut);
                Console.SetOut(_instance);
            }
            _instance.AddInstance(instanceId, logDir, server);
            _refCount++;
        }
    }

    /// <summary>Unregister. Last caller restores Console.Out.</summary>
    public static void Unregister(string instanceId)
    {
        lock (_lock)
        {
            _instance?.RemoveInstance(instanceId);
            _refCount--;
            if (_refCount <= 0 && _originalOut != null)
            {
                Console.SetOut(_originalOut);
                _instance?.Dispose();
                _instance = null;
                _originalOut = null;
                _refCount = 0;
            }
        }
    }

    // ── Per-instance bookkeeping ─────────────────────────
    // instanceId → logDir
    private readonly ConcurrentDictionary<string, string> _dirs = new();
    // instanceId → { fileKey → StreamWriter }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StreamWriter>> _files = new();
    // instanceId → lib60870 Server reference  (for connection ownership lookup)
    private readonly ConcurrentDictionary<string, object> _servers = new();
    // connection-number → instanceId  (cached after first lookup)
    private readonly ConcurrentDictionary<int, string> _connOwner = new();
    // Reflection handles (resolved once)
    private static System.Reflection.FieldInfo? _serverConnsField;
    private static System.Reflection.FieldInfo? _clientConnIdField;

    // ── Core ─────────────────────────────────────────────
    private readonly TextWriter _inner;
    private readonly StringBuilder _buf = new();
    private static readonly Regex _connRx = new(@"CS104 SLAVE CONNECTION (\d+):", RegexOptions.Compiled);

    public override Encoding Encoding => _inner.Encoding;

    private DebugLogInterceptor(TextWriter inner) { _inner = inner; }

    private void AddInstance(string instanceId, string logDir, object server)
    {
        Directory.CreateDirectory(logDir);
        _dirs[instanceId] = logDir;
        _files[instanceId] = new ConcurrentDictionary<string, StreamWriter>();
        _servers[instanceId] = server;
        // Resolve reflection fields once
        if (_serverConnsField == null)
        {
            var sType = server.GetType();
            _serverConnsField = sType.GetField("allOpenConnections",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var ccType = sType.Assembly.GetType("lib60870.CS104.ClientConnection");
            _clientConnIdField = ccType?.GetField("connectionID",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        }
    }

    private void RemoveInstance(string instanceId)
    {
        _dirs.TryRemove(instanceId, out _);
        _servers.TryRemove(instanceId, out _);
        if (_files.TryRemove(instanceId, out var writers))
            foreach (var w in writers.Values)
                try { w.Dispose(); } catch { }
        // Remove cached ownership
        foreach (var kv in _connOwner)
            if (kv.Value == instanceId) _connOwner.TryRemove(kv.Key, out _);
    }

    /// <summary>Look up which registered Server owns a given connection number.</summary>
    private string? FindOwner(int connNum)
    {
        if (_connOwner.TryGetValue(connNum, out var cached)) return cached;
        if (_serverConnsField == null || _clientConnIdField == null) return null;

        foreach (var kv in _servers)
        {
            try
            {
                if (_serverConnsField.GetValue(kv.Value) is System.Collections.IList conns)
                {
                    foreach (var cc in conns)
                    {
                        if (_clientConnIdField.GetValue(cc) is int cid && cid == connNum)
                        {
                            _connOwner[connNum] = kv.Key;
                            return kv.Key;
                        }
                    }
                }
            }
            catch { }
        }
        return null;
    }

    // ── TextWriter overrides ─────────────────────────────
    public override void Write(char value)
    {
        lock (_buf)
        {
            if (value == '\n')
            {
                var line = _buf.ToString().TrimEnd('\r');
                _buf.Clear();
                FlushLine(line);
            }
            else
            {
                _buf.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        foreach (var c in value) Write(c);
    }

    public override void WriteLine(string? value)
    {
        Write(value ?? "");
        Write('\n');
    }

    private void FlushLine(string line)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var stamped = $"[{ts}] {line}";

        // 1. Always write to real console (once, no chaining)
        _inner.WriteLine(stamped);

        // 2. Connection lines → route to owning instance only
        var cm = _connRx.Match(line);
        if (cm.Success && int.TryParse(cm.Groups[1].Value, out var connNum))
        {
            var owner = FindOwner(connNum);
            if (owner != null && _dirs.TryGetValue(owner, out var dir))
                WriteToFile(owner, dir, $"connection_{connNum}", stamped);
            return;
        }

        // 3. Non-connection lines (general): write to all instances
        foreach (var kv in _dirs)
            WriteToFile(kv.Key, kv.Value, "general", stamped);
    }

    private void WriteToFile(string instanceId, string logDir, string fileKey, string stamped)
    {
        if (!_files.TryGetValue(instanceId, out var dict)) return;
        var writer = dict.GetOrAdd(fileKey, k =>
            new StreamWriter(Path.Combine(logDir, $"{k}.log"), append: true, Encoding.UTF8) { AutoFlush = true });
        try { writer.WriteLine(stamped); } catch { /* best-effort */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_buf) { if (_buf.Length > 0) FlushLine(_buf.ToString()); }
            foreach (var dict in _files.Values)
                foreach (var w in dict.Values)
                    try { w.Dispose(); } catch { }
            _files.Clear();
            _dirs.Clear();
            _servers.Clear();
            _connOwner.Clear();
        }
        base.Dispose(disposing);
    }
}
