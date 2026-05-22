param(
    [Parameter(Mandatory = $true)]
    [string]$ModelPath
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$model = (Resolve-Path $ModelPath).Path
$work = Join-Path $repo (".tmp\onnx-inspect\inspect-" + [guid]::NewGuid().ToString("N"))
$project = Join-Path $work "OnnxInspect.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 $project

@'
using System;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

string model = args[0];
using var session = new InferenceSession(model);

foreach (var input in session.InputMetadata)
    Console.WriteLine($"[Input] name={input.Key}, dims={string.Join("x", input.Value.Dimensions)}");

foreach (var output in session.OutputMetadata)
    Console.WriteLine($"[OutputMeta] name={output.Key}, dims={string.Join("x", output.Value.Dimensions)}");

var inputName = session.InputMetadata.Keys.First();
var dims = session.InputMetadata[inputName].Dimensions;
int height = dims.Length > 2 && dims[2] > 0 ? dims[2] : 640;
int width = dims.Length > 3 && dims[3] > 0 ? dims[3] : 640;
var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });

using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
foreach (var result in results)
{
    var tensorResult = result.AsTensor<float>();
    var values = tensorResult.ToArray();
    float min = values.Length == 0 ? 0 : values.Min();
    float max = values.Length == 0 ? 0 : values.Max();
    string sample = string.Join(",", values.Take(12).Select(v => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
    Console.WriteLine($"[Output] name={result.Name}, dims={string.Join("x", tensorResult.Dimensions.ToArray())}, len={values.Length}, min={min:0.####}, max={max:0.####}, first={sample}");
}
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project -- $model
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
