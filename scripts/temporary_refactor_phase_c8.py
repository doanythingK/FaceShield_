from pathlib import Path


def read_exact(path: str) -> str:
    with Path(path).open('r', encoding='utf-8', newline='') as f:
        return f.read()


def write_exact(path: str, text: str) -> None:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open('w', encoding='utf-8', newline='') as f:
        f.write(text)


def replace_once(path: str, old: str, new: str) -> None:
    text = read_exact(path)
    candidates = [(old, new)]
    if '\n' in old:
        candidates.append((old.replace('\n', '\r\n'), new.replace('\n', '\r\n')))
    for before, after in candidates:
        count = text.count(before)
        if count == 1:
            write_exact(path, text.replace(before, after, 1))
            return
        if count > 1:
            raise RuntimeError(f'Expected one match in {path}, found {count}: {old[:120]!r}')
    raise RuntimeError(f'Patch target not found in {path}: {old[:160]!r}')


def replace_between(path: str, start_marker: str, end_marker: str, replacement: str) -> None:
    text = read_exact(path)
    candidates = [('\n', start_marker, end_marker, replacement), ('\r\n', start_marker, end_marker, replacement)]
    for sep, start_raw, end_raw, repl_raw in candidates:
        start_key = start_raw.replace('\n', sep)
        end_key = end_raw.replace('\n', sep)
        start = text.find(start_key)
        if start < 0:
            continue
        end = text.find(end_key, start)
        if end < 0:
            continue
        write_exact(path, text[:start] + repl_raw.replace('\n', sep) + text[end:])
        return
    raise RuntimeError(f'Patch range not found in {path}: {start_marker!r} -> {end_marker!r}')


write_exact('Services/Video/VideoExportAttemptCoordinator.cs', '''using System;
using System.Diagnostics;

namespace FaceShield.Services.Video;

internal readonly record struct VideoExportAttemptConfiguration(
    string ExportMode,
    bool ForceSoftwareEncoder,
    bool AllowHybridCopy,
    bool ForceSafeEncoding,
    bool ForceAudioTranscode,
    bool ForceH264Fallback);

internal static class VideoExportAttemptCoordinator
{
    internal static int Execute(
        bool allowHybridCopy,
        Func<bool> isStaticHdrConfigured,
        Action<VideoExportAttemptConfiguration> executeAttempt)
    {
        if (isStaticHdrConfigured == null)
            throw new ArgumentNullException(nameof(isStaticHdrConfigured));
        if (executeAttempt == null)
            throw new ArgumentNullException(nameof(executeAttempt));

        int attemptCount = 0;
        try
        {
            attemptCount++;
            executeAttempt(new VideoExportAttemptConfiguration(
                ExportMode: "primary",
                ForceSoftwareEncoder: false,
                AllowHybridCopy: allowHybridCopy && VideoHybridCopyPolicy.EnableHybridCopyWindow,
                ForceSafeEncoding: false,
                ForceAudioTranscode: false,
                ForceH264Fallback: false));
        }
        catch (InvalidOperationException ex) when (
            VideoExportRetryPolicy.ShouldRetryWithSafeEncoding(ex))
        {
            Debug.WriteLine($"[Export] mode=fallback-safe로 재시도: {ex.Message}");
            try
            {
                attemptCount++;
                executeAttempt(new VideoExportAttemptConfiguration(
                    ExportMode: "fallback-safe",
                    ForceSoftwareEncoder: true,
                    AllowHybridCopy: false,
                    ForceSafeEncoding: true,
                    ForceAudioTranscode: false,
                    ForceH264Fallback: false));
            }
            catch (InvalidOperationException nestedEx) when (
                VideoExportRetryPolicy.ShouldRetryWithH264Fallback(
                    nestedEx,
                    isStaticHdrConfigured()))
            {
                Debug.WriteLine(
                    $"[Export] mode=fallback-h264로 재시도: 안전 모드에서도 실패. {nestedEx.Message}");
                attemptCount++;
                executeAttempt(new VideoExportAttemptConfiguration(
                    ExportMode: "fallback-h264",
                    ForceSoftwareEncoder: true,
                    AllowHybridCopy: false,
                    ForceSafeEncoding: true,
                    ForceAudioTranscode: false,
                    ForceH264Fallback: true));
            }
        }

        return attemptCount;
    }
}
''')

