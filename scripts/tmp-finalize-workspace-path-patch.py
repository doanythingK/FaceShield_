from pathlib import Path

p = Path('scripts/tmp-fix-workspace-path-identity.py')
text = p.read_text(encoding='utf-8')

old_check = '''if "FilePathComparison" in home or "FilePathComparer" in home:\n    raise RuntimeError("home still contains duplicate path identity policy")\nhome_path.write_text(home, encoding="utf-8")'''
new_check = '''home = replace_once(\n    home,\n    'new HashSet<string>(FilePathComparer)',\n    'new HashSet<string>(WorkspacePathIdentity.Comparer)',\n    'home non-workspace path comparer')\nif "FilePathComparison" in home or "FilePathComparer" in home:\n    raise RuntimeError("home still contains duplicate path identity policy")\nhome_path.write_text(home, encoding="utf-8")'''
if old_check not in text:
    raise RuntimeError('final path comparer patch target not found')
text = text.replace(old_check, new_check, 1)

old_reopen = '''home = replace_once(\n    home,\n    \'\'\'            var vm = new WorkspaceViewModel(\\n                SelectedVideoPath,\'\'\',\n    \'\'\'            // A removed path cannot save again until a fresh workspace instance\\n            // explicitly reopens its process-local persistence identity.\\n            _stateStore.ReopenWorkspacePath(workspacePath);\\n\\n            var vm = new WorkspaceViewModel(\\n                workspacePath,\'\'\',\n    "home explicit reopen")'''
new_reopen = '''home = replace_once(\n    home,\n    \'\'\'            var vm = new WorkspaceViewModel(\\n                SelectedVideoPath,\'\'\',\n    \'\'\'            var vm = new WorkspaceViewModel(\\n                workspacePath,\'\'\',\n    "home normalized workspace construction")\n\nhome = replace_once(\n    home,\n    \'\'\'            vm.RestoreFromStore(_stateStore);\\n            vm.ToolPanel.BlurRadius = BlurRadius;\'\'\',\n    \'\'\'            vm.RestoreFromStore(_stateStore);\\n            // Reopen only after a fresh workspace instance has been constructed and\\n            // restored successfully. Constructor/restore failure must leave the\\n            // removal tombstone intact.\\n            _stateStore.ReopenWorkspacePath(workspacePath);\\n            vm.ToolPanel.BlurRadius = BlurRadius;\'\'\',\n    "home explicit reopen after construction")'''
if old_reopen not in text:
    raise RuntimeError('explicit reopen patch target not found')
text = text.replace(old_reopen, new_reopen, 1)

p.write_text(text, encoding='utf-8')
print('[WorkspaceIdentityFinalize] PASS')
