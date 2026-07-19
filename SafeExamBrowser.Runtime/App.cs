/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.Core.Blinkered;
using SafeExamBrowser.Runtime.Blinkered;

namespace SafeExamBrowser.Runtime
{
	public class App : Application
	{
		private static readonly Mutex Mutex = new Mutex(true, AppConfig.RUNTIME_MUTEX_NAME);
		private readonly CompositionRoot instances = new CompositionRoot();

		[STAThread]
		public static void Main()
		{
			try
			{
				// Blinkered: a 'blinkered://savepair' handoff is a one-shot that writes local pairing
				// credentials and exits. Handle it before the single-instance mutex / session startup so
				// it works even if a Runtime is already running, and never spins up a WPF session.
				var args = Environment.GetCommandLineArgs();

				if (args.Length > 1 && BlinkeredUrl.IsSavePair(args[1]))
				{
					SavePairHandler.Run(args[1]);
					return;
				}

				// Blinkered: the home session is driven by the agent, which launches the Runtime with a downloaded
				// .seb on a lock from the parent dashboard. A manual launch (Start-menu shortcut / double-click)
				// passes no config, so there is no session to run - rather than building the default-config kiosk
				// (a slow, confusing dead-end), show a clear message and exit cleanly.
				if (args.Length <= 1)
				{
					MessageBox.Show(
						"Blinkered runs when your parent locks this device from the dashboard. There's nothing to do here right now.",
						"Blinkered", MessageBoxButton.OK, MessageBoxImage.Information);
					return;
				}

				StartApplication();
			}
			catch (Exception e)
			{
				MessageBox.Show(e.Message + "\n\n" + e.StackTrace, "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			finally
			{
				Mutex.Close();
			}
		}

		private static void StartApplication()
		{
			if (NoInstanceRunning())
			{
				new App().Run();
			}
			else
			{
				MessageBox.Show("You can only run one instance of Blinkered at a time.", "Startup Not Allowed", MessageBoxButton.OK, MessageBoxImage.Information);
			}
		}

		private static bool NoInstanceRunning()
		{
			return Mutex.WaitOne(TimeSpan.Zero, true);
		}

		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			ShutdownMode = ShutdownMode.OnExplicitShutdown;

			instances.BuildObjectGraph(Shutdown);
			instances.LogStartupInformation();

			Task.Run(new Action(TryStart));
		}

		private void TryStart()
		{
			var success = instances.RuntimeController.TryStart();

			if (!success)
			{
				Shutdown();
			}
		}

		public new void Shutdown()
		{
			Task.Run(new Action(ShutdownInternal));
		}

		private void ShutdownInternal()
		{
			instances.RuntimeController.Terminate();
			instances.LogShutdownInformation();

			Dispatcher.Invoke(base.Shutdown);
		}
	}
}
