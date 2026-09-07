$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path $PSScriptRoot '.tmp-remove-workspace-race-harness'
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
    <ProjectReference Include="..\..\FaceShield.csproj" />
  </ItemGroup>
</Project>
'@
Set-Content -Path (Join-Path $temp 'Harness.csproj') -Value $csproj -Encoding utf8

$program = @'
using FaceShield.Enums.Workspace;
using FaceShield.Services.Video;
using FaceShield.Services.Workspace;
using System.Reflection;

static WorkspaceSnapshot Snapshot(string videoPath, WorkspaceMode mode, int frame) => new(
    videoPath,
    mode,
    frame,
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

static object StaticField(Type type, string name)
    => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException($"Missing static field {name}");

string token = Guid.NewGuid().ToString("N");
string videoPath = Path.Combine(Path.GetTempPath(), $"faceshield-remove-race-{token}.mp4");
Type storeType = typeof(WorkspaceStateStore);
var store = new WorkspaceStateStore();
using var provider = new FrameMaskProvider();

string BaseDir(string path) => Path.GetFullPath((string)(
    InvokeInstance(store, "GetWorkspaceBaseDir", path)
    ?? throw new Exception("Could not resolve workspace base directory.")));

bool TryDeleteBase(string path, string directory) => (bool)(
    InvokeInstance(store, "TryDeleteWorkspaceBaseDirectoryIfUnreferenced", path, directory)
    ?? false);

// A base directory must not be deleted while any mode in primary/backup state
// still references this video path.
store.SaveWorkspace(Snapshot(videoPath, WorkspaceMode.Manual, 11), provider);
store.SaveWorkspace(Snapshot(videoPath, WorkspaceMode.Auto, 22), provider);
string baseDir = BaseDir(videoPath);
if (!Directory.Exists(baseDir))
    throw new Exception("Expected workspace base directory after saves.");
if (TryDeleteBase(videoPath, baseDir) || !Directory.Exists(baseDir))
    throw new Exception("Base cleanup deleted a directory referenced by workspace state.");

// Normal explicit removal should remove all modes and then delete the payload base.
store.RemoveWorkspacesForPath(videoPath);
if (Directory.Exists(baseDir))
    throw new Exception("Explicit workspace removal left an unreferenced base directory.");

// An already-active preparation below the base must block deletion.
string activeVideo = videoPath + ".active";
string activeBase = BaseDir(activeVideo);
string activeChild = Path.GetFullPath(Path.Combine(activeBase, "Manual-active-test"));
Directory.CreateDirectory(activeChild);
InvokeStatic(storeType, "RegisterActiveWorkspaceDirectory", activeChild);
try
{
    if (TryDeleteBase(activeVideo, activeBase) || !Directory.Exists(activeBase))
        throw new Exception("Base cleanup crossed an active preparation.");
}
finally
{
    InvokeStatic(storeType, "UnregisterActiveWorkspaceDirectory", activeChild);
}
if (!TryDeleteBase(activeVideo, activeBase) || Directory.Exists(activeBase))
    throw new Exception("Unreferenced inactive base directory was not deleted.");

// Deterministically prove the base deletion reservation prevents a new save
// registration from crossing the final state-reference check/delete boundary.
string reservedVideo = videoPath + ".reserved";
string reservedBase = BaseDir(reservedVideo);
string reservedChild = Path.GetFullPath(Path.Combine(reservedBase, "Manual-reservation-test"));
Directory.CreateDirectory(reservedChild);
object globalGate = StaticField(storeType, "GlobalStateGate");
object directoryGate = StaticField(storeType, "WorkspaceDirectoryGate");
bool globalHeld = false;
try
{
    Monitor.Enter(globalGate, ref globalHeld);
    Task<bool> deleteTask = Task.Run(() => TryDeleteBase(reservedVideo, reservedBase));

    bool observedReservation = false;
    for (int i = 0; i < 400; i++)
    {
        if (!Monitor.TryEnter(directoryGate, 5))
        {
            observedReservation = true;
            break;
        }
        Monitor.Exit(directoryGate);
        Thread.Sleep(5);
    }
    if (!observedReservation)
        throw new Exception("Base cleanup did not reserve WorkspaceDirectoryGate before final reference check.");

    Task registerTask = Task.Run(() => InvokeStatic(
        storeType,
        "RegisterActiveWorkspaceDirectory",
        reservedChild));
    Thread.Sleep(150);
    if (registerTask.IsCompleted)
        throw new Exception("New save registration crossed the base deletion reservation.");

    Monitor.Exit(globalGate);
    globalHeld = false;

    if (!deleteTask.Wait(TimeSpan.FromSeconds(10)) || !deleteTask.Result)
        throw new Exception("Reserved base cleanup did not complete successfully.");
    if (!registerTask.Wait(TimeSpan.FromSeconds(10)))
        throw new Exception("Blocked save registration did not resume after deletion boundary.");
    InvokeStatic(storeType, "UnregisterActiveWorkspaceDirectory", reservedChild);

    if (Directory.Exists(reservedBase))
        throw new Exception("Reserved base directory still exists after successful deletion.");
}
finally
{
    if (globalHeld)
        Monitor.Exit(globalGate);
    try { InvokeStatic(storeType, "UnregisterActiveWorkspaceDirectory", reservedChild); } catch { }
}

Console.WriteLine("[RemoveWorkspaceRaceHarness] PASS");
'@
Set-Content -Path (Join-Path $temp 'Program.cs') -Value $program -Encoding utf8

try {
    dotnet run --project (Join-Path $temp 'Harness.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Remove workspace race harness failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
