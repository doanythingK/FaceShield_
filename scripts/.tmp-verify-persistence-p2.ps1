$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path $PSScriptRoot '.tmp-persistence-p2-harness'
Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $temp | Out-Null

$csproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\FaceShield.csproj" />
  </ItemGroup>
</Project>
'@
Set-Content -Path (Join-Path $temp 'Harness.csproj') -Value $csproj -Encoding utf8

$program = @'
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FaceShield;
using FaceShield.Enums.Workspace;
using FaceShield.Services.Video;
using FaceShield.Services.Workspace;
using System.Reflection;

static WorkspaceSnapshot Snapshot(string videoPath, int selectedFrame) => new(
    videoPath,
    WorkspaceMode.Manual,
    selectedFrame,
    viewStartSeconds: 0,
    secondsPerScreen: 10,
    lastOpened: DateTimeOffset.UtcNow,
    autoResumeIndex: 0,
    autoCompleted: false,
    autoRunSignature: null,
    autoExportGateRequired: false,
    autoExportGatePassed: false,
    autoExportGateFailure: null,
    autoExportHybridPolicyAvailable: false,
    autoExportAllowHybridCopy: false,
    autoExportHybridDisableReasons: null);

static object InstanceField(object target, string name)
    => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
        ?? throw new InvalidOperationException($"Missing instance field {name}");

static object StaticField(Type type, string name)
    => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException($"Missing static field {name}");

static bool ReadBoolField(object target, string name)
{
    FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing bool field {name}");
    return (bool)(field.GetValue(target) ?? false);
}

static object InvokeNonPublic(object target, string name, params object?[] args)
{
    MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing method {name}");
    return method.Invoke(target, args)
        ?? throw new InvalidOperationException($"Method {name} returned null");
}

static unsafe WriteableBitmap CreateMask(int seed, int width = 512, int height = 320)
{
    var bitmap = new WriteableBitmap(
        new PixelSize(width, height),
        new Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Premul);
    using var fb = bitmap.Lock();
    byte* basePtr = (byte*)fb.Address;
    for (int y = 0; y < height; y++)
    {
        byte* row = basePtr + y * fb.RowBytes;
        for (int x = 0; x < width; x++)
        {
            int p = x * 4;
            byte alpha = (byte)(((x + y + seed) % 7) == 0 ? 255 : 0);
            row[p + 0] = 0;
            row[p + 1] = 0;
            row[p + 2] = 0;
            row[p + 3] = alpha;
        }
    }
    return bitmap;
}

AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
string token = Guid.NewGuid().ToString("N");

