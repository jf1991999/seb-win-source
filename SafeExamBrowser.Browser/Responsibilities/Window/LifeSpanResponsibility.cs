/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using CefSharp.WinForms.Handler;
using CefSharp.WinForms.Host;
using SafeExamBrowser.Browser.Events;
using SafeExamBrowser.Settings.Browser;

namespace SafeExamBrowser.Browser.Responsibilities.Window
{
	internal class LifeSpanResponsibility : WindowResponsibility
	{
		private readonly Dictionary<int, BrowserWindow> popups;

		internal event PopupRequestedEventHandler PopupRequested;

		public LifeSpanResponsibility(BrowserWindowContext context) : base(context)
		{
			popups = new Dictionary<int, BrowserWindow>();
		}

		public override void Assume(WindowTask task)
		{
			if (task == WindowTask.InitializeLifeSpanHandler)
			{
				Initialize();
			}
		}

		private void Initialize()
		{
			Context.LifeSpanHandler = LifeSpanHandler
				.Create(() => LifeSpanHandler_CreatePopup())
				.OnBeforePopupCreated((wb, b, f, u, t, d, g, s) => LifeSpanHandler_PopupRequested(u, g))
				.OnPopupCreated((c, u) => LifeSpanHandler_PopupCreated(c, u))
				.OnPopupDestroyed((c, b) => LifeSpanHandler_PopupDestroyed(c))
				.Build();
		}

		private ChromiumHostControl LifeSpanHandler_CreatePopup()
		{
			var args = new PopupRequestedEventArgs();

			PopupRequested?.Invoke(args);

			var control = args.Window.Control.EmbeddableControl as ChromiumHostControl;
			var id = control.GetHashCode();
			var window = args.Window;

			popups[id] = window;
			window.Closed += (_) => popups.Remove(id);

			return control;
		}

		private void LifeSpanHandler_PopupCreated(ChromiumHostControl control, string targetUrl)
		{
			var id = control.GetHashCode();
			var window = popups[id];

			// Record the URL the window was opened FOR (the window.open / tab target) as its stable tab identity, before
			// the page can navigate/redirect. BlinkeredWindowController.FindByUrl matches on this so a tab-switch back to
			// a site that redirected cross-host (gmail.com -> mail.google.com) still finds the existing window instead of
			// falling through to a spurious reopen (window.open) that reloads the page.
			window.SetOriginatingUrl(targetUrl);

			window.Show();
		}

		private void LifeSpanHandler_PopupDestroyed(ChromiumHostControl control)
		{
			var id = control.GetHashCode();
			var window = popups[id];

			window.Close();
		}

		private PopupCreation LifeSpanHandler_PopupRequested(string targetUrl, bool userGesture)
		{
			var creation = PopupCreation.Cancel;
			var validCurrentUri = Uri.TryCreate(Control.Address, UriKind.Absolute, out var currentUri);
			var validNewUri = Uri.TryCreate(targetUrl, UriKind.Absolute, out var newUri);
			var sameHost = validCurrentUri && validNewUri && string.Equals(currentUri.Host, newUri.Host, StringComparison.OrdinalIgnoreCase);

			// userGesture=false is the launch auto-open path (programmatic window.open). With Chromium's popup
			// blocker disabled (disable-popup-blocking) these now reach us instead of being dropped by Blink, so the
			// decision below is authoritative for both the gesture-carrying click and the gesture-less launch reopen.
			Logger.Info($"[Blinkered] OnBeforePopup{(WindowSettings.UrlPolicy.CanLog() ? $" '{targetUrl}'" : "")} userGesture={userGesture} sameHost={sameHost} policy={Settings.PopupPolicy}.");

			switch (Settings.PopupPolicy)
			{
				case PopupPolicy.Allow:
				case PopupPolicy.AllowSameHost when sameHost:
					Logger.Debug($"Forwarding request to open new window{(WindowSettings.UrlPolicy.CanLog() ? $" for '{targetUrl}'" : "")}...");
					creation = PopupCreation.Continue;
					break;
				case PopupPolicy.AllowSameWindow:
				case PopupPolicy.AllowSameHostAndWindow when sameHost:
					Logger.Info($"Discarding request to open new window and loading{(WindowSettings.UrlPolicy.CanLog() ? $" '{targetUrl}'" : "")} directly...");
					Control.NavigateTo(targetUrl);
					break;
				case PopupPolicy.AllowSameHost when !sameHost:
				case PopupPolicy.AllowSameHostAndWindow when !sameHost:
					Logger.Info($"Blocked request to open new window{(WindowSettings.UrlPolicy.CanLog() ? $" for '{targetUrl}'" : "")} as it targets a different host.");
					break;
				default:
					Logger.Info($"Blocked request to open new window{(WindowSettings.UrlPolicy.CanLog() ? $" for '{targetUrl}'" : "")}.");
					break;
			}

			return creation;
		}
	}
}
