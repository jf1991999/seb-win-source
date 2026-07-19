/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Threading.Tasks;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Monitoring.Contracts.System.Events;
using SafeExamBrowser.SystemComponents.Contracts.Registry;

namespace SafeExamBrowser.Monitoring.System.Components
{
	internal class EaseOfAccess
	{
		private readonly ILogger logger;
		private readonly IRegistry registry;

		internal event SentinelEventHandler EaseOfAccessChanged;

		internal EaseOfAccess(ILogger logger, IRegistry registry)
		{
			this.logger = logger;
			this.registry = registry;
		}

		internal void StartMonitoring()
		{
			registry.ValueChanged += Registry_ValueChanged;
			registry.StartMonitoring(RegistryValue.MachineHive.EaseOfAccess_Key, RegistryValue.MachineHive.EaseOfAccess_Name);

			logger.Info("Started monitoring ease of access.");
		}

		internal void StopMonitoring()
		{
			registry.ValueChanged -= Registry_ValueChanged;
			registry.StopMonitoring(RegistryValue.MachineHive.EaseOfAccess_Key, RegistryValue.MachineHive.EaseOfAccess_Name);

			logger.Info("Stopped monitoring ease of access.");
		}

		internal bool Verify()
		{
			logger.Info($"Starting ease of access verification...");

			var success = registry.TryRead(RegistryValue.MachineHive.EaseOfAccess_Key, RegistryValue.MachineHive.EaseOfAccess_Name, out var value);

			if (success)
			{
				if (value is string s && IsSafeDebuggerValue(s))
				{
					logger.Info("Ease of access configuration successfully verified.");
				}
				else
				{
					logger.Warn($"Ease of access configuration is compromised: '{value}'!");
					success = false;
				}
			}
			else
			{
				success = true;
				logger.Info("Ease of access configuration successfully verified (value does not exist).");
			}

			return success;
		}

		private void Registry_ValueChanged(string key, string name, object oldValue, object newValue)
		{
			if (key == RegistryValue.MachineHive.EaseOfAccess_Key)
			{
				// A change TO a safe state — empty/absent (default) or SEB's own inert SebDummy.exe (the hardened
				// state, escape blocked) — is not a compromise: the Blinkered service writes exactly this, and it may
				// re-assert it mid-session. Only a change to some OTHER debugger (a real tool) is an attack worth a
				// lock screen. Without this guard, a (matching or stale) service re-writing SebDummy.exe would pop the
				// lock screen repeatedly.
				if (newValue is string s && IsSafeDebuggerValue(s))
				{
					return;
				}

				HandleEaseOfAccessChange(key, name, oldValue, newValue);
			}
		}

		/// <summary>
		/// True if the Utilman IFEO Debugger value is a non-compromised state: empty/whitespace (default, Utilman works
		/// normally) or SEB's own inert SebDummy.exe hardening (Utilman redirected to a non-existent exe → escape
		/// blocked). Any other non-empty value is a real debugger redirect → compromised.
		/// </summary>
		private static bool IsSafeDebuggerValue(string value)
		{
			return string.IsNullOrWhiteSpace(value)
				|| string.Equals(value.Trim(), RegistryValue.MachineHive.EaseOfAccess_HardenedDebuggerValue, global::System.StringComparison.OrdinalIgnoreCase);
		}

		private void HandleEaseOfAccessChange(string key, string name, object oldValue, object newValue)
		{
			var args = new SentinelEventArgs();

			logger.Warn($@"The ease of access registry value '{key}\{name}' has changed from '{oldValue}' to '{newValue}'!");

			Task.Run(() => EaseOfAccessChanged?.Invoke(args)).ContinueWith((_) =>
			{
				if (args.Allow)
				{
					registry.StopMonitoring(key, name);
				}
			});
		}
	}
}
