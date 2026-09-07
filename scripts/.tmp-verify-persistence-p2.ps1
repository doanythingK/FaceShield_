$ErrorActionPreference = 'Stop'
$temp = Join-Path $PSScriptRoot '.tmp-persistence-p2-harness'
Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $temp | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="..\..\FaceShield.csproj" /></ItemGroup>
</Project>
'@ | Set-Content -Path (Join-Path $temp 'Harness.csproj') -Encoding utf8

@'
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FaceShield;
using FaceShield.Enums.Workspace;
using FaceShield.Services.Video;
using FaceShield.Services.Workspace;
using System.Reflection;

static WorkspaceSnapshot Snap(string path, int frame) => new(
    path, WorkspaceMode.Manual, frame, 0, 10, DateTimeOffset.UtcNow,
    0, false, null, false, false, null, false, false, null);

static object IField(object target, string name) =>
    target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
    ?? throw new Exception($"Missing field {name}");
static object SField(Type type, string name) =>
    type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
    ?? throw new Exception($"Missing static field {name}");
static bool BoolField(object target, string name) =>
    (bool)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
    ?? throw new Exception($"Missing bool field {name}"));

static WriteableBitmap Stored(FrameMaskProvider provider, int frame)
{
    object masks = IField(provider, "_masks");
    MethodInfo tryGet = masks.GetType().GetMethod("TryGetValue") ?? throw new Exception("Missing TryGetValue");
    object?[] args = { frame, null };
    if (!(bool)(tryGet.Invoke(masks, args) ?? false) || args[1] is not WriteableBitmap bitmap)
        throw new Exception("Stored bitmap not found");
    return bitmap;
}

static unsafe WriteableBitmap Mask(int seed)
{
    const int w = 512, h = 320;
    var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    using var fb = bmp.Lock();
    byte* p = (byte*)fb.Address;
    for (int y = 0; y < h; y++)
    {
        byte* row = p + y * fb.RowBytes;
        for (int x = 0; x < w; x++)
        {
            int o = x * 4;
            row[o] = row[o + 1] = row[o + 2] = 0;
            row[o + 3] = (byte)(((x + y + seed) % 7) == 0 ? 255 : 0);
        }
    }
    return bmp;
}

AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
string token = Guid.NewGuid().ToString("N");

// SaveNow: final placeholder is published before provider snapshot capture, without
// holding _taskGate during the potentially proportional metadata copy.
{
    string path = Path.Combine(Path.GetTempPath(), $"fs-p2-final-{token}.mp4");
    var store = new WorkspaceStateStore();
    using var provider = new FrameMaskProvider();
    provider.SetFaceRects(0, new[] { new Rect(1, 1, 4, 4) }, new PixelSize(16, 16));
    var coordinator = new WorkspacePersistenceCoordinator(store, provider);
    object providerGate = IField(provider, "_stateGate");
    object taskGate = IField(coordinator, "_taskGate");
    bool held = false;
    try
    {
        Monitor.Enter(providerGate, ref held);
        Task final = Task.Run(() => coordinator.SaveNow(Snap(path, 101)));
        if (!SpinWait.SpinUntil(() => BoolField(coordinator, "_finalizing"), TimeSpan.FromSeconds(5)))
            throw new Exception("Final boundary was not published");
        if (!Monitor.TryEnter(taskGate, 1000))
            throw new Exception("SaveNow held _taskGate during provider snapshot capture");
        Monitor.Exit(taskGate);
        if (final.IsCompleted) throw new Exception("SaveNow completed while provider gate was blocked");
        Task<bool> reject = Task.Run(() => { try { coordinator.QueueSaveAsync(Snap(path, 102)); return false; } catch (InvalidOperationException) { return true; } });
        if (!reject.Wait(TimeSpan.FromSeconds(5)) || !reject.Result)
            throw new Exception("Queue was not rejected after final boundary");
        Monitor.Exit(providerGate); held = false;
        final.Wait(TimeSpan.FromSeconds(20));
        if (!final.IsCompletedSuccessfully) throw new Exception("Final save did not complete");
    }
    finally
    {
        if (held) Monitor.Exit(providerGate);
        coordinator.Dispose();
    }
}

