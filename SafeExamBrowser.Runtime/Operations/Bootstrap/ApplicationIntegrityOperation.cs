/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Threading;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.Core.Contracts.OperationModel.Events;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Integrity.Contracts;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.Runtime.Operations.Bootstrap
{
	internal class ApplicationIntegrityOperation : IOperation
	{
		private readonly IIntegrityModule module;
		private readonly ILogger logger;

		public event StatusChangedEventHandler StatusChanged;

		public ApplicationIntegrityOperation(IIntegrityModule module, ILogger logger)
		{
			this.module = module;
			this.logger = logger;
		}

		public OperationResult Perform()
		{
			logger.Info("Attempting to verify application integrity...");
			StatusChanged?.Invoke(TextKey.OperationStatus_VerifyApplicationIntegrity);

			// Verify our OWN binaries are Authenticode-signed by our certificate (deterministic, fail-closed),
			// replacing reliance on ETH's native runtime-integrity check (which is ETH-keyed and unusable for our
			// fork). The check is "indeterminate" if a binary is briefly unreadable (antivirus often locks freshly
			// installed files at first launch); since this one-shot bootstrap has no next tick, tolerate THAT with
			// a SHORT bounded retry — but fail-closed IMMEDIATELY on a definitive violation (isValid == false).
			const int MAX_ATTEMPTS = 5;
			const int RETRY_INTERVAL_MS = 2000;

			for (var attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
			{
				if (module.TryVerifyApplicationIntegrity(out var isValid))
				{
					if (isValid)
					{
						logger.Info($"Application integrity successfully verified (attempt {attempt}/{MAX_ATTEMPTS}).");
						logger.Perf("Runtime: integrity done");

						return OperationResult.Success;
					}

					logger.Error("Application integrity is compromised! Aborting startup.");

					return OperationResult.Failed;
				}

				if (attempt < MAX_ATTEMPTS)
				{
					logger.Warn($"Application integrity could not be determined (attempt {attempt}/{MAX_ATTEMPTS}); some binaries unreadable (antivirus?), retrying in {RETRY_INTERVAL_MS}ms...");
					StatusChanged?.Invoke(TextKey.OperationStatus_VerifyApplicationIntegrity);
					Thread.Sleep(RETRY_INTERVAL_MS);
				}
			}

			// Persistent indeterminate is NOT a confirmed violation (just unreadable binaries). Do not block
			// startup on an inconclusive read at boot — log loudly and proceed; the continuous 5s integrity
			// monitor stays the enforcer and will fail-closed once the binaries are readable (or remain so).
			logger.Warn($"Application integrity could not be determined after {MAX_ATTEMPTS} attempts (binaries unreadable, NOT a confirmed violation); proceeding — the continuous integrity monitor will enforce.");

			return OperationResult.Success;
		}

		public OperationResult Revert()
		{
			return OperationResult.Success;
		}
	}
}
