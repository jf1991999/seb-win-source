/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Threading;
using SafeExamBrowser.Communication.Contracts;
using SafeExamBrowser.Communication.Contracts.Hosts;
using SafeExamBrowser.Communication.Contracts.Proxies;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.Core.Contracts.OperationModel.Events;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.WindowsApi.Contracts;

namespace SafeExamBrowser.Runtime.Operations.Session
{
	/// <summary>
	/// #4 launch speedup — second half of the split <c>ClientOperation</c>. Waits for the client (spawned early by
	/// <see cref="ClientStartOperation"/>) to signal ready AND for the runtime lockdown to have been applied
	/// (<see cref="LockdownAppliedOperation"/>), then establishes the runtime&lt;-&gt;client communication proxy. Failing
	/// the wait (timeout or the client terminated during initialization) aborts the session start. Teardown of the client
	/// is owned by <see cref="ClientStartOperation"/>, so this operation's Revert is a no-op.
	/// </summary>
	internal class ClientReadyOperation : SessionOperation
	{
		private readonly IProxyFactory proxyFactory;
		private readonly IRuntimeHost runtimeHost;
		private readonly int timeout_ms;

		private IProcess ClientProcess
		{
			get { return Context.ClientProcess; }
			set { Context.ClientProcess = value; }
		}

		private IClientProxy ClientProxy
		{
			get { return Context.ClientProxy; }
			set { Context.ClientProxy = value; }
		}

		public override event StatusChangedEventHandler StatusChanged;

		public ClientReadyOperation(
			Dependencies dependencies,
			IProxyFactory proxyFactory,
			IRuntimeHost runtimeHost,
			int timeout_ms) : base(dependencies)
		{
			this.proxyFactory = proxyFactory;
			this.runtimeHost = runtimeHost;
			this.timeout_ms = timeout_ms;
		}

		public override OperationResult Perform()
		{
			StatusChanged?.Invoke(TextKey.OperationStatus_StartClient);

			Logger.Info("Waiting for client to complete initialization and for lockdown to be applied...");

			var ready = WaitHandle.WaitAll(new WaitHandle[] { Context.ClientReady, Context.LockdownApplied }, timeout_ms);

			runtimeHost.AllowConnection = false;
			runtimeHost.AuthenticationToken = default;

			if (!ready || Context.ClientTerminated)
			{
				Logger.Error("Client did not become ready before the timeout, or the client terminated unexpectedly during initialization! Aborting procedure...");

				return OperationResult.Failed;
			}

			var success = TryStartCommunication();

			if (success)
			{
				Logger.Info("Successfully connected to the early-started client instance.");
				Logger.Perf("Runtime: client ready");
			}
			else
			{
				Logger.Error("Failed to establish communication with the client instance! Aborting procedure...");
			}

			return success ? OperationResult.Success : OperationResult.Failed;
		}

		public override OperationResult Repeat()
		{
			return Perform();
		}

		public override OperationResult Revert()
		{
			return OperationResult.Success;
		}

		private bool TryStartCommunication()
		{
			var success = false;

			Logger.Info("Client has been successfully started and initialized. Creating communication proxy for client host...");
			ClientProxy = proxyFactory.CreateClientProxy(Context.Next.AppConfig.ClientAddress, Interlocutor.Runtime);

			if (ClientProxy.Connect(Context.Next.ClientAuthenticationToken))
			{
				Logger.Info("Connection with client has been established. Requesting authentication...");

				var communication = ClientProxy.RequestAuthentication();
				var response = communication.Value;

				success = communication.Success && ClientProcess.Id == response?.ProcessId;

				if (success)
				{
					Logger.Info("Authentication of client has been successful, client is ready to operate.");
				}
				else
				{
					Logger.Error("Failed to verify client integrity!");
				}
			}
			else
			{
				Logger.Error("Failed to connect to client!");
			}

			return success;
		}
	}
}
