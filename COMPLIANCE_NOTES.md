# Compliance notes — excluded installer files (§3.1 re-audit, 2026-07-19)

MPL 2.0 "Covered Software" (§1.4) is the source form to which the MPL notice is attached.
Each modified upstream file under `Setup/`, `SetupBundle/`, `SetupCef/` was tested for an MPL
notice in its **upstream** version (at fork base `c71cbcae`):

    git show c71cbcae:<path> | grep -iE 'mozilla public license|mozilla.org/MPL'

**Published** (upstream carries an MPL notice → Covered Software):

* `Setup/Resources/License.rtf` — the installer's license document (contains the MPL 2.0 notice).

**Not published** (no MPL notice upstream → header-less WiX/installer/build scaffolding, not
Covered Software; these are packaging config and binary resources, not licensed source form):

* `Setup/Components/Application.wxs`
* `Setup/Components/Application.xslt`
* `Setup/Components/Configuration.wxs`
* `Setup/Components/Reset.wxs`
* `Setup/Components/Service.wxs`
* `Setup/Components/Service.xslt`
* `Setup/Directories.wxs`
* `Setup/Product.wxs`
* `Setup/Resources/Application.ico`
* `Setup/Resources/Banner.bmp`
* `Setup/Resources/ConfigurationFile.ico`
* `Setup/Resources/ConfigurationTool.ico`
* `Setup/Resources/Dialog.bmp`
* `Setup/Resources/ResetUtility.ico`
* `Setup/Setup.wixproj`
* `Setup/Shortcuts.wxs`
* `SetupBundle/Bundle.wxs`
* `SetupBundle/Resources/Logo.png`
* `SetupBundle/Resources/Theme.wxl`
* `SetupBundle/SetupBundle.wixproj`

(Blinkered-original and non-source trees — `docs/`, `dev/`, `tools/`,
`SafeExamBrowser.BlinkeredAgent/`, `launch-reveal-recording/`, `.github/`, and the new `SetupCef/`
project — are not upstream Covered Software and are out of scope for §3.1.)
