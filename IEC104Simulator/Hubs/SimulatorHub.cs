using Microsoft.AspNetCore.SignalR;
using IEC104Simulator.Protocol;

namespace IEC104Simulator.Hubs;

public class SimulatorHub : Hub
{
    private readonly SlaveManager _manager;

    public SimulatorHub(SlaveManager manager)
    {
        _manager = manager;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("instanceList", _manager.GetAll());
        await base.OnConnectedAsync();
    }

    // ─── Instance Management ────────────────────────────

    public async Task CreateInstance(int port, int ca,
        bool tlsEnabled = false, string pfxPath = "", string pfxPassword = "",
        bool requireClientCert = false, string caCertPath = "",
        int periodicSec = 0,
        int apciK = 12, int apciW = 8, int apciT0 = 10, int apciT1 = 15, int apciT2 = 10, int apciT3 = 20,
        bool debugOutput = false)
    {
        if (_manager.IsPortInUse(port))
        {
            await Clients.Caller.SendAsync("createInstanceError", $"端口 {port} 已被其他实例占用，请使用其他端口");
            return;
        }
        var tls = new IEC104Simulator.Protocol.TlsConfig
        {
            Enabled = tlsEnabled,
            PfxPath = pfxPath,
            PfxPassword = pfxPassword,
            RequireClientCert = requireClientCert,
            CaCertPath = caCertPath,
        };
        var info = _manager.CreateInstance(port, ca, tls);
        // Apply additional settings before start
        var server = _manager.Get(info.Id)!;
        server.PeriodicIntervalSec = periodicSec;
        server.ApciConfig = new IEC104Simulator.Protocol.ApciConfig
        {
            K = apciK, W = apciW, T0 = apciT0, T1 = apciT1, T2 = apciT2, T3 = apciT3
        };
        server.DebugOutput = debugOutput;
        try
        {
            await _manager.StartInstance(info.Id);
        }
        catch (Exception ex)
        {
            _manager.RemoveInstance(info.Id);
            await Clients.Caller.SendAsync("createInstanceError", $"启动失败: {ex.Message}");
            return;
        }
        await Clients.All.SendAsync("instanceList", _manager.GetAll());
        // Push initial data so the new instance panel gets data points immediately
        if (server != null)
            await Clients.All.SendAsync("instanceData", info.Id, new
            {
                connections = server.GetConnections(),
                dataPoints = server.DataPoints.GetAll(),
                logs = server.GetLogs(),
                commandHistory = server.GetCommandHistory(),
                commRecords = server.GetCommRecords(),
            });
    }

    // Upload a cert/key file sent as base64 from the browser.
    // Returns the absolute server-side path where the file was saved.
    public async Task<string> UploadCertFile(string base64Content, string fileName)
    {
        // Sanitize — reject path traversal
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new HubException("无效的文件名");

        var ext = Path.GetExtension(safeName).ToLowerInvariant();
        if (!new[] { ".pfx", ".p12", ".crt", ".cer", ".pem" }.Contains(ext))
            throw new HubException($"不支持的证书文件格式: {ext}");

        var certsDir = Path.Combine(AppContext.BaseDirectory, "certs");
        Directory.CreateDirectory(certsDir);

        var destPath = Path.Combine(certsDir, safeName);
        var bytes = Convert.FromBase64String(base64Content);
        await File.WriteAllBytesAsync(destPath, bytes);
        return destPath;
    }

    public async Task StartInstance(string instanceId)
    {
        await _manager.StartInstance(instanceId);
        await Clients.All.SendAsync("instanceList", _manager.GetAll());
    }

    public async Task StopInstance(string instanceId)
    {
        _manager.StopInstance(instanceId);
        await Clients.All.SendAsync("instanceList", _manager.GetAll());
    }

    public async Task RemoveInstance(string instanceId)
    {
        _manager.RemoveInstance(instanceId);
        await Clients.All.SendAsync("instanceList", _manager.GetAll());
        await Clients.All.SendAsync("instanceRemoved", instanceId);
    }

    // ─── Instance Data ──────────────────────────────────

    public async Task GetInstanceData(string instanceId)
    {
        var server = _manager.Get(instanceId);
        if (server == null) return;
        await Clients.Caller.SendAsync("instanceData", instanceId, new
        {
            connections = server.GetConnections(),
            dataPoints = server.DataPoints.GetAll(),
            logs = server.GetLogs(),
            commandHistory = server.GetCommandHistory(),
            commRecords = server.GetCommRecords(),
        });
    }

    // ─── Data Point Operations (scoped by instance) ─────

    public Task ClearCommRecords(string instanceId)
    {
        _manager.Get(instanceId)?.ClearCommRecords();
        return Task.CompletedTask;
    }

    public Task ClearCommandHistory(string instanceId)
    {
        _manager.Get(instanceId)?.ClearCommandHistory();
        return Task.CompletedTask;
    }

    public Task ClearLogs(string instanceId)
    {
        _manager.Get(instanceId)?.ClearLogs();
        return Task.CompletedTask;
    }

    public async Task UpdateDataPoint(string instanceId, int ioa, double value, byte quality)
    {
        var server = _manager.Get(instanceId);
        if (server == null) return;
        var dp = server.DataPoints.Get(ioa);
        if (dp == null) return;
        dp.Value = value;
        dp.Quality = quality;
        dp.LastUpdate = server.SyncedNow;
        server.DataPoints.Set(dp);
        server.SendSpontaneous(ioa);
        await Clients.All.SendAsync("dataPointUpdate", instanceId, dp);
    }

    public async Task AddDataPoint(string instanceId, int ioa, byte typeId, string name, double value, int groupId = 0)
    {
        var server = _manager.Get(instanceId);
        if (server == null) return;
        if (server.DataPoints.Get(ioa) != null) return;
        var dp = server.DataPoints.Set(new DataPoint
        {
            Ioa = ioa,
            TypeId = typeId,
            Name = name,
            Value = value,
            GroupId = groupId,
            IsCommand = TypeIdHelper.IsCommandType(typeId),
        });
        await Clients.All.SendAsync("dataPointAdded", instanceId, dp);
    }

    public async Task DeleteDataPoint(string instanceId, int ioa)
    {
        var server = _manager.Get(instanceId);
        if (server == null) return;
        server.DataPoints.Remove(ioa);
        await Clients.All.SendAsync("dataPointDeleted", instanceId, ioa);
    }

    public Task TriggerSpontaneous(string instanceId, int ioa)
    {
        _manager.Get(instanceId)?.SendSpontaneous(ioa);
        return Task.CompletedTask;
    }

    public void TriggerGI(string instanceId)
    {
        _manager.Get(instanceId)?.TriggerGI();
    }

    public Task RespondCommand(string instanceId, string pendingId, bool accept)
    {
        _manager.ResolveCommand(instanceId, pendingId, accept);
        return Task.CompletedTask;
    }
}
