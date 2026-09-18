#!/usr/bin/env python3
"""Align static hardening assertions with the shared, locked mask write path."""
from pathlib import Path
path = Path(__file__).resolve().parent / "verify-runtime-hardening.ps1"
text = path.read_text(encoding="utf-8")
old = "Assert-Match \"stored-mask writes serialize both stores\" $frameMaskProvider 'public\\s+void\\s+SetMask\\([\\s\\S]{0,300}lock\\s*\\(_stateGate\\)[\\s\\S]{0,700}_masks[\\s\\S]{0,300}_faceMasks'"
new = """Assert-Match "editor composite uses shared locked write path" $frameMaskProvider 'public\\s+void\\s+SetMask\\([\\s\\S]{0,150}SetMaskCore\\(frameIndex,\\s*mask,\\s*editorComposite:\\s*true\\)'
Assert-Match "trusted manual continuation bypasses Auto stripping" $frameMaskProvider 'internal\\s+void\\s+SetIndependentManualMask\\([\\s\\S]{0,160}SetMaskCore\\(frameIndex,\\s*mask,\\s*editorComposite:\\s*false\\)'
Assert-Match "both mask writes serialize under the state gate" $frameMaskProvider 'private\\s+void\\s+SetMaskCore\\([\\s\\S]{0,220}lock\\s*\\(_stateGate\\)[\\s\\S]{0,900}_faceMasks[\\s\\S]{0,400}_masks'"""
if text.count(old) != 1:
    raise RuntimeError(f"Expected exactly one old stored-mask assertion, found {text.count(old)}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
print("PASS: replaced obsolete in-method lock assertion with shared locked-write checks")