staging_path = 'Services/Video/VideoExportStagingPolicy.cs'
staging = read_exact(staging_path)
insert_marker = '''    internal static void TryDeleteStagedOutput(string stagedOutputPath)\n'''
commit_code = '''    internal static VideoExportOutputCommitResult CommitStagedOutput(\n        string stagedOutputPath,\n        string finalOutputPath,\n        bool allowOverwrite)\n    {\n        if (!File.Exists(stagedOutputPath))\n        {\n            throw new InvalidOperationException(\n                "검증된 임시 출력 파일이 생성되지 않았습니다.");\n        }\n\n        var timer = Stopwatch.StartNew();\n        string mode;\n        if (!allowOverwrite)\n        {\n            try\n            {\n                File.Move(stagedOutputPath, finalOutputPath, overwrite: false);\n                mode = "move-no-overwrite";\n            }\n            catch (IOException ex) when (File.Exists(finalOutputPath))\n            {\n                throw new IOException(\n                    $"내보내기 대상 파일이 작업 중 생성되어 덮어쓰기를 중단했습니다: {finalOutputPath}",\n                    ex);\n            }\n        }\n        else if (File.Exists(finalOutputPath))\n        {\n            File.Replace(\n                stagedOutputPath,\n                finalOutputPath,\n                destinationBackupFileName: null,\n                ignoreMetadataErrors: true);\n            mode = "replace";\n        }\n        else\n        {\n            File.Move(stagedOutputPath, finalOutputPath);\n            mode = "move";\n        }\n\n        timer.Stop();\n        return new VideoExportOutputCommitResult(\n            mode,\n            timer.ElapsedMilliseconds);\n    }\n\n'''
for sep in ('\n', '\r\n'):
    marker = insert_marker.replace('\n', sep)
    if marker in staging:
        staging = staging.replace(marker, commit_code.replace('\n', sep) + marker, 1)
        break
else:
    raise RuntimeError('VideoExportStagingPolicy insertion marker missing')
if 'VideoExportOutputCommitResult' not in staging.split('internal static class VideoExportStagingPolicy')[0]:
    namespace_marker = 'namespace FaceShield.Services.Video;'
    record_text = '''namespace FaceShield.Services.Video;\n\ninternal readonly record struct VideoExportOutputCommitResult(\n    string Mode,\n    long ElapsedMilliseconds);'''
    for sep in ('\n', '\r\n'):
        marker = namespace_marker
        if marker in staging:
            staging = staging.replace(
                marker,
                record_text.replace('\n', sep),
                1)
            break
write_exact(staging_path, staging)

replace_between(
    'Services/Video/VideoExportService.cs',
    '''            try\n            {\n                attemptCount++;\n''',
    '''            if (!File.Exists(stagedOutputPath))\n''',
    '''            attemptCount = VideoExportAttemptCoordinator.Execute(\n                allowHybridCopy,\n                () => _staticHdrConfigured,\n                attempt => ExportInternal(\n                    inputPath,\n                    stagedOutputPath,\n                    blurRadius,\n                    progress,\n                    cancellationToken,\n                    runId,\n                    exportMode: attempt.ExportMode,\n                    forceSoftwareEncoder: attempt.ForceSoftwareEncoder,\n                    allowHybridCopy: attempt.AllowHybridCopy,\n                    forceSafeEncoding: attempt.ForceSafeEncoding,\n                    forceAudioTranscode: attempt.ForceAudioTranscode,\n                    forceH264Fallback: attempt.ForceH264Fallback));\n\n''')

replace_between(
    'Services/Video/VideoExportService.cs',
    '''            var outputCommitTimer = Stopwatch.StartNew();\n''',
    '''            endToEndTimer.Stop();\n''',
    '''            VideoExportOutputCommitResult outputCommit =\n                VideoExportStagingPolicy.CommitStagedOutput(\n                    stagedOutputPath,\n                    finalOutputPath,\n                    allowOutputOverwrite);\n\n''')

replace_once(
    'Services/Video/VideoExportService.cs',
    '''                OutputCommitMs = outputCommitTimer.ElapsedMilliseconds,\n''',
    '''                OutputCommitMs = outputCommit.ElapsedMilliseconds,\n''')
replace_once(
    'Services/Video/VideoExportService.cs',
    '''                $"[ExportCommitted] runId={LastExportSummary.RunId ?? "n/a"}, mode={commitMode}";\n''',
    '''                $"[ExportCommitted] runId={LastExportSummary.RunId ?? "n/a"}, mode={outputCommit.Mode}";\n''')

print('Phase C8 video export outer coordination extraction applied.')
