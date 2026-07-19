/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Threading;
using SafeExamBrowser.Communication.Contracts.Proxies;
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.Core.Contracts.ResponsibilityModel;
using SafeExamBrowser.Runtime.Responsibilities;
using SafeExamBrowser.WindowsApi.Contracts;

namespace SafeExamBrowser.Runtime
{
	/// <summary>
	/// Holds all configuration and session data for the runtime.
	/// </summary>
	internal class RuntimeContext
	{
		/// <summary>
		/// #4 launch speedup — reveal gate. Signalled when the client process reports ready (or terminated early — see
		/// <see cref="ClientTerminated"/>, which fires it too so the ready-wait never hangs on a dead client). The client
		/// is spawned EARLY to overlap its cold-start with the runtime lockdown, so this can now be set before the lockdown
		/// is applied — hence the gate ANDs it with <see cref="LockdownApplied"/>. Reset per session by ClientStartOperation.
		/// </summary>
		internal ManualResetEvent ClientReady { get; } = new ManualResetEvent(false);

		/// <summary>
		/// #4 launch speedup — reveal gate. Signalled after the LAST runtime lockdown operation (ServiceOperation) has been
		/// applied. The kiosk-desktop reveal (ActivateCustomDesktopOperation) requires BOTH this AND <see cref="ClientReady"/>,
		/// so an early-spawned client is NEVER revealed before the lockdown is applied. Reset per session by ClientStartOperation.
		/// </summary>
		internal ManualResetEvent LockdownApplied { get; } = new ManualResetEvent(false);

		/// <summary>
		/// Blinkered launch reveal — signalled when the client reports the first-site content has finished rendering (CEF
		/// LoadEnd, via InformContentReady). For a CreateNewDesktop (lock) launch, ActivateCustomDesktopOperation waits on
		/// this (with a fallback timeout) before switching to the custom desktop, so the switch reveals PAINTED content
		/// rather than a white about:blank — CEF loads/fires LoadEnd even while the target desktop is still inactive. NOT a
		/// security gate (the reveal still ANDs LockdownApplied + ClientReady). Reset per session by ClientStartOperation.
		/// </summary>
		internal ManualResetEvent ContentReady { get; } = new ManualResetEvent(false);

		/// <summary>
		/// #4 launch speedup — set if the client process terminated before signalling ready, so the ready-wait fails
		/// instead of proceeding on a dead client. Reset per session by ClientStartOperation.
		/// </summary>
		internal bool ClientTerminated { get; set; }

		/// <summary>
		/// #A launch speedup — named, per-session event that gates ONLY the client's browser-init TIMING (created unsignaled
		/// before the client spawn, signalled once the runtime lockdown has been applied). This is a timing hint, NOT a
		/// security gate: the reveal remains the in-process AND of <see cref="LockdownApplied"/> + <see cref="ClientReady"/>.
		/// Disposed/recreated per session by ClientStartOperation.
		/// </summary>
		internal System.Threading.EventWaitHandle BrowserInitGate { get; set; }

		/// <summary>
		/// The currently running client process.
		/// </summary>
		internal IProcess ClientProcess { get; set; }

		/// <summary>
		/// The communication proxy for the currently running client process.
		/// </summary>
		internal IClientProxy ClientProxy { get; set; }

		/// <summary>
		/// The configuration of the currently active session.
		/// </summary>
		internal SessionConfiguration Current { get; set; }

		/// <summary>
		/// The configuration of the next session to be activated.
		/// </summary>
		internal SessionConfiguration Next { get; set; }

		/// <summary>
		/// The path of the configuration file to be used for reconfiguration.
		/// </summary>
		internal string ReconfigurationFilePath { get; set; }

		/// <summary>
		/// The original URL from where the configuration file was downloaded.
		/// </summary>
		internal string ReconfigurationUrl { get; set; }

		/// <summary>
		/// The runtime responsibilities.
		/// </summary>
		internal IResponsibilityCollection<RuntimeTask> Responsibilities { get; set; }
	}
}
