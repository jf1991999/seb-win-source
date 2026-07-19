/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading;
using SafeExamBrowser.Communication.Contracts.Hosts;
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Settings.Security;
using SafeExamBrowser.UserInterface.Contracts.MessageBox;
using SafeExamBrowser.UserInterface.Contracts.Windows;

namespace SafeExamBrowser.Runtime.Responsibilities
{
	internal class SessionResponsibility : RuntimeResponsibility
	{
		private readonly AppConfig appConfig;
		private readonly IMessageBox messageBox;
		private readonly IRuntimeWindow runtimeWindow;
		private readonly ISplashScreen splashScreen;
		private readonly IWindow launchCover;
		private readonly IRuntimeHost runtimeHost;
		private readonly IRepeatableOperationSequence sessionSequence;
		private readonly Action shutdown;
		private readonly IText text;

		// Launch-cover teardown: held until the client signals the first-site content has rendered (ContentReady), or a
		// fallback timeout — whichever first. Closed exactly once (coverClosed guard).
		private int coverClosed;
		private Timer coverFallbackTimer;

		internal SessionResponsibility(
			AppConfig appConfig,
			ILogger logger,
			IMessageBox messageBox,
			RuntimeContext runtimeContext,
			IRuntimeWindow runtimeWindow,
			ISplashScreen splashScreen,
			IWindow launchCover,
			IRuntimeHost runtimeHost,
			IRepeatableOperationSequence sessionSequence,
			Action shutdown,
			IText text) : base(logger, runtimeContext)
		{
			this.appConfig = appConfig;
			this.messageBox = messageBox;
			this.runtimeWindow = runtimeWindow;
			this.splashScreen = splashScreen;
			this.launchCover = launchCover;
			this.runtimeHost = runtimeHost;
			this.sessionSequence = sessionSequence;
			this.shutdown = shutdown;
			this.text = text;

			// Blinkered: on content-ready (ALL windows painted), a WINDOWED launch reveals BY tearing the cover down — the
			// black holds over the white/header staging and reveals fully-painted content in one hit. But for CreateNewDesktop
			// (lock) the reveal is the desktop SWITCH (ActivateCustomDesktopOperation, also gated on content-ready); tearing
			// the cover down here would briefly expose the ORIGINAL desktop in the milliseconds before the switch. So for that
			// mode the cover is closed post-switch in HandleSessionStartSuccess instead.
			this.runtimeHost.ContentReady += () =>
			{
				// Read the mode from the ACTIVE-or-incoming session (Current ?? Next): content-ready can arrive before
				// Context.Current is set, and relying on Current alone would skip this close and leave the windowed cover to
				// the slow 8s fallback (the very delay we're removing).
				var mode = (Session ?? Context.Next)?.Settings?.Security?.KioskMode;
				if (mode != KioskMode.CreateNewDesktop)
				{
					CloseLaunchCover("content ready (all sites painted)");
				}
			};
		}

		public override void Assume(RuntimeTask task)
		{
			switch (task)
			{
				case RuntimeTask.StartSession:
					StartSession();
					break;
				case RuntimeTask.StopSession:
					StopSession();
					break;
			}
		}

		private void StartSession()
		{
			runtimeWindow.Show();
			runtimeWindow.BringToForeground();
			runtimeWindow.ShowProgressBar = true;

			// Blinkered: the launch cover (black + composited logo) is the sole launch visual now — don't show the small
			// splash card on top of it (avoids the "logo → black → logo" launch flicker). The cover is torn down at
			// content-ready (HandleSessionStartSuccess).

			Logger.Info(AppendDivider("Session Start Procedure"));

			if (SessionIsRunning)
			{
				Context.Responsibilities.Delegate(RuntimeTask.DeregisterSessionEvents);
			}

			Logger.Perf("Runtime: session-start begin");

			var result = SessionIsRunning ? sessionSequence.TryRepeat() : sessionSequence.TryPerform();

			Logger.Perf("Runtime: session-start done");

			if (result == OperationResult.Success)
			{
				Logger.Info(AppendDivider("Session Running"));

				HandleSessionStartSuccess();
			}
			else if (result == OperationResult.Failed)
			{
				Logger.Info(AppendDivider("Session Start Failed"));

				HandleSessionStartFailure();
			}
			else if (result == OperationResult.Aborted)
			{
				Logger.Info(AppendDivider("Session Start Aborted"));

				HandleSessionStartAbortion();
			}
		}

		private void StopSession()
		{
			runtimeWindow.Show();
			runtimeWindow.BringToForeground();
			runtimeWindow.ShowProgressBar = true;

			Logger.Info(AppendDivider("Session Stop Procedure"));

			Context.Responsibilities.Delegate(RuntimeTask.DeregisterSessionEvents);

			var success = sessionSequence.TryRevert() == OperationResult.Success;

			if (success)
			{
				Logger.Info(AppendDivider("Session Terminated"));
			}
			else
			{
				Logger.Info(AppendDivider("Session Stop Failed"));
			}
		}

