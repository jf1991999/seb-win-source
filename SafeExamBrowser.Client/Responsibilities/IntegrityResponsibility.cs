/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using SafeExamBrowser.Client.Contracts;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Integrity.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.UserInterface.Contracts.Windows.Data;

namespace SafeExamBrowser.Client.Responsibilities
{
	internal class IntegrityResponsibility : ClientResponsibility
	{
		private readonly ICoordinator coordinator;
		private readonly IText text;
		private readonly Timer timer;

		private IIntegrityModule IntegrityModule => Context.IntegrityModule;

		public IntegrityResponsibility(ClientContext context, ICoordinator coordinator, ILogger logger, IText text) : base(context, logger)
		{
			this.coordinator = coordinator;
			this.text = text;
			this.timer = new Timer();
		}

		public override void Assume(ClientTask task)
		{
			switch (task)
			{
				case ClientTask.PrepareShutdown_Wave2:
					StopIntegrityMonitoring();
					break;
				case ClientTask.ScheduleIntegrityVerification:
					ScheduleIntegrityVerification();
					break;
				case ClientTask.StartMonitoring:
					StartIntegrityMonitoring();
					break;
				case ClientTask.UpdateSessionIntegrity:
					UpdateSessionIntegrity();
					break;
				case ClientTask.VerifySessionIntegrity:
					VerifySessionIntegrity();
					break;
			}
		}

		private void ScheduleIntegrityVerification()
		{
			var delay = TimeSpan.FromMinutes(10) + TimeSpan.FromMinutes(new Random().NextDouble() * 5);

			Task.Delay(delay).ContinueWith(_ => VerifyApplicationIntegrity());
		}

		private void StartIntegrityMonitoring()
		{
			const int FIVE_SECONDS = 5000;

			timer.AutoReset = false;
			timer.Interval = FIVE_SECONDS;
			timer.Elapsed += Timer_Elapsed;
			timer.Start();

			Logger.Info("Started monitoring application integrity.");
		}

		private void StopIntegrityMonitoring()
		{
			timer.Stop();
			timer.Elapsed -= Timer_Elapsed;

			Logger.Info("Stopped monitoring runtime integrity.");
		}

		private void Timer_Elapsed(object sender, ElapsedEventArgs e)
		{
			Logger.Info("Attempting to verify application integrity...");

			// Our own-binary Authenticode check is deterministic, so a false is a real violation (no warmup
			// tolerance needed). A 'false' RETURN means indeterminate (a binary was briefly unreadable) — skip
			// this tick and re-check in 5s; never treat indeterminate as compromised.
			if (IntegrityModule.TryVerifyApplicationIntegrity(out var isValid))
			{
				HandleRuntimeIntegrityStatus(isValid);
			}
			else
			{
				Logger.Warn("Application integrity could not be determined this tick; re-checking in 5s.");
			}

			timer.Start();
		}

		private void UpdateSessionIntegrity()
		{
			var hasQuitPassword = !string.IsNullOrEmpty(Settings?.Security.QuitPasswordHash);

			if (hasQuitPassword)
			{
				IntegrityModule?.ClearSession(Settings.Browser.ConfigurationKey);
			}
		}

		private void VerifyApplicationIntegrity()
		{
			Logger.Info($"Attempting to verify application integrity (delayed re-check)...");

			// Switched from the ETH native code-signature check to our own-binary Authenticode check (the native
			// one is ETH-keyed and rejects our cert). 'false' return = indeterminate -> just log, no lock.
			if (IntegrityModule.TryVerifyApplicationIntegrity(out var isValid))
			{
				HandleApplicationIntegrityStatus(isValid);
			}
			else
			{
				Logger.Warn("Application integrity could not be determined (delayed re-check).");
			}
		}

		private void VerifySessionIntegrity()
		{
			var hasQuitPassword = !string.IsNullOrEmpty(Settings.Security.QuitPasswordHash);

			if (hasQuitPassword && Settings.Security.VerifySessionIntegrity)
			{
				Logger.Info($"Attempting to verify session integrity...");

				if (IntegrityModule.TryVerifySessionIntegrity(Settings.Browser.ConfigurationKey, out var isValid))
				{
					HandleSessionIntegrityStatus(isValid);
				}
				else
				{
					Logger.Warn("Failed to verify session integrity!");
				}
			}
		}

		private void HandleApplicationIntegrityStatus(bool isValid)
		{
			if (isValid)
			{
				Logger.Info("Application integrity successfully verified.");
			}
			else if (coordinator.RequestSessionLock())
			{
				Logger.Warn("Application integrity is compromised!");

				ShowLockScreen(text.Get(TextKey.LockScreen_ApplicationIntegrityMessage), text.Get(TextKey.LockScreen_Title), Enumerable.Empty<LockScreenOption>());
				coordinator.ReleaseSessionLock();
			}
			else
			{
				Logger.Warn("Application integrity is compromised but lock screen is already active!");
				Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => VerifyApplicationIntegrity());
			}
		}

		private void HandleRuntimeIntegrityStatus(bool isValid)
		{
			if (isValid)
			{
				Logger.Info("Application integrity successfully verified.");
			}
			else if (coordinator.RequestSessionLock())
			{
				Logger.Warn("Application integrity is compromised!");

				Task.Run(() =>
				{
					ShowLockScreen(text.Get(TextKey.LockScreen_RuntimeIntegrityMessage), text.Get(TextKey.LockScreen_Title), Enumerable.Empty<LockScreenOption>());
					coordinator.ReleaseSessionLock();
				});
			}
			else
			{
				Logger.Warn("Application integrity is compromised but lock screen is already active!");
			}
		}

		private void HandleSessionIntegrityStatus(bool isValid)
		{
			if (isValid)
			{
				Logger.Info("Session integrity successfully verified.");
				IntegrityModule.CacheSession(Settings.Browser.ConfigurationKey);
			}
			else if (coordinator.RequestSessionLock())
			{
				Logger.Warn("Session integrity is compromised!");

				Task.Delay(1000).ContinueWith(_ =>
				{
					ShowLockScreen(text.Get(TextKey.LockScreen_SessionIntegrityMessage), text.Get(TextKey.LockScreen_Title), Enumerable.Empty<LockScreenOption>());
					coordinator.ReleaseSessionLock();
				});
			}
			else
			{
				Logger.Warn("Session integrity is compromised but lock screen is already active!");
				Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => VerifySessionIntegrity());
			}
		}
	}
}
