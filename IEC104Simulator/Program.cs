using IEC104Simulator.Hubs;
using IEC104Simulator.Protocol;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<SlaveManager>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<SimulatorHub>("/hub");

// REST API endpoints
app.MapGet("/api/instances", (SlaveManager mgr) => mgr.GetAll());

// ─── Wire SignalR events ────────────────────────────

var manager = app.Services.GetRequiredService<SlaveManager>();
var hubContext = app.Services.GetRequiredService<IHubContext<SimulatorHub>>();

manager.OnLog += (id, entry) => hubContext.Clients.All.SendAsync("log", id, entry);
manager.OnConnectionsChanged += (id, conns) => hubContext.Clients.All.SendAsync("masterList", id, conns);
manager.OnDataPointUpdated += (id, dp) => hubContext.Clients.All.SendAsync("dataPointUpdate", id, dp);
manager.OnCommandReceived += (id, cmd) => hubContext.Clients.All.SendAsync("commandReceived", id, cmd);
manager.OnCommandPending += (id, cmd) => hubContext.Clients.All.SendAsync("commandPending", id, cmd);
manager.OnCommRecord += (id, rec) => hubContext.Clients.All.SendAsync("commRecord", id, rec);
manager.OnInstanceListChanged += () => hubContext.Clients.All.SendAsync("instanceList", manager.GetAll());

// Create a default instance
var defaultInfo = manager.CreateInstance(2404, 1);
await manager.StartInstance(defaultInfo.Id);

Console.WriteLine("======================================");
Console.WriteLine(" IEC 60870-5-104 Slave Simulator");
Console.WriteLine("  (powered by lib60870.NET)");
Console.WriteLine("======================================");
Console.WriteLine($" Default Slave   : Port {defaultInfo.Port}, CA {defaultInfo.CommonAddress}");
Console.WriteLine($" Web UI          : http://localhost:5010");
Console.WriteLine("======================================");

app.Run("http://0.0.0.0:5010");