		private void HandleSessionStartSuccess()
		{
			Context.Responsibilities.Delegate(RuntimeTask.RegisterSessionEvents);

			runtimeWindow.ShowProgressBar = false;
			runtimeWindow.ShowLog = Session.Settings.Security.AllowApplicationLogAccess;
			runtimeWindow.TopMost = Session.Settings.Security.KioskMode != KioskMode.None;
			runtimeWindow.UpdateStatus(TextKey.RuntimeWindow_ApplicationRunning);

			// #3: session is running — the browser is taking over the screen, so hide the launch splash. Hide (not Close):
			// the RuntimeController reuses this splash instance for the shutdown sequence.
			splashScreen.Hide();

			// Blinkered cover teardown, mode-aware:
			//  - CreateNewDesktop (lock): the reveal already happened via the desktop SWITCH (which itself waited for
			//    content-ready). Session Running runs AFTER that switch, so tear the cover down HERE — it is on the now-
			//    hidden original desktop, so this closes with no teardown-before-switch flash of the original desktop.
			//  - None (focus) / DisableExplorerShell: the reveal IS the cover teardown on content-ready, which fires AFTER
			//    Session Running (the windowed launch isn't gated on it). Don't close here; arm the fallback so a missed
			//    content-ready can never leave the cover stuck.
			if (Session.Settings.Security.KioskMode == KioskMode.CreateNewDesktop)
			{
				CloseLaunchCover("session running (post-desktop-switch)");
			}
			else
			{
				StartCoverFallback();
			}

			if (Session.Settings.Security.KioskMode == KioskMode.DisableExplorerShell)
			{
				runtimeWindow.Hide();
			}
		}

		private void HandleSessionStartFailure()
		{
			CloseLaunchCover("launch failed");   // don't leave the black cover behind the error dialog

			var message = AppendLogFilePaths(appConfig, text.Get(TextKey.MessageBox_SessionStartError));
			var title = text.Get(TextKey.MessageBox_SessionStartErrorTitle);

			if (SessionIsRunning)
			{
				StopSession();

				// #4 launch speedup — safety net for the reconfig (TryRepeat) path. The client is now spawned EARLY, and
				// RepeatableOperationSequence.TryRepeat does NOT auto-revert the stack on failure (unlike TryPerform). The
				// early client is normally torn down by StopSession -> TryRevert -> ClientStartOperation.Revert (that op
				// persists on the sequence's revert stack from the initial perform), but this explicit, idempotent kill
				// guarantees a failed reconfig can never leave the early client orphaned/hidden, regardless of stack state.
				TerminateEarlyClientIfRunning();

				messageBox.Show(message, title, icon: MessageBoxIcon.Error, parent: runtimeWindow);

				Logger.Info("Terminating application...");
				shutdown.Invoke();
			}
			else
			{
				TerminateEarlyClientIfRunning();

				messageBox.Show(message, title, icon: MessageBoxIcon.Error, parent: runtimeWindow);
			}
		}

		private void TerminateEarlyClientIfRunning()
		{
			var client = Context.ClientProcess;

			if (client != null && !client.HasTerminated)
			{
				Logger.Warn("Session start failed with the early-spawned client still running — killing it to avoid an orphaned/hidden client.");

				for (var attempt = 1; attempt <= 5 && !client.HasTerminated; attempt++)
				{
					client.TryKill(500);
				}

				Context.ClientProcess = null;
				Context.ClientProxy = null;
			}
		}

		private void HandleSessionStartAbortion()
		{
			CloseLaunchCover("launch aborted");   // tear the cover down

			if (SessionIsRunning)
			{
				Context.Responsibilities.Delegate(RuntimeTask.RegisterSessionEvents);

				runtimeWindow.ShowProgressBar = false;
				runtimeWindow.UpdateStatus(TextKey.RuntimeWindow_ApplicationRunning);
				runtimeWindow.TopMost = Session.Settings.Security.KioskMode != KioskMode.None;

				if (Session.Settings.Security.KioskMode == KioskMode.DisableExplorerShell)
				{
					runtimeWindow.Hide();
				}

				Context.ClientProxy.InformReconfigurationAborted();
			}
		}

		private void CloseLaunchCover(string reason)
		{
			// Close exactly once — ContentReady, the fallback timer, and the failure/abort paths can all race.
			if (Interlocked.Exchange(ref coverClosed, 1) != 0)
			{
				return;
			}

			try { coverFallbackTimer?.Dispose(); } catch { /* best-effort */ }

			try
			{
				Logger.Info($"Launch cover: tearing down ({reason}).");
				launchCover?.Close();
			}
			catch (Exception e)
			{
				Logger.Warn($"Launch cover: failed to close ({e.Message}).");
			}
		}

		private void StartCoverFallback()
		{
			// Safety net: if the client never signals ContentReady (e.g. the first site hangs / the signal is missed),
			// tear the cover down anyway ~8s after Session Running, so a stuck black screen can never happen.
			try
			{
				coverFallbackTimer = new Timer(_ => CloseLaunchCover("fallback timeout (no content-ready signal)"), null, 8000, Timeout.Infinite);
			}
			catch (Exception e)
			{
				Logger.Warn($"Launch cover: failed to start fallback timer ({e.Message}) — closing now.");
				CloseLaunchCover("fallback timer failed");
			}
		}

		private string AppendDivider(string message)
		{
			var dashesLeft = new string('-', 48 - message.Length / 2 - message.Length % 2);
			var dashesRight = new string('-', 48 - message.Length / 2);

			return $"### {dashesLeft} {message} {dashesRight} ###";
		}
	}
}
