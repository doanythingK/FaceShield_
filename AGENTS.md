# Repository Guidelines

## Project Structure & Module Organization
FaceShield is a .NET 8 Avalonia desktop app. The root contains `FaceShield.sln`, `FaceShield.csproj`, `Program.cs`, and `App.axaml`. UI markup and code-behind live in `Views/`, with pages in `Views/Pages/`, dialogs in `Views/Dialogs/`, and workspace components in `Views/Workspace/`. View models mirror that structure in `ViewModels/`. Domain data belongs in `Models/` and `Enums/`; reusable UI helpers are in `Controls/` and `Converters/`. Video, analysis, face detection, and workspace logic belong in `Services/`. Runtime assets are in `Assets/`, `FFmpeg/`, and `Native/`; avoid unrelated binary churn.

## Build, Test, and Development Commands
- `dotnet restore`: restore NuGet packages.
- `dotnet build FaceShield.sln`: compile the app for local development.
- `dotnet run --project FaceShield.csproj`: run the Avalonia app locally.
- `dotnet publish FaceShield.csproj -c Release -r win-x64 --self-contained true`: create a Windows release build.
- `bash scripts/prepare-ffmpeg-osx.sh` then `dotnet publish FaceShield.csproj -c Release -r osx-arm64 --self-contained true`: prepare and publish the macOS ARM64 build.

CI workflows in `.github/workflows/` package Windows/macOS artifacts.

## Coding Style & Naming Conventions
Follow the existing C# style: 4-space indentation, block-scoped namespaces, nullable reference types enabled, and braces on their own lines. Use PascalCase for types, methods, and properties; prefix interfaces with `I` (for example, `IFrameAnalyzer`). Pair Avalonia `.axaml` files with `.axaml.cs` code-behind when needed, and align view/view model names, such as `WorkspaceView` and `WorkspaceViewModel`.

## Testing Guidelines
No dedicated test project is currently present. At minimum, run `dotnet build FaceShield.sln` before submitting changes. For video export, FFmpeg loading, ONNX/DirectML, or workspace editing changes, perform a manual smoke test with a short video and verify open, preview, edit, and export behavior. Add future tests under `FaceShield.Tests/`, with classes named after the unit under test.

## Commit & Pull Request Guidelines
Recent history uses concise Conventional Commit-style messages, such as `fix: bundle windows native libs in CI publish` and `feat: improve blur quality/speed pipeline and macOS compatibility`. Prefer `fix:`, `feat:`, `perf:`, or `chore:` followed by an imperative summary.

Pull requests should describe the change, list verification steps, call out platform impact (`win-x64`, `osx-arm64`), and include screenshots or recordings for visible UI changes. Link related issues when available and mention native dependency or packaging changes.

## Security & Configuration Tips
Do not commit local logs, private videos, signing credentials, or machine-specific paths. Treat bundled FFmpeg, ONNX Runtime, DirectML, and macOS dylibs as platform-sensitive assets; update them intentionally and verify the publish output after changes.

## Agent-Specific Instructions
응답과 작업 과정의 소통은 한국어로 한다. 질문 자체에 대한 평가는 하지 않는다.
`네가`, `내가`처럼 직접적인 1인칭/2인칭 지칭 표현을 쓰지 않고, 모든 응답과 작업 과정에서는 존댓말만 사용한다.
- 직접적인 1인칭/2인칭 지칭 표현에는 `네가`, `내가`, `너`, `나`처럼 사용자나 작업자를 직접 가리키는 표현이 포함된다.
- 코드, 로그, 사용자 문장을 직접 인용해야 하는 경우를 제외하고는 반말, 명령조, 직접적인 1인칭/2인칭 지칭 표현을 피한다.
- 사용자와 작업자를 직접 지칭하는 표현 대신 요청, 작업, 코드, 변경 사항 중심으로 설명한다.

- 근거가 충분하지 않거나 정보가 불확실하면 임의로 만들지 말고 `알 수 없습니다` 또는 `잘 모르겠습니다`라고 명시한다.
- 답변 전 가능한 정보를 단계별로 검증하고, 모호하거나 출처가 불분명한 부분은 `확실하지 않음`이라고 표시한다.
- 최종 답변은 확인된 정보만 사용해 간결하게 작성한다. 추측이 불가피하면 `추측입니다`라고 밝힌다.
- 요청이 모호하거나 추가 정보가 필요하면 먼저 사용자에게 맥락이나 세부 정보를 요청한다.
- 확인되지 않은 사실을 단정하지 말고, 필요한 경우 근거를 함께 제시한다.
- 출처나 근거가 있으면 답변에 명시하고, 가능하면 관련 링크나 참고 자료를 짧게 요약한다.
