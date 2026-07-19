/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using SafeExamBrowser.Communication.Contracts.Hosts;
using SafeExamBrowser.Communication.Contracts.Proxies;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.WindowsApi.Contracts;

namespace SafeExamBrowser.Runtime.Operations.Session
{
	// #4 launch speedup: ClientOperation was split into ClientStartOperation (early spawn + teardown) and
	// ClientReadyOperation (wait + connect). ClientTerminationOperation now derives from ClientStartOperation, reusing its
	// Revert() as the "stop the previously running client" teardown it triggers on reconfiguration (Repeat => base.Revert).
	// The proxyFactory parameter is retained for call-site compatibility; the teardown reuses the existing proxy and does
	// not need it.
	internal class ClientTerminationOperation : ClientStartOperation
	{
		public ClientTerminationOperation(
			Dependencies dependencies,
			IProcessFactory processFactory,
			IProxyFactory proxyFactory,
			IRuntimeHost runtimeHost,
			int timeout_ms) : base(dependencies, processFactory, runtimeHost, timeout_ms)
		{
		}

		public override OperationResult Perform()
		{
			return OperationResult.Success;
		}

		public override OperationResult Repeat()
		{
			return base.Revert();
		}

		public override OperationResult Revert()
		{
			return OperationResult.Success;
		}
	}
}
