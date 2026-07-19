/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.Core.Contracts.ResponsibilityModel;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Runtime.Responsibilities;
using SafeExamBrowser.UserInterface.Contracts.Windows;

namespace SafeExamBrowser.Runtime
{
	internal class RuntimeController
	{
		private readonly ILogger logger;
		private readonly IOperationSequence bootstrapSequence;
		private readonly IResponsibilityCollection<RuntimeTask> responsibilities;
		private readonly RuntimeContext runtimeContext;
		private readonly IRuntimeWindow runtimeWindow;
		private readonly ISplashScreen splashScreen;
		private readonly IWindow launchCover;

		private bool SessionIsRunning => runtimeContext.Current != default;

		internal RuntimeController(
			ILogger logger,
			IOperationSequence bootstrapSequence,
			IResponsibilityCollection<RuntimeTask> responsibilities,
			RuntimeContext runtimeContext,
			IRuntimeWindow runtimeWindow,
			ISplashScreen splashScreen,
			IWindow launchCover)
		{
			this.bootstrapSequence = bootstrapSequence;
			this.responsibilities = responsibilities;
			this.logger = logger;
			this.runtimeWindow = runtimeWindow;
			this.runtimeContext = runtimeContext;
			this.splashScreen = splashScreen;
			this.launchCover = launchCover;
		}

		internal bool TryStart()
		{
			logger.Info("Initiating startup procedure...");

			// Blinkered: black the screen FIRST — before the runtime window and the splash — so a windowed Focus launch
			// goes STRAIGHT to black with no home-desktop flash, then the splash + content appear over it. Launch-only:
			// SessionResponsibility tears it down at content-ready (Session Running), so it never persists and can't
			// reintroduce the exit flash. Best-effort; a cover failure must not affect the launch.
			ShowLaunchCover();

			// We need to show the runtime window here already, this way implicitly setting it as the runtime application's main window.
			// Otherwise, the splash screen is considered as the main window and thus the operating system and/or WPF does not correctly
			// activate the runtime window once bootstrapping has finished, which in turn leads to undesired user interface behavior.
			runtimeWindow.Show();
			runtimeWindow.BringToForeground();
			runtimeWindow.SetIndeterminate();

			// Blinkered: the launch cover (black + composited logo) is now the sole launch visual — do NOT also show the
			// small splash card on top of it (that caused the "logo → black → logo" flicker). The splash instance stays
			// alive as the parent for any error message box; it is simply never shown during the launch. The CLIENT's splash
			// is likewise suppressed (Client CompositionRoot creates it with show:false) so it can't activate on top of the
			// cover when the client process starts.

			var initialized = bootstrapSequence.TryPerform() == OperationResult.Success;

			if (initialized)
			{
				responsibilities.Delegate(RuntimeTask.RegisterEvents);

				// #3: keep the splash up past bootstrap. The RuntimeWindow is off-screen (Blinkered shows only the small
				// splash), so the splash is the sole visible launch window; it persists through session start and is
				// hidden once the session is running (SessionResponsibility.HandleSessionStartSuccess), by which point the
				// browser is taking over. Hiding it here would leave a bare desktop during the whole session-start phase.

				logger.Info("Application successfully initialized.");
				logger.Log(string.Empty);
				logger.Subscribe(runtimeWindow);

				// Auto-update is deliberately NOT checked here. This path runs inside the (agent-launched) locked
				// kiosk session, where the WinSparkle "update available" UI is suppressed by the kiosk AND the check
				// disrupts the session launch (alto fails to load). The update check now runs in the Blinkered agent
				// at logon — outside/before the lock — where it can apply silently at a safe moment.
				responsibilities.Delegate(RuntimeTask.StartSession);
				responsibilities.Delegate(RuntimeTask.StartIntegrityMonitoring);
			}
			else
			{
				logger.Info("Application startup aborted!");
				logger.Log(string.Empty);

				responsibilities.Delegate(RuntimeTask.ShowStartupError);
			}

			return initialized && SessionIsRunning;
		}

		internal void Terminate()
		{
			// Fallback: ensure the launch cover is gone before teardown (it is normally torn down at content-ready by
			// SessionResponsibility; this covers a failed/aborted launch that never reached Session Running).
			CloseLaunchCover();

			responsibilities.Delegate(RuntimeTask.StopIntegrityMonitoring);
			responsibilities.Delegate(RuntimeTask.DeregisterEvents);

			if (SessionIsRunning)
			{
				responsibilities.Delegate(RuntimeTask.StopSession);
			}

			logger.Unsubscribe(runtimeWindow);
			runtimeWindow.Close();

			// #3: nothing on exit — do NOT re-show the splash during shutdown teardown. The exit goes straight to
			// whatever is underneath (the Alto tab). The splash instance stays alive (parent for a shutdown-error
			// message box) and is closed below; the exit black-flash is handled separately (window topmost management),
			// not by this cover, so leaving it hidden doesn't reintroduce the flash.

			logger.Log(string.Empty);
			logger.Info("Initiating shutdown procedure...");

			var success = bootstrapSequence.TryRevert() == OperationResult.Success;

			if (success)
			{
				logger.Info("Application successfully finalized.");
				logger.Log(string.Empty);
			}
			else
			{
				logger.Info("Shutdown procedure failed!");
				logger.Log(string.Empty);

				responsibilities.Delegate(RuntimeTask.ShowShutdownError);
			}

			splashScreen.Close();
		}

		private void ShowLaunchCover()
		{
			try
			{
				launchCover?.Show();
				launchCover?.BringToForeground();
			}
			catch (Exception e)
			{
				logger.Warn($"Launch cover: failed to show ({e.Message}) — continuing without it.");
			}
		}

		private void CloseLaunchCover()
		{
			try
			{
				launchCover?.Close();
			}
			catch (Exception e)
			{
				logger.Warn($"Launch cover: failed to close ({e.Message}).");
			}
		}
	}
}
