# Blinkered (Windows) — Modified Safe Exam Browser source

This repository is the **MPL 2.0 modified-source disclosure** for **Blinkered**, a Larger Work
(MPL 2.0 §3.3) built on [Safe Exam Browser](https://github.com/SafeExamBrowser/seb-win-refactoring)
(© ETH Zürich, IT Services — MPL 2.0).

It contains **only** the Safe Exam Browser source files that Blinkered modified, plus new files
derived from upstream MPL source. Blinkered's own proprietary files are **not** included.

* Upstream: `SafeExamBrowser/seb-win-refactoring`
* Fork base: `c71cbcae`
* Each release is one immutable snapshot tag `vX.Y.Z.B` with no history.
* This tag: `v1.0.0.86-r2`

**Supersession note (2026-07-19):** tag `v1.0.0.86-r2` supersedes `v1.0.0.86` for the 1.0.0.86
release. A §3.1 re-audit of the installer directories added one MPL-covered file that the
original snapshot omitted (`Setup/Resources/License.rtf` — the installer's MPL license
document). The earlier `v1.0.0.86` tag is retained unchanged per the immutable-tag rule; the
`main` branch and `-r2` tag carry the complete, corrected set. Excluded header-less installer
files are listed in `COMPLIANCE_NOTES.md`.

Published under the Mozilla Public License 2.0 (see `LICENSE.txt`).
