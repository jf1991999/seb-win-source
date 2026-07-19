/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using SafeExamBrowser.Communication.Hosts;
using SafeExamBrowser.Communication.Contracts;
using SafeExamBrowser.Communication.Contracts.Data;
using SafeExamBrowser.Communication.Contracts.Events;
using SafeExamBrowser.Communication.Contracts.Hosts;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.Service.Communication
{
	internal class ServiceHost : BaseHost, IServiceHost
	{
		private bool allowConnection;

		public event CommunicationEventHandler<SessionStartEventArgs> SessionStartRequested;
		public event CommunicationEventHandler<SessionStopEventArgs> SessionStopRequested;
		public event CommunicationEventHandler SystemConfigurationUpdateRequested;

		internal ServiceHost(string address, IHostObjectFactory factory, ILogger logger, int timeout_ms) : base(address, factory, logger, timeout_ms)
		{
			allowConnection = true;
		}

		protected override bool OnConnect(Guid? token)
		{
			// The Runtime is single-instance (App-level mutex), so a new connection request can only come from a
			// fresh Runtime. Any existing connection is therefore stale: a previous Runtime that was force-killed
			// (e.g. by the Blinkered agent on unlock) and never sent Disconnect, so OnDisconnect never reset the
			// flag. Supersede it - accept unconditionally (race-free against a kill -> immediate-relaunch sequence,
			// unlike a channel-fault-triggered reset would be) and drop the stale communication token, rather than
			// refusing every future Runtime for the lifetime of the service process.
			if (!allowConnection)
			{
				Logger.Warn("Superseding a stale service connection (the previous Runtime did not disconnect).");
				CommunicationToken.Clear();
			}

			allowConnection = false;

			return true;
		}

		protected override void OnDisconnect(Interlocutor interlocutor)
		{
			if (interlocutor == Interlocutor.Runtime)
			{
				allowConnection = true;
			}
		}

		protected override Response OnReceive(Message message)
		{
			switch (message)
			{
				case SessionStartMessage m:
					SessionStartRequested?.InvokeAsync(new SessionStartEventArgs { Configuration = m.Configuration });
					return new SimpleResponse(SimpleResponsePurport.Acknowledged);
				case SessionStopMessage m:
					SessionStopRequested?.InvokeAsync(new SessionStopEventArgs { SessionId = m.SessionId });
					return new SimpleResponse(SimpleResponsePurport.Acknowledged);
			}

			return new SimpleResponse(SimpleResponsePurport.UnknownMessage);
		}

		protected override Response OnReceive(SimpleMessagePurport message)
		{
			switch (message)
			{
				case SimpleMessagePurport.UpdateSystemConfiguration:
					SystemConfigurationUpdateRequested?.InvokeAsync();
					return new SimpleResponse(SimpleResponsePurport.Acknowledged);
			}

			return new SimpleResponse(SimpleResponsePurport.UnknownMessage);
		}
	}
}
