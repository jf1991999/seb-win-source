/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SafeExamBrowser.Communication.Contracts;
using SafeExamBrowser.Communication.Contracts.Data;
using SafeExamBrowser.Communication.Contracts.Events;
using SafeExamBrowser.Communication.Contracts.Hosts;
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Service.Communication;

namespace SafeExamBrowser.Service.UnitTests.Communication
{
	[TestClass]
	public class ServiceHostTests
	{
		private Mock<IHostObject> hostObject;
		private Mock<IHostObjectFactory> hostObjectFactory;
		private Mock<ILogger> logger;
		private ServiceHost sut;

		[TestInitialize]
		public void Initialize()
		{
			hostObject = new Mock<IHostObject>();
			hostObjectFactory = new Mock<IHostObjectFactory>();
			logger = new Mock<ILogger>();

			hostObjectFactory.Setup(f => f.CreateObject(It.IsAny<string>(), It.IsAny<ICommunication>())).Returns(hostObject.Object);

			sut = new ServiceHost("net:pipe://some/address", hostObjectFactory.Object, logger.Object, 0);
		}

		[TestMethod]
		public void Connect_MustSupersedeStaleConnectionFromForceKilledRuntime()
		{
			// A previous Runtime connected but was force-killed without disconnecting (e.g. the Blinkered agent on
			// unlock), so no Disconnect arrived and the host still considers itself connected. A fresh, single-
			// instance Runtime must be able to connect (supersede the stale state) rather than being refused for
			// the lifetime of the service process.
			var first = sut.Connect();
			var second = sut.Connect();
			var third = sut.Connect();

			Assert.IsTrue(first.ConnectionEstablished);
			Assert.IsTrue(second.ConnectionEstablished);
			Assert.IsTrue(third.ConnectionEstablished);

			// The superseded (dead-interlocutor) tokens must be dropped, so only the latest connection is
			// authorized and a stale token cannot linger and act against the service.
			var staleResponse = sut.Send(new SimpleMessage(SimpleMessagePurport.Ping) { CommunicationToken = first.CommunicationToken.Value });
			var liveResponse = sut.Send(new SimpleMessage(SimpleMessagePurport.Ping) { CommunicationToken = third.CommunicationToken.Value });

			Assert.AreEqual(SimpleResponsePurport.Unauthorized, (staleResponse as SimpleResponse)?.Purport);
			Assert.AreEqual(SimpleResponsePurport.Acknowledged, (liveResponse as SimpleResponse)?.Purport);
		}

		[TestMethod]
		public void Disconnect_MustDisconnectAndThenAllowNewConnection()
		{
			var connect = sut.Connect();
			var disconnect = sut.Disconnect(new DisconnectionMessage { CommunicationToken = connect.CommunicationToken.Value, Interlocutor = Interlocutor.Runtime });

			Assert.IsTrue(disconnect.ConnectionTerminated);

			var connect2 = sut.Connect();

			Assert.IsTrue(connect2.ConnectionEstablished);
		}

		[TestMethod]
		public void Send_MustHandleSystemConfigurationUpdate()
		{
			var sync = new AutoResetEvent(false);
			var systemConfigurationUpdateRequested = false;

			sut.SystemConfigurationUpdateRequested += () => { systemConfigurationUpdateRequested = true; sync.Set(); };

			var token = sut.Connect().CommunicationToken.Value;
			var message = new SimpleMessage(SimpleMessagePurport.UpdateSystemConfiguration) { CommunicationToken = token };
			var response = sut.Send(message);

			sync.WaitOne();

			Assert.IsTrue(systemConfigurationUpdateRequested);
			Assert.IsNotNull(response);
			Assert.IsInstanceOfType(response, typeof(SimpleResponse));
			Assert.AreEqual(SimpleResponsePurport.Acknowledged, (response as SimpleResponse)?.Purport);
		}

		[TestMethod]
		public void Send_MustHandleSessionStartRequest()
		{
			var args = default(SessionStartEventArgs);
			var sync = new AutoResetEvent(false);
			var configuration = new ServiceConfiguration { SessionId = Guid.NewGuid() };
			var sessionStartRequested = false;

			sut.SessionStartRequested += (a) => { args = a; sessionStartRequested = true; sync.Set(); };

			var token = sut.Connect().CommunicationToken.Value;
			var message = new SessionStartMessage(configuration) { CommunicationToken = token };
			var response = sut.Send(message);

			sync.WaitOne();

			Assert.IsTrue(sessionStartRequested);
			Assert.IsNotNull(args);
			Assert.IsNotNull(response);
			Assert.IsInstanceOfType(response, typeof(SimpleResponse));
			Assert.AreEqual(configuration.SessionId, args.Configuration.SessionId);
			Assert.AreEqual(SimpleResponsePurport.Acknowledged, (response as SimpleResponse)?.Purport);
		}

		[TestMethod]
		public void Send_MustHandleSessionStopRequest()
		{
			var args = default(SessionStopEventArgs);
			var sync = new AutoResetEvent(false);
			var sessionId = Guid.NewGuid();
			var sessionStopRequested = false;

			sut.SessionStopRequested += (a) => { args = a; sessionStopRequested = true; sync.Set(); };

			var token = sut.Connect().CommunicationToken.Value;
			var message = new SessionStopMessage(sessionId) { CommunicationToken = token };
			var response = sut.Send(message);

			sync.WaitOne();

			Assert.IsTrue(sessionStopRequested);
			Assert.IsNotNull(args);
			Assert.IsNotNull(response);
			Assert.IsInstanceOfType(response, typeof(SimpleResponse));
			Assert.AreEqual(sessionId, args.SessionId);
			Assert.AreEqual(SimpleResponsePurport.Acknowledged, (response as SimpleResponse)?.Purport);
		}

		[TestMethod]
		public void Send_MustReturnUnknownMessageAsDefault()
		{
			var token = sut.Connect().CommunicationToken.Value;
			var message = new TestMessage { CommunicationToken = token } as Message;
			var response = sut.Send(message);

			Assert.IsNotNull(response);
			Assert.IsInstanceOfType(response, typeof(SimpleResponse));
			Assert.AreEqual(SimpleResponsePurport.UnknownMessage, (response as SimpleResponse)?.Purport);

			message = new SimpleMessage(default(SimpleMessagePurport)) { CommunicationToken = token };
			response = sut.Send(message);

			Assert.IsNotNull(response);
			Assert.IsInstanceOfType(response, typeof(SimpleResponse));
			Assert.AreEqual(SimpleResponsePurport.UnknownMessage, (response as SimpleResponse)?.Purport);
		}

		[TestMethod]
		public void MustNotFailIfNoEventHandlersSubscribed()
		{
			var token = sut.Connect(Guid.Empty).CommunicationToken.Value;

			sut.Send(new SessionStartMessage(null) { CommunicationToken = token });
			sut.Send(new SessionStopMessage(Guid.Empty) { CommunicationToken = token });
			sut.Disconnect(new DisconnectionMessage { CommunicationToken = token, Interlocutor = Interlocutor.Runtime });
		}

		private class TestMessage : Message { };
	}
}
