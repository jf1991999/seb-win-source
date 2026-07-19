/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using System.Threading;
using System.Windows;
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Server.Contracts.Data;
using SafeExamBrowser.Settings.Browser;
using SafeExamBrowser.Settings.UserInterface;
using SafeExamBrowser.UserInterface.Contracts;
using SafeExamBrowser.UserInterface.Contracts.Browser;
using SafeExamBrowser.UserInterface.Contracts.Proctoring;
using SafeExamBrowser.UserInterface.Contracts.Shell;
using SafeExamBrowser.UserInterface.Contracts.Windows;
using SafeExamBrowser.UserInterface.Contracts.Windows.Data;
using SafeExamBrowser.UserInterface.Desktop.Windows;
using SafeExamBrowser.UserInterface.Shared;
using SplashScreen = SafeExamBrowser.UserInterface.Desktop.Windows.SplashScreen;

namespace SafeExamBrowser.UserInterface.Desktop
{
	internal class WindowFactory : Guardable
	{
		private readonly IText text;

		internal WindowFactory(IText text, IWindowGuard windowGuard = default) : base(windowGuard)
		{
			this.text = text;
		}

		internal IWindow CreateAboutWindow(AppConfig appConfig)
		{
			return Guard(new AboutWindow(appConfig, text));
		}

		internal IActionCenter CreateActionCenter()
		{
			return Guard(new ActionCenter());
		}

		internal IBrowserWindow CreateBrowserWindow(IBrowserControl control, BrowserSettings settings, bool isMainWindow, ILogger logger)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new BrowserWindow(control, settings, isMainWindow, text, logger)));
		}

		internal ICredentialsDialog CreateCredentialsDialog(CredentialsDialogPurpose purpose, string message, string title)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new CredentialsDialog(purpose, message, title, text)));
		}

		internal IExamSelectionDialog CreateExamSelectionDialog(IEnumerable<Exam> exams)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new ExamSelectionDialog(exams, text)));
		}

		internal ILockScreen CreateLockScreen(string message, string title, IEnumerable<LockScreenOption> options, LockScreenSettings settings)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new LockScreen(message, title, settings, text, options)));
		}

		internal IWindow CreateLogWindow(ILogger logger)
		{
			var window = default(LogWindow);
			var windowReadyEvent = new AutoResetEvent(false);
			var windowThread = new Thread(() =>
			{
				window = Guard(new LogWindow(logger, text));
				window.Closed += (o, args) => window.Dispatcher.InvokeShutdown();
				window.Show();

				windowReadyEvent.Set();

				System.Windows.Threading.Dispatcher.Run();
			});

			windowThread.SetApartmentState(ApartmentState.STA);
			windowThread.IsBackground = true;
			windowThread.Start();

			windowReadyEvent.WaitOne();

			return window;
		}

		internal IPasswordDialog CreatePasswordDialog(string message, string title)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new PasswordDialog(message, title, text)));
		}

		internal IPasswordDialog CreatePasswordDialog(TextKey message, TextKey title)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new PasswordDialog(text.Get(message), text.Get(title), text)));
		}

		internal IProctoringFinalizationDialog CreateProctoringFinalizationDialog(bool requiresPassword)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new ProctoringFinalizationDialog(requiresPassword, text)));
		}

		internal IProctoringWindow CreateProctoringWindow(IProctoringControl control)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new ProctoringWindow(control)));
		}

		internal IWindow CreateLaunchCover(AppConfig appConfig = null)
		{
			// The launch cover MUST run on its own dedicated UI thread with its own Dispatcher message pump — exactly like
			// the splash screen below. The runtime's main thread BLOCKS for several seconds during bootstrap + session start
			// (lockdown, waiting for the client), so a cover created on Application.Current.Dispatcher would call Show() but
			// never get pumped/painted — it stays invisible for the whole launch (the bug that let unmasked white site
			// windows show through). On its own pumping thread it paints black+logo immediately, animates its progress
			// ribbon (also on this thread, so it moves even while the main thread is blocked), and holds until torn down.
			var window = default(LaunchCover);
			var windowReadyEvent = new AutoResetEvent(false);
			var windowThread = new Thread(() =>
			{
				window = Guard(new LaunchCover(appConfig));
				window.Closed += (o, args) => window.Dispatcher.InvokeShutdown();
				window.Show();

				windowReadyEvent.Set();

				System.Windows.Threading.Dispatcher.Run();
			});

			windowThread.SetApartmentState(ApartmentState.STA);
			windowThread.IsBackground = true;
			windowThread.Start();

			windowReadyEvent.WaitOne();

			return window;
		}

		internal IRuntimeWindow CreateRuntimeWindow(AppConfig appConfig)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new RuntimeWindow(appConfig, text)));
		}

		internal IServerFailureDialog CreateServerFailureDialog(string info, bool showFallback)
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new ServerFailureDialog(info, showFallback, text)));
		}

		internal ISplashScreen CreateSplashScreen(AppConfig appConfig = null, bool show = true)
		{
			var window = default(SplashScreen);
			var windowReadyEvent = new AutoResetEvent(false);
			var windowThread = new Thread(() =>
			{
				window = Guard(new SplashScreen(text, appConfig));
				window.Closed += (o, args) => window.Dispatcher.InvokeShutdown();

				// Blinkered: the client passes show:false so its splash never auto-shows during launch — the runtime's
				// launch cover is the sole launch surface, and a client splash (shown when the client process starts, later
				// than the cover) would otherwise activate on TOP of the cover as a cross-process topmost window. It is still
				// created (with a live pump) so an in-session reconfiguration can Show() it explicitly.
				if (show)
				{
					window.Show();
				}

				windowReadyEvent.Set();

				System.Windows.Threading.Dispatcher.Run();
			});

			windowThread.SetApartmentState(ApartmentState.STA);
			windowThread.IsBackground = true;
			windowThread.Start();

			windowReadyEvent.WaitOne();

			return window;
		}

		internal ITaskbar CreateTaskbar(ILogger logger)
		{
			return Guard(new Taskbar(logger));
		}

		internal ITaskview CreateTaskview()
		{
			return Guard(new Taskview());
		}

		internal IVerificatorOverlay CreateVerificatorOverlay()
		{
			return Application.Current.Dispatcher.Invoke(() => Guard(new VerificatorOverlay()));
		}
	}
}
