using System.Collections.Concurrent;

namespace IEC104Simulator.Protocol;

public class SlaveManager
{
    private readonly ConcurrentDictionary<string, IEC104SlaveServer> _instances = new();
    private int _idSeq;

    // Events for SignalR bridge (include instanceId)
    public event Action<string, LogEntry>? OnLog;
    public event Action<string, ConnectionInfo[]>? OnConnectionsChanged;
    public event Action<string, DataPoint>? OnDataPointUpdated;
    public event Action<string, CommandRecord>? OnCommandReceived;
    public event Action<string, CommandRecord>? OnCommandPending;
    public event Action<string, CommRecord>? OnCommRecord;
    public event Action? OnInstanceListChanged;

    public IEC104SlaveServer? Get(string instanceId) =>
        _instances.TryGetValue(instanceId, out var s) ? s : null;

    public IReadOnlyList<SlaveInstanceInfo> GetAll() =>
        _instances.Values.Select(s => s.ToInfo()).OrderBy(i => i.Id).ToList();

    public bool IsPortInUse(int port) =>
        _instances.Values.Any(s => s.Port == port);

    public SlaveInstanceInfo CreateInstance(int port, int ca, TlsConfig? tlsConfig = null)
    {
        var id = $"slave-{Interlocked.Increment(ref _idSeq)}";
        var server = new IEC104SlaveServer { InstanceId = id, Port = port, CommonAddress = ca, TlsConfig = tlsConfig ?? new TlsConfig() };
        server.DataPoints.InitDefaults();
        WireEvents(server);
        _instances[id] = server;
        OnInstanceListChanged?.Invoke();
        return server.ToInfo();
    }

    public async Task<SlaveInstanceInfo?> StartInstance(string instanceId)
    {
        var server = Get(instanceId);
        if (server == null) return null;
        await server.StartAsync();
        OnInstanceListChanged?.Invoke();
        return server.ToInfo();
    }

    public SlaveInstanceInfo? StopInstance(string instanceId)
    {
        var server = Get(instanceId);
        if (server == null) return null;
        server.Stop();
        OnInstanceListChanged?.Invoke();
        return server.ToInfo();
    }

    public bool RemoveInstance(string instanceId)
    {
        if (!_instances.TryRemove(instanceId, out var server)) return false;
        if (server.IsListening) server.Stop();
        UnwireEvents(server);
        OnInstanceListChanged?.Invoke();
        return true;
    }

    public bool ResolveCommand(string instanceId, string pendingId, bool accept)
    {
        var server = Get(instanceId);
        if (server == null) return false;
        server.ResolveCommand(pendingId, accept);
        return true;
    }

    private void WireEvents(IEC104SlaveServer server)
    {
        server.OnLog += entry => OnLog?.Invoke(server.InstanceId, entry);
        server.OnConnectionsChanged += conns => OnConnectionsChanged?.Invoke(server.InstanceId, conns);
        server.OnDataPointUpdated += dp => OnDataPointUpdated?.Invoke(server.InstanceId, dp);
        server.OnCommandReceived += cmd => OnCommandReceived?.Invoke(server.InstanceId, cmd);
        server.OnCommandPending += cmd => OnCommandPending?.Invoke(server.InstanceId, cmd);
        server.OnCommRecord += rec => OnCommRecord?.Invoke(server.InstanceId, rec);
    }

    private void UnwireEvents(IEC104SlaveServer server)
    {
        server.ClearEvents();
    }
}

public class SlaveInstanceInfo
{
    public string Id { get; set; } = "";
    public int Port { get; set; }
    public int CommonAddress { get; set; }
    public bool IsListening { get; set; }
    public int ConnectionCount { get; set; }
    public int DataPointCount { get; set; }
    public bool TlsEnabled { get; set; }
}