// TryLoadWorkspace: hold directory lifecycle gate so load pauses after metadata
// capture; GlobalStateGate must already be free.
{
    string path = Path.Combine(Path.GetTempPath(), $"fs-p2-load-{token}.mp4");
    var store = new WorkspaceStateStore();
    using (var source = new FrameMaskProvider())
    {
        source.SetFaceRects(3, new[] { new Rect(2, 2, 8, 8) }, new PixelSize(32, 32));
        store.SaveWorkspace(Snap(path, 303), source);
    }
    object dirGate = SField(typeof(WorkspaceStateStore), "WorkspaceDirectoryGate");
    object globalGate = SField(typeof(WorkspaceStateStore), "GlobalStateGate");
    using var loadedProvider = new FrameMaskProvider();
    bool held = false;
    Task<(bool Ok, WorkspaceSnapshot? State)>? load = null;
    try
    {
        Monitor.Enter(dirGate, ref held);
        load = Task.Run(() => { bool ok = store.TryLoadWorkspace(path, WorkspaceMode.Manual, loadedProvider, out var state); return (ok, state); });
        Thread.Sleep(250);
        if (load.IsCompleted) throw new Exception("Load did not wait for directory reservation");
        if (!Monitor.TryEnter(globalGate, 1000)) throw new Exception("Load held GlobalStateGate during payload phase");
        Monitor.Exit(globalGate);
        Monitor.Exit(dirGate); held = false;
    }
    finally { if (held) Monitor.Exit(dirGate); }
    if (load == null || !load.Wait(TimeSpan.FromSeconds(20)) || !load.Result.Ok || load.Result.State?.SelectedFrameIndex != 303)
        throw new Exception("Load failed after reservation release");
}

// Directory reservations must be reference counted for overlapping readers.
{
    Type t = typeof(WorkspaceStateStore);
    MethodInfo reg = t.GetMethod("RegisterActiveWorkspaceDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
    MethodInfo unreg = t.GetMethod("UnregisterActiveWorkspaceDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
    MethodInfo active = t.GetMethod("HasActiveWorkspaceDirectoryUnderLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
    object gate = SField(t, "WorkspaceDirectoryGate");
    string dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"fs-p2-ref-{token}"));
    reg.Invoke(null, new object[] { dir }); reg.Invoke(null, new object[] { dir }); unreg.Invoke(null, new object[] { dir });
    lock (gate) if (!(bool)active.Invoke(null, new object[] { dir })!) throw new Exception("Shared reservation released early");
    unreg.Invoke(null, new object[] { dir });
    lock (gate) if ((bool)active.Invoke(null, new object[] { dir })!) throw new Exception("Final reservation was not released");
}

// Actual platform stress: persistence Save, direct immutable Save, provider clone,
// retirement/replacement, then persisted PNG decode/reload.
{
    string path = Path.Combine(Path.GetTempPath(), $"fs-p2-bitmap-{token}.mp4");
    var store = new WorkspaceStateStore();
    using var provider = new FrameMaskProvider();
    var coordinator = new WorkspacePersistenceCoordinator(store, provider);
    provider.SetMask(0, Mask(1));
    MethodInfo createLease = typeof(FrameMaskProvider).GetMethod("CreatePersistenceSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
    for (int iteration = 0; iteration < 8; iteration++)
    {
        WriteableBitmap stored = Stored(provider, 0);
        using var lease = (IDisposable)createLease.Invoke(provider, new object[] { CancellationToken.None })!;
        Task save = coordinator.QueueSaveAsync(Snap(path, 400 + iteration));
        Task direct = Task.Run(() => { for (int i = 0; i < 8; i++) { using var ms = new MemoryStream(); stored.Save(ms); if (ms.Length <= 8) throw new Exception("empty direct PNG"); } });
        Task clones = Task.Run(() => { for (int i = 0; i < 8; i++) { using var clone = provider.GetFinalMask(0) ?? throw new Exception("missing clone"); using var ms = new MemoryStream(); clone.Save(ms); if (ms.Length <= 8) throw new Exception("empty clone PNG"); } });
        provider.SetMask(0, Mask(100 + iteration));
        Task all = Task.WhenAll(save, direct, clones);
        if (!all.Wait(TimeSpan.FromSeconds(30))) throw new Exception("bitmap concurrency stress timed out");
        all.GetAwaiter().GetResult();
    }
    coordinator.FlushAsync().GetAwaiter().GetResult();
    coordinator.Dispose();
    using var reloaded = new FrameMaskProvider();
    if (!store.TryLoadWorkspace(path, WorkspaceMode.Manual, reloaded, out var state) || state == null) throw new Exception("reload failed");
    using var finalMask = reloaded.GetFinalMask(0) ?? throw new Exception("reloaded mask missing");
    using var finalPng = new MemoryStream(); finalMask.Save(finalPng);
    if (finalPng.Length <= 8) throw new Exception("reloaded PNG empty");
}

Console.WriteLine("[PersistenceP2Harness] PASS saveNowGateHold=false loadGlobalGateReleased=true refCountedReservations=true bitmapConcurrentSave=true");
'@ | Set-Content -Path (Join-Path $temp 'Program.cs') -Encoding utf8

try {
    dotnet run --project (Join-Path $temp 'Harness.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Persistence P2 harness failed with exit code $LASTEXITCODE" }
}
finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
