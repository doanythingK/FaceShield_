$ErrorActionPreference = 'Stop'

$temp = Join-Path $PSScriptRoot '.tmp-persistence-p1-harness'
Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $temp | Out-Null

$csproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../FaceShield.csproj" />
  </ItemGroup>
</Project>
'@
Set-Content -Path (Join-Path $temp 'Harness.csproj') -Value $csproj -Encoding utf8

$program = @'
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

static T InstanceField<T>(object target, string name) where T : class
    => (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
        ?? throw new InvalidOperationException($"Missing instance field {name}"));

static object StaticField(Type type, string name)
    => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException($"Missing static field {name}");

static object? InvokeInstance(object target, string name, params object?[] args)
{
    MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing instance method {name}");
    return method.Invoke(target, args);
}

static void InvokeStatic(Type type, string name, params object?[] args)
{
    MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing static method {name}");
    method.Invoke(null, args);
}

static bool ReadBoolField(object target, string name)
{
    var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing bool field {name}");
    return (bool)(field.GetValue(target) ?? false);
}

static (bool Acquired, IDisposable? Guard) AcquireSingleInstance(Type guardType)
{
    MethodInfo method = guardType.GetMethod("TryAcquire", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing SingleInstanceGuard.TryAcquire");
    object?[] args = [null];
    bool acquired = (bool)(method.Invoke(null, args) ?? false);
    return (acquired, args[0] as IDisposable);
}

// Cross-platform runtime check for the single-instance primitive. This catches
// platform-unsupported primitives such as FileStream.Lock on macOS.
{
    Type guardType = typeof(WorkspaceStateStore).Assembly.GetType(
        "FaceShield.Services.Application.SingleInstanceGuard")
        ?? throw new Exception("SingleInstanceGuard type not found.");

    var first = AcquireSingleInstance(guardType);
    if (!first.Acquired || first.Guard == null)
        throw new Exception("First single-instance acquisition failed.");

    try
    {
        var second = AcquireSingleInstance(guardType);
        if (second.Acquired)
        {
            second.Guard?.Dispose();
            throw new Exception("Second single-instance acquisition unexpectedly succeeded.");
        }
    }
    finally
    {
        first.Guard.Dispose();
    }

    var third = AcquireSingleInstance(guardType);
    if (!third.Acquired || third.Guard == null)
        throw new Exception("Single-instance acquisition did not recover after release.");
    third.Guard.Dispose();
}

string token = Guid.NewGuid().ToString("N");
string videoPath = Path.Combine(Path.GetTempPath(), $"faceshield-persistence-{token}.mp4");

// SaveNow must publish a terminal boundary, reject later queued saves, drain the
// predecessor, and persist the final snapshot before returning.
{
    var store = new WorkspaceStateStore();
    using var provider = new FrameMaskProvider();
    var coordinator = new WorkspacePersistenceCoordinator(store, provider);
    SemaphoreSlim gate = InstanceField<SemaphoreSlim>(coordinator, "_saveGate");
    gate.Wait();

    Task earlier = coordinator.QueueSaveAsync(Snapshot(videoPath, 11));
    Task final = Task.Run(() => coordinator.SaveNow(Snapshot(videoPath, 22)));

    if (!SpinWait.SpinUntil(() => ReadBoolField(coordinator, "_finalizing"), TimeSpan.FromSeconds(5)))
        throw new Exception("SaveNow did not enter finalizing state.");

    bool rejected = false;
    try
    {
        _ = coordinator.QueueSaveAsync(Snapshot(videoPath, 33));
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }

    if (!rejected)
        throw new Exception("Queued save was accepted after SaveNow finalization started.");
    if (final.IsCompleted)
        throw new Exception("SaveNow returned while the persistence gate was deliberately blocked.");

    gate.Release();
    await final.WaitAsync(TimeSpan.FromSeconds(20));
    await earlier.WaitAsync(TimeSpan.FromSeconds(20));

    using var loadedMasks = new FrameMaskProvider();
    if (!store.TryLoadWorkspace(videoPath, WorkspaceMode.Manual, loadedMasks, out WorkspaceSnapshot? loaded) ||
        loaded == null || loaded.SelectedFrameIndex != 22)
    {
        throw new Exception("SaveNow did not persist the terminal snapshot.");
    }

    coordinator.Dispose();
}

// Dispose must wait for the published completion tail instead of disposing the
// semaphore while a worker is still pending.
{
    string disposeVideo = videoPath + ".dispose";
    var store = new WorkspaceStateStore();
    using var provider = new FrameMaskProvider();
    var coordinator = new WorkspacePersistenceCoordinator(store, provider);
    SemaphoreSlim gate = InstanceField<SemaphoreSlim>(coordinator, "_saveGate");
    gate.Wait();
    Task pending = coordinator.QueueSaveAsync(Snapshot(disposeVideo, 44));
    Task disposing = Task.Run(coordinator.Dispose);
    await Task.Delay(150);
    if (disposing.IsCompleted)
        throw new Exception("Dispose returned before a blocked published save completed.");
    gate.Release();
    await disposing.WaitAsync(TimeSpan.FromSeconds(20));
    await pending.WaitAsync(TimeSpan.FromSeconds(20));
}

// Directory cleanup must keep referenced/active generations and delete only an
// unreferenced inactive candidate.
{
    string cleanupVideo = videoPath + ".cleanup";
    var store = new WorkspaceStateStore();
    using var provider = new FrameMaskProvider();
    store.SaveWorkspace(Snapshot(cleanupVideo, 55), provider);

    string baseDir = (string)(InvokeInstance(store, "GetWorkspaceBaseDir", cleanupVideo)
        ?? throw new Exception("Could not resolve workspace base directory."));
    string[] referencedDirs = Directory.Exists(baseDir)
        ? Directory.GetDirectories(baseDir, "Manual-*")
        : Array.Empty<string>();
    if (referencedDirs.Length == 0)
        throw new Exception("Expected a committed workspace generation directory.");

    foreach (string referenced in referencedDirs)
    {
        bool deleted = (bool)(InvokeInstance(
            store,
            "TryDeleteWorkspaceDirectoryIfUnreferenced",
            cleanupVideo,
            WorkspaceMode.Manual,
            Path.GetFullPath(referenced)) ?? false);
        if (deleted || !Directory.Exists(referenced))
            throw new Exception("Cleanup deleted a state/backup referenced generation.");
    }

    Type storeType = typeof(WorkspaceStateStore);
    string activeCandidate = Path.GetFullPath(Path.Combine(baseDir, "Manual-active-test"));
    Directory.CreateDirectory(activeCandidate);
    InvokeStatic(storeType, "RegisterActiveWorkspaceDirectory", activeCandidate);
    try
    {
        bool deletedWhileActive = (bool)(InvokeInstance(
            store,
            "TryDeleteWorkspaceDirectoryIfUnreferenced",
            cleanupVideo,
            WorkspaceMode.Manual,
            activeCandidate) ?? false);
        if (deletedWhileActive || !Directory.Exists(activeCandidate))
            throw new Exception("Cleanup deleted an active preparation directory.");
    }
    finally
    {
        InvokeStatic(storeType, "UnregisterActiveWorkspaceDirectory", activeCandidate);
    }

    bool deletedAfterInactive = (bool)(InvokeInstance(
        store,
        "TryDeleteWorkspaceDirectoryIfUnreferenced",
        cleanupVideo,
        WorkspaceMode.Manual,
        activeCandidate) ?? false);
    if (!deletedAfterInactive || Directory.Exists(activeCandidate))
        throw new Exception("Cleanup failed to delete an unreferenced inactive directory.");

    // Deterministically prove that cleanup reserves WorkspaceDirectoryGate while
    // blocked on the final GlobalStateGate reference check. A new active registration
    // must not cross that deletion boundary.
    string reservedCandidate = Path.GetFullPath(Path.Combine(baseDir, "Manual-reservation-test"));
    Directory.CreateDirectory(reservedCandidate);
    object globalGate = StaticField(storeType, "GlobalStateGate");
    object directoryGate = StaticField(storeType, "WorkspaceDirectoryGate");
    using var globalLocked = new ManualResetEventSlim(false);
    using var releaseGlobal = new ManualResetEventSlim(false);

    Task globalHolder = Task.Run(() =>
    {
        lock (globalGate)
        {
            globalLocked.Set();
            if (!releaseGlobal.Wait(TimeSpan.FromSeconds(10)))
                throw new Exception("Timed out waiting to release GlobalStateGate test hold.");
        }
    });

    if (!globalLocked.Wait(TimeSpan.FromSeconds(5)))
        throw new Exception("Could not establish GlobalStateGate test hold.");

    Task<bool>? deleteTask = null;
    Task? registerTask = null;
    try
    {
        deleteTask = Task.Run(() => (bool)(InvokeInstance(
            store,
            "TryDeleteWorkspaceDirectoryIfUnreferenced",
            cleanupVideo,
            WorkspaceMode.Manual,
            reservedCandidate) ?? false));

        bool observedDirectoryReservation = false;
        for (int i = 0; i < 200; i++)
        {
            if (!Monitor.TryEnter(directoryGate, 5))
            {
                observedDirectoryReservation = true;
                break;
            }
            Monitor.Exit(directoryGate);
            await Task.Delay(5);
        }
        if (!observedDirectoryReservation)
            throw new Exception("Cleanup did not reserve WorkspaceDirectoryGate before state recheck.");

        registerTask = Task.Run(() => InvokeStatic(
            storeType,
            "RegisterActiveWorkspaceDirectory",
            reservedCandidate));
        await Task.Delay(100);
        if (registerTask.IsCompleted)
            throw new Exception("Active registration crossed the cleanup deletion reservation.");

        releaseGlobal.Set();
        await globalHolder.WaitAsync(TimeSpan.FromSeconds(10));
        if (!await deleteTask.WaitAsync(TimeSpan.FromSeconds(10)))
            throw new Exception("Reserved cleanup did not delete the unreferenced candidate.");
        await registerTask.WaitAsync(TimeSpan.FromSeconds(10));
        InvokeStatic(storeType, "UnregisterActiveWorkspaceDirectory", reservedCandidate);
        if (Directory.Exists(reservedCandidate))
            throw new Exception("Reserved cleanup candidate still exists after successful deletion.");
    }
    finally
    {
        releaseGlobal.Set();
        try { await globalHolder.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        if (registerTask != null)
        {
            try { await registerTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
        try { InvokeStatic(storeType, "UnregisterActiveWorkspaceDirectory", reservedCandidate); } catch { }
    }
}

Console.WriteLine("[PersistenceP1Harness] PASS");
'@
Set-Content -Path (Join-Path $temp 'Program.cs') -Value $program -Encoding utf8

try {
    dotnet run --project (Join-Path $temp 'Harness.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Persistence P1 harness failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
