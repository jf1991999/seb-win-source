/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using SafeExamBrowser.Communication.Hosts;
using SafeExamBrowser.Communication.Proxies;
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.Core.OperationModel;
using SafeExamBrowser.Core.Operations;
using SafeExamBrowser.Lockdown;
using SafeExamBrowser.Logging;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Service.Communication;
using SafeExamBrowser.Service.Operations;
using SafeExamBrowser.Settings.Logging;

namespace SafeExamBrowser.Service
{
	internal class CompositionRoot
	{
		private ILogger logger;
		private Update.PeriodicUpdateCheck updateCheck;

		internal ServiceController ServiceController { get; private set; }

		internal void BuildObjectGraph()
		{
			const int ONE_SECOND = 1000;
			const int FIVE_SECONDS = 5000;
			const int FIFTEEN_SECONDS = 15000;

			var backupFilePath = BuildBackupFilePath();

			InitializeLogging();

			var featureBackup = new FeatureConfigurationBackup(backupFilePath, new ModuleLogger(logger, nameof(FeatureConfigurationBackup)));
			var featureFactory = new FeatureConfigurationFactory(new ModuleLogger(logger, nameof(FeatureConfigurationFactory)));
			var featureMonitor = new FeatureConfigurationMonitor(new ModuleLogger(logger, nameof(FeatureConfigurationMonitor)), ONE_SECOND);
			var proxyFactory = new ProxyFactory(new ProxyObjectFactory(), new ModuleLogger(logger, nameof(ProxyFactory)));
			var serviceHost = new ServiceHost(AppConfig.SERVICE_ADDRESS, new HostObjectFactory(), new ModuleLogger(logger, nameof(ServiceHost)), FIFTEEN_SECONDS);
			var sessionContext = new SessionContext();
			var systemConfigurationUpdate = new SystemConfigurationUpdate(new ModuleLogger(logger, nameof(SystemConfigurationUpdate)));

			var bootstrapOperations = new Queue<IOperation>();
			var sessionOperations = new Queue<SessionOperation>();

			sessionContext.AutoRestoreMechanism = new AutoRestoreMechanism(featureBackup, new ModuleLogger(logger, nameof(AutoRestoreMechanism)), systemConfigurationUpdate, FIVE_SECONDS);

			bootstrapOperations.Enqueue(new RestoreOperation(featureBackup, logger, sessionContext));
			bootstrapOperations.Enqueue(new CommunicationHostOperation(serviceHost, logger));
			bootstrapOperations.Enqueue(new ServiceEventCleanupOperation(logger, sessionContext));

			sessionOperations.Enqueue(new SessionInitializationOperation(logger, ServiceEventFactory, sessionContext));
			sessionOperations.Enqueue(new LockdownOperation(featureBackup, featureFactory, featureMonitor, logger, sessionContext));
			sessionOperations.Enqueue(new SessionActivationOperation(logger, sessionContext));

			var bootstrapSequence = new OperationSequence<IOperation>(logger, bootstrapOperations);
			var sessionSequence = new OperationSequence<SessionOperation>(logger, sessionOperations);

			ServiceController = new ServiceController(logger, LogWriterFactory, bootstrapSequence, sessionSequence, serviceHost, sessionContext, systemConfigurationUpdate);
		}

		private string BuildBackupFilePath()
		{
			var appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(SafeExamBrowser));
			var filePath = Path.Combine(appDataFolder, AppConfig.BACKUP_FILE_NAME);

			return filePath;
		}

		internal void LogStartupInformation()
		{
			logger.Log($"# Service started at {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")}");
			logger.Log(string.Empty);
		}

		internal void LogShutdownInformation()
		{
			logger?.Log($"# Service terminated at {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")}");
		}

		/// <summary>
		/// Silent-update (P3b): kick the update checker on a background task. It decides its own mode — APPLY (when the
		/// apply gate is open: production OV thumbprint, or the self-signed stand-in with the explicit test opt-in),
		/// DRY-RUN (BLINKERED_UPDATE_DRYRUN=1, log-only), or skip — and is fully exception-isolated, so this never
		/// delays or destabilizes service start (the recovery-guarantee: a broken build can't wedge the updater).
		/// </summary>
		internal void StartUpdateCheck()
		{
			// Point 4 + point-of-use triggers: give the checker the Service's authoritative session state, and converge
			// the device on unlock (apply-on-unlock) instead of only on boot / the ~6h timer.
			updateCheck = new Update.PeriodicUpdateCheck(logger, () => ServiceController.SessionIsRunning);
			ServiceController.SessionStarted += updateCheck.OnSessionStarted;
			ServiceController.SessionEnded += updateCheck.OnSessionEnded;
			updateCheck.Start();
		}

		internal void StopUpdateCheck()
		{
			if (updateCheck != null)
			{
				ServiceController.SessionStarted -= updateCheck.OnSessionStarted;
				ServiceController.SessionEnded -= updateCheck.OnSessionEnded;
			}

			updateCheck?.Dispose();
		}

		private void InitializeLogging()
		{
			var appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(SafeExamBrowser));
			var logFolder = Path.Combine(appDataFolder, "Logs");
			var logFilePrefix = DateTime.Now.ToString("yyyy-MM-dd\\_HH\\hmm\\mss\\s", CultureInfo.InvariantCulture);
			var logFilePath = Path.Combine(logFolder, $"{logFilePrefix}_Service.log");
			var logFileWriter = new LogFileWriter(new DefaultLogFormatter(), logFilePath);

			logger = new Logger();
			logger.LogLevel = LogLevel.Debug;
			logger.Subscribe(logFileWriter);
			logFileWriter.Initialize();
		}

		private ILogObserver LogWriterFactory(string filePath)
		{
			var writer = new LogFileWriter(new DefaultLogFormatter(), filePath);

			writer.Initialize();

			return writer;
		}

		private EventWaitHandle ServiceEventFactory(string eventName)
		{
			var securityIdentifier = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
			var accessRule = new EventWaitHandleAccessRule(securityIdentifier, EventWaitHandleRights.Synchronize, AccessControlType.Allow);
			var security = new EventWaitHandleSecurity();

			security.AddAccessRule(accessRule);

			return new EventWaitHandle(false, EventResetMode.AutoReset, eventName, out _, security);
		}
	}
}
