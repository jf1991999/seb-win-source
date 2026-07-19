/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading.Tasks;
using System.Timers;
using SafeExamBrowser.Integrity.Contracts;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.Runtime.Responsibilities
{
	internal class IntegrityResponsibility : RuntimeResponsibility
	{
		private readonly IIntegrityModule integrityModule;
		private readonly Action shutdown;
		private readonly Timer timer;

		public IntegrityResponsibility(
			IIntegrityModule integrityModule,
			ILogger logger,
			RuntimeContext runtimeContext,
			Action shutdown) : base(logger, runtimeContext)
		{
			this.integrityModule = integrityModule;
			this.shutdown = shutdown;
			this.timer = new Timer();
		}

		public override void Assume(RuntimeTask task)
		{
			switch (task)
			{
				case RuntimeTask.StartIntegrityMonitoring:
					StartIntegrityMonitoring();
					break;
				case RuntimeTask.StopIntegrityMonitoring:
					StopIntegrityMonitoring();
					break;
			}
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

			Logger.Info("Stopped monitoring application integrity.");
		}

		private void Timer_Elapsed(object sender, ElapsedEventArgs e)
		{
			Logger.Info("Attempting to verify application integrity...");

			// Our own-binary Authenticode check is deterministic, so a false is a real violation (no warmup
			// tolerance needed). A 'false' RETURN means the verdict was indeterminate (a binary was briefly
			// unreadable) — skip this tick and re-check in 5s; do NOT treat indeterminate as compromised.
			if (integrityModule.TryVerifyApplicationIntegrity(out var isValid))
			{
				HandleIntegrityStatus(isValid);
			}
			else
			{
				Logger.Warn("Application integrity could not be determined this tick; re-checking in 5s.");
			}

			timer.Start();
		}

		private void HandleIntegrityStatus(bool isValid)
		{
			if (isValid)
			{
				Logger.Info("Application integrity successfully verified.");
			}
			else
			{
				Logger.Error("Application integrity is compromised!");

				StopIntegrityMonitoring();

				Task.Run(() =>
				{
					if (SessionIsRunning)
					{
						Context.Responsibilities.Delegate(RuntimeTask.StopSession);
					}

					shutdown.Invoke();
				});
			}
		}
	}
}
