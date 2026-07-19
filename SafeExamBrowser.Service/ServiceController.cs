/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using SafeExamBrowser.Communication.Contracts.Events;
using SafeExamBrowser.Communication.Contracts.Hosts;
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.Lockdown.Contracts;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.Service
{
	internal class ServiceController
	{
		private ILogger logger;
		private Func<string, ILogObserver> logWriterFactory;
		private IOperationSequence bootstrapSequence;
		private IOperationSequence sessionSequence;
		private IServiceHost serviceHost;
		private SessionContext sessionContext;
		private ISystemConfigurationUpdate systemConfigurationUpdate;
		private ILogObserver sessionWriter;

		private ServiceConfiguration Session
		{
			get { return sessionContext.Configuration; }
		}

		internal bool SessionIsRunning
		{
			get { return sessionContext.IsRunning; }
		}

		// Update-reliability: the Service is the AUTHORITATIVE source of session state and the natural point-of-use
		// trigger. These fire when a lock session actually starts/ends, so the update check can converge the device on
		// unlock (apply between sessions) instead of only on boot / the ~6h timer. SessionEnded is where apply-on-unlock
		// hangs off; SessionStarted is reserved for the future download-on-lock staging.
		internal event Action SessionStarted;
		internal event Action SessionEnded;

		internal ServiceController(
			ILogger logger,
			Func<string, ILogObserver> logWriterFactory,
			IOperationSequence bootstrapSequence,
			IOperationSequence sessionSequence,
			IServiceHost serviceHost,
			SessionContext sessionContext,
			ISystemConfigurationUpdate systemConfigurationUpdate)
		{
			this.logger = logger;
			this.logWriterFactory = logWriterFactory;
			this.bootstrapSequence = bootstrapSequence;
			this.sessionSequence = sessionSequence;
			this.serviceHost = serviceHost;
			this.sessionContext = sessionContext;
			this.systemConfigurationUpdate = systemConfigurationUpdate;
		}

		internal bool TryStart()
		{
			logger.Info("Initiating startup procedure...");

			var result = bootstrapSequence.TryPerform();
			var success = result == OperationResult.Success;

			if (success)
			{
				RegisterEvents();

				logger.Info("Service successfully initialized.");
				logger.Log(string.Empty);
			}
			else
			{
				logger.Info("Service startup aborted!");
				logger.Log(string.Empty);
			}

			return success;
		}

		internal void Terminate()
		{
			DeregisterEvents();

			if (SessionIsRunning)
			{
				StopSession();
			}

			logger.Log(string.Empty);
			logger.Info("Initiating termination procedure...");

			var result = bootstrapSequence.TryRevert();
			var success = result == OperationResult.Success;

			if (success)
			{
				logger.Info("Service successfully terminated.");
				logger.Log(string.Empty);
			}
			else
			{
				logger.Info("Service termination failed!");
				logger.Log(string.Empty);
			}
		}

		private void StartSession()
		{
			InitializeSessionLogging();
			logger.Info(AppendDivider("Session Start Procedure"));

			var result = sessionSequence.TryPerform();

			if (result == OperationResult.Success)
			{
				logger.Info(AppendDivider("Session Running"));
				SessionStarted?.Invoke();
			}
			else
			{
				logger.Info(AppendDivider("Session Start Failed"));
			}
		}

		private void StopSession()
		{
			logger.Info(AppendDivider("Session Stop Procedure"));

			var result = sessionSequence.TryRevert();

			if (result == OperationResult.Success)
			{
				logger.Info(AppendDivider("Session Terminated"));
			}
			else
			{
				logger.Info(AppendDivider("Session Stop Failed"));
			}

			FinalizeSessionLogging();

			// Apply-on-unlock: the session has ended and the device is idle — a natural, safe moment to converge to the
			// latest version (the apply gate re-confirms idle before any machine change). Non-blocking (runs on a
			// background task via the overlap-lock).
			SessionEnded?.Invoke();
		}

		private void RegisterEvents()
		{
			serviceHost.SessionStartRequested += ServiceHost_SessionStartRequested;
			serviceHost.SessionStopRequested += ServiceHost_SessionStopRequested;
			serviceHost.SystemConfigurationUpdateRequested += ServiceHost_SystemConfigurationUpdateRequested;
		}

		private void DeregisterEvents()
		{
			serviceHost.SessionStartRequested -= ServiceHost_SessionStartRequested;
			serviceHost.SessionStopRequested -= ServiceHost_SessionStopRequested;
			serviceHost.SystemConfigurationUpdateRequested -= ServiceHost_SystemConfigurationUpdateRequested;
		}

		private void ServiceHost_SessionStartRequested(SessionStartEventArgs args)
		{
			if (SessionIsRunning)
			{
				// A session is still marked as running: the previous Runtime was force-killed (e.g. by the Blinkered
				// agent on unlock) without sending a SessionStop, so StopSession never ran - IsRunning stayed true and
				// the lockdown / ease-of-access (Utilman IFEO) state was left un-reverted. Without handling this, the
				// request fell through to a warning and the session sequence (which sets the ServiceEvent) never ran,
				// so the new Runtime timed out after 30s. Supersede the stale session: stop it first (reverts lockdown,
				// restores the ease-of-access key, clears IsRunning), then start the new one. Mirrors the OnConnect
				// supersede - that clears the stale connection; this clears the stale session.
				logger.Warn("A session is still marked as running (the previous Runtime did not stop cleanly); superseding the stale session before starting the new one.");

				StopSession();
			}

			sessionContext.Configuration = args.Configuration;

			StartSession();
		}

		private void ServiceHost_SessionStopRequested(SessionStopEventArgs args)
		{
			if (SessionIsRunning)
			{
				if (Session.SessionId == args.SessionId)
				{
					StopSession();
				}
				else
				{
					logger.Warn("Received session stop request with wrong session ID!");
				}
			}
			else
			{
				logger.Warn("Received session stop request, even though no session is currently running!");
			}
		}

		private void ServiceHost_SystemConfigurationUpdateRequested()
		{
			logger.Info("Received request to initiate system configuration update.");
			systemConfigurationUpdate.ExecuteAsync();
		}

		private string AppendDivider(string message)
		{
			var dashesLeft = new String('-', 48 - message.Length / 2 - message.Length % 2);
			var dashesRight = new String('-', 48 - message.Length / 2);

			return $"### {dashesLeft} {message} {dashesRight} ###";
		}

		private void InitializeSessionLogging()
		{
			if (Session?.AppConfig?.ServiceLogFilePath != null)
			{
				sessionWriter = logWriterFactory.Invoke(Session.AppConfig.ServiceLogFilePath);
				logger.Subscribe(sessionWriter);
				logger.LogLevel = Session.Settings.LogLevel;
			}
			else
			{
				logger.Warn("Could not initialize session writer due to missing configuration data!");
			}
		}

		private void FinalizeSessionLogging()
		{
			if (sessionWriter != null)
			{
				logger.Unsubscribe(sessionWriter);
				sessionWriter = null;
			}
		}
	}
}
