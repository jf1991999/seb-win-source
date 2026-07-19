/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.Core.Contracts.OperationModel.Events;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Settings.Security;
using SafeExamBrowser.WindowsApi.Contracts;

namespace SafeExamBrowser.Runtime.Operations.Session
{
	internal class KioskModeOperation : SessionOperation
	{
		private readonly IDesktopFactory desktopFactory;
		private readonly IDesktopMonitor desktopMonitor;
		private readonly IExplorerShell explorerShell;
		private readonly IProcessFactory processFactory;

		private KioskMode? activeMode;
		private IDesktop customDesktop;
		private IDesktop originalDesktop;
		private bool customDesktopActivated;

		public override event StatusChangedEventHandler StatusChanged;

		public KioskModeOperation(
			Dependencies dependencies,
			IDesktopFactory desktopFactory,
			IDesktopMonitor desktopMonitor,
			IExplorerShell explorerShell,
			IProcessFactory processFactory) : base(dependencies)
		{
			this.desktopFactory = desktopFactory;
			this.desktopMonitor = desktopMonitor;
			this.explorerShell = explorerShell;
			this.processFactory = processFactory;
		}

		public override OperationResult Perform()
		{
			Logger.Info($"Initializing kiosk mode '{Context.Next.Settings.Security.KioskMode}'...");
			StatusChanged?.Invoke(TextKey.OperationStatus_InitializeKioskMode);

			activeMode = Context.Next.Settings.Security.KioskMode;

			switch (Context.Next.Settings.Security.KioskMode)
			{
				case KioskMode.CreateNewDesktop:
					CreateCustomDesktop();
					break;
				case KioskMode.DisableExplorerShell:
					TerminateExplorerShell();
					break;
			}

			return OperationResult.Success;
		}

		public override OperationResult Repeat()
		{
			var newMode = Context.Next.Settings.Security.KioskMode;

			if (activeMode == newMode)
			{
				Logger.Info($"New kiosk mode '{newMode}' is the same as the currently active mode, skipping re-initialization...");
			}
			else
			{
				Logger.Info($"Switching from kiosk mode '{activeMode}' to '{newMode}'...");
				StatusChanged?.Invoke(TextKey.OperationStatus_InitializeKioskMode);

				switch (activeMode)
				{
					case KioskMode.CreateNewDesktop:
						CloseCustomDesktop();
						break;
					case KioskMode.DisableExplorerShell:
						RestartExplorerShell();
						break;
				}

				activeMode = newMode;

				switch (newMode)
				{
					case KioskMode.CreateNewDesktop:
						CreateCustomDesktop();
						break;
					case KioskMode.DisableExplorerShell:
						TerminateExplorerShell();
						break;
				}
			}

			return OperationResult.Success;
		}

		public override OperationResult Revert()
		{
			Logger.Info($"Reverting kiosk mode '{activeMode}'...");
			StatusChanged?.Invoke(TextKey.OperationStatus_RevertKioskMode);

			switch (activeMode)
			{
				case KioskMode.CreateNewDesktop:
					CloseCustomDesktop();
					break;
				case KioskMode.DisableExplorerShell:
					RestartExplorerShell();
					break;
			}

			return OperationResult.Success;
		}

		private void CreateCustomDesktop()
		{
			originalDesktop = desktopFactory.GetCurrent();
			Logger.Info($"Current desktop is {originalDesktop}.");

			customDesktop = desktopFactory.CreateRandom();
			Logger.Info($"Created custom desktop {customDesktop}.");

			// #3 (Home bridge): create the desktop and make it the client's startup desktop, but DEFER activating it
			// (and starting the desktop monitor) to ActivateCustomDesktop() — called after the client + its splash are
			// up (see ActivateCustomDesktopOperation) — so switching to it shows no black frame. The client launches on
			// this still-inactive desktop and paints its splash there; activating then reveals an already-populated
			// desktop. Monitor start is deferred too: monitoring a not-yet-active desktop would misfire.
			processFactory.StartupDesktop = customDesktop;
			customDesktopActivated = false;
			Logger.Info($"Custom desktop set as client startup desktop; activation deferred until the client is up.");
		}

		/// <summary>
		/// Activates the custom desktop (and starts monitoring it). Deferred from <see cref="CreateCustomDesktop"/> until
		/// AFTER the client and its splash are up, so the CreateNewDesktop switch bridges with no black frame.
		///
		/// TRADEOFF (deliberate, Home-appropriate): because activation waits for the client, the device sits on the
		/// ORIGINAL desktop for the client's ~1s startup before the kiosk lockdown is active. This suits Blinkered's
		/// parental-Home threat model (the parent initiated the lock). If Blinkered ever targets EXAM-GRADE lockdown
		/// against a motivated cheater, revisit: activate inside CreateCustomDesktop (switch-immediately) or make this
		/// deferral config-driven — that ~1s pre-lockdown gap matters there. Idempotent across reconfiguration.
		/// </summary>
		public void ActivateCustomDesktop()
		{
			if (customDesktop != default && !customDesktopActivated)
			{
				customDesktop.Activate();
				customDesktopActivated = true;
				Logger.Info("Activated custom desktop (bridged: after the client is up, so the switch shows no black frame).");

				desktopMonitor.Start(customDesktop);
			}
		}

		private void CloseCustomDesktop()
		{
			desktopMonitor.Stop();
			customDesktopActivated = false;

			if (originalDesktop != default)
			{
				originalDesktop.Activate();
				processFactory.StartupDesktop = originalDesktop;
				Logger.Info($"Switched back to original desktop {originalDesktop}.");
			}
			else
			{
				Logger.Warn($"No original desktop found to activate!");
			}

			if (customDesktop != default)
			{
				customDesktop.Close();
				Logger.Info($"Closed custom desktop {customDesktop}.");
			}
			else
			{
				Logger.Warn($"No custom desktop found to close!");
			}
		}

		private void TerminateExplorerShell()
		{
			StatusChanged?.Invoke(TextKey.OperationStatus_WaitExplorerTermination);

			explorerShell.HideAllWindows();
			explorerShell.Terminate();
		}

		private void RestartExplorerShell()
		{
			StatusChanged?.Invoke(TextKey.OperationStatus_WaitExplorerStartup);

			explorerShell.Start();
			explorerShell.RestoreAllWindows();
		}
	}
}