// 1) SaveNow must publish its terminal boundary before mask snapshot capture, and
// must not hold _taskGate while waiting for FrameMaskProvider._stateGate.
{
    string video = Path.Combine(Path.GetTempPath(), $"faceshield-p2-final-{token}.mp4");
    var store = new WorkspaceStateStore();
    using var provider = new FrameMaskProvider();
    provider.SetFaceRects(0, new[] { new Rect(1, 1, 4, 4) }, new PixelSize(16, 16));
    var coordinator = new WorkspacePersistenceCoordinator(store, provider);

    object providerGate = InstanceField(provider, "_stateGate");
    object taskGate = InstanceField(coordinator, "_taskGate");
    bool providerHeld = false;
    try
    {
        Monitor.Enter(providerGate, ref providerHeld);
        Task final = Task.Run(() => coordinator.SaveNow(Snapshot(video, 101)));
        if (!SpinWait.SpinUntil(() => ReadBoolField(coordinator, "_finalizing"), TimeSpan.FromSeconds(5)))
            throw new Exception("SaveNow did not publish finalization while provider snapshot was blocked.");

        bool taskGateEntered = Monitor.TryEnter(taskGate, 1000);
        if (!taskGateEntered)
            throw new Exception("SaveNow held _taskGate while blocked on persistence snapshot capture.");
        Monitor.Exit(taskGate);

        if (final.IsCompleted)
            throw new Exception("SaveNow completed while provider snapshot capture was deliberately blocked.");

        Task<bool> rejected = Task.Run(() =>
        {
            try
            {
                _ = coordinator.QueueSaveAsync(Snapshot(video, 102));
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        });
        if (!await rejected.WaitAsync(TimeSpan.FromSeconds(5)))
            throw new Exception("QueueSaveAsync was not rejected after terminal boundary publication.");

        Monitor.Exit(providerGate);
        providerHeld = false;
        await final.WaitAsync(TimeSpan.FromSeconds(20));
    }
    finally
    {
        if (providerHeld)
            Monitor.Exit(providerGate);
        coordinator.Dispose();
    }
}

// 2) TryLoadWorkspace must release GlobalStateGate before entering directory/payload
// work. Holding WorkspaceDirectoryGate forces the load to pause at its read reservation;
// the global state gate must remain independently available.
{
    string video = Path.Combine(Path.GetTempPath(), $"faceshield-p2-load-{token}.mp4");
    var store = new WorkspaceStateStore();
    using (var source = new FrameMaskProvider())
    {
        source.SetFaceRects(3, new[] { new Rect(2, 2, 8, 8) }, new PixelSize(32, 32));
        store.SaveWorkspace(Snapshot(video, 303), source);
    }

    object directoryGate = StaticField(typeof(WorkspaceStateStore), "WorkspaceDirectoryGate");
    object globalGate = StaticField(typeof(WorkspaceStateStore), "GlobalStateGate");
    bool directoryHeld = false;
    using var loadedProvider = new FrameMaskProvider();
    try
    {
        Monitor.Enter(directoryGate, ref directoryHeld);
        Task<(bool Ok, WorkspaceSnapshot? Snapshot)> load = Task.Run(() =>
        {
            bool ok = store.TryLoadWorkspace(video, WorkspaceMode.Manual, loadedProvider, out WorkspaceSnapshot? state);
            return (ok, state);
        });

        await Task.Delay(200);
        if (load.IsCompleted)
            throw new Exception("TryLoadWorkspace did not wait for its directory read reservation.");

        bool globalEntered = Monitor.TryEnter(globalGate, 1000);
        if (!globalEntered)
            throw new Exception("TryLoadWorkspace held GlobalStateGate while blocked before payload load.");
        Monitor.Exit(globalGate);

        Monitor.Exit(directoryGate);
        directoryHeld = false;
        var result = await load.WaitAsync(TimeSpan.FromSeconds(20));
        if (!result.Ok || result.Snapshot?.SelectedFrameIndex != 303)
            throw new Exception("TryLoadWorkspace failed after releasing the directory reservation.");
    }
    finally
    {
        if (directoryHeld)
            Monitor.Exit(directoryGate);
    }
}

// 3) Directory read reservations are reference-counted. One reader releasing a
// shared generation must not remove another reader/save's protection.
{
    var store = new WorkspaceStateStore();
    Type type = typeof(WorkspaceStateStore);
    MethodInfo register = type.GetMethod("RegisterActiveWorkspaceDirectory", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new Exception("Missing RegisterActiveWorkspaceDirectory");
    MethodInfo unregister = type.GetMethod("UnregisterActiveWorkspaceDirectory", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new Exception("Missing UnregisterActiveWorkspaceDirectory");
    MethodInfo hasActive = type.GetMethod("HasActiveWorkspaceDirectoryUnderLocked", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new Exception("Missing HasActiveWorkspaceDirectoryUnderLocked");
    object directoryGate = StaticField(type, "WorkspaceDirectoryGate");
    string dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"faceshield-p2-reservation-{token}"));
    register.Invoke(null, new object[] { dir });
    register.Invoke(null, new object[] { dir });
    unregister.Invoke(null, new object[] { dir });
    lock (directoryGate)
    {
        if (!(bool)(hasActive.Invoke(null, new object[] { dir }) ?? false))
            throw new Exception("One unregister released a still-shared directory reservation.");
    }
    unregister.Invoke(null, new object[] { dir });
    lock (directoryGate)
    {
        if ((bool)(hasActive.Invoke(null, new object[] { dir }) ?? false))
            throw new Exception("Directory reservation remained active after final release.");
    }
}

// 4) Cross-platform runtime stress for persistence WriteableBitmap.Save while the
// same immutable stored bitmap is concurrently read and then retired/replaced.
{
    string video = Path.Combine(Path.GetTempPath(), $"faceshield-p2-bitmap-{token}.mp4");
    var store = new WorkspaceStateStore();
    using var provider = new FrameMaskProvider();
    var coordinator = new WorkspacePersistenceCoordinator(store, provider);
    provider.SetMask(0, CreateMask(1));

    MethodInfo createPersistenceSnapshot = typeof(FrameMaskProvider).GetMethod(
        "CreatePersistenceSnapshot",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("Missing CreatePersistenceSnapshot");

    for (int iteration = 0; iteration < 8; iteration++)
    {
        WriteableBitmap stored = provider.GetMaskEntries().Single(entry => entry.Key == 0).Value;
        using var lease = (IDisposable)(createPersistenceSnapshot.Invoke(
            provider,
            new object[] { CancellationToken.None })
            ?? throw new Exception("Could not acquire persistence lease"));

        Task save = coordinator.QueueSaveAsync(Snapshot(video, 400 + iteration));
        Task directReads = Task.Run(() =>
        {
            for (int i = 0; i < 8; i++)
            {
                using var ms = new MemoryStream();
                stored.Save(ms);
                if (ms.Length <= 8)
                    throw new Exception("Concurrent stored bitmap Save produced an empty image.");
            }
        });
        Task providerReads = Task.Run(() =>
        {
            for (int i = 0; i < 8; i++)
            {
                using WriteableBitmap? clone = provider.GetFinalMask(0);
                if (clone == null)
                    throw new Exception("Concurrent provider read lost the stored mask.");
                using var ms = new MemoryStream();
                clone.Save(ms);
                if (ms.Length <= 8)
                    throw new Exception("Concurrent cloned bitmap Save produced an empty image.");
            }
        });

        // QueueSaveAsync has already captured its lease. Retire the same bitmap while
        // persistence/direct reads are active, then install a new immutable bitmap.
        provider.SetMask(0, CreateMask(100 + iteration));
        await Task.WhenAll(save, directReads, providerReads).WaitAsync(TimeSpan.FromSeconds(30));
    }

    await coordinator.FlushAsync();
    coordinator.Dispose();

    using var reloaded = new FrameMaskProvider();
    if (!store.TryLoadWorkspace(video, WorkspaceMode.Manual, reloaded, out WorkspaceSnapshot? state) || state == null)
        throw new Exception("Could not reload bitmap persistence stress workspace.");
    using WriteableBitmap? finalMask = reloaded.GetFinalMask(0);
    if (finalMask == null)
        throw new Exception("Reloaded bitmap persistence stress workspace has no mask.");
    using var finalPng = new MemoryStream();
    finalMask.Save(finalPng);
    if (finalPng.Length <= 8)
        throw new Exception("Reloaded persisted bitmap is not encodable.");
}

Console.WriteLine("[PersistenceP2Harness] PASS saveNowGateHold=false loadGlobalGateReleased=true refCountedReservations=true bitmapConcurrentSave=true");
'@
Set-Content -Path (Join-Path $temp 'Program.cs') -Value $program -Encoding utf8

try {
    dotnet run --project (Join-Path $temp 'Harness.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Persistence P2 harness failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
