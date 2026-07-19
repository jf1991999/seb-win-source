/*
 * Copyright (c) 2026 ETH Z�rich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using CefSharp;

namespace SafeExamBrowser.Browser.Responsibilities.Browser
{
	internal class CacheResponsibility : BrowserResponsibility
	{
		public CacheResponsibility(BrowserApplicationContext context) : base(context)
		{
		}

		public override void Assume(BrowserTask task)
		{
			switch (task)
			{
				case BrowserTask.InitializeCookies:
					InitializeCookies();
					break;
				case BrowserTask.FinalizeCache:
					FinalizeCache();
					break;
				case BrowserTask.FinalizeCookies:
					FinalizeCookies();
					break;
			}
		}

		private void DeleteCookies()
		{
			var callback = new TaskDeleteCookiesCallback();
			var cookieManager = Cef.GetGlobalCookieManager();

			callback.Task.ContinueWith(task =>
			{
				if (!task.IsCompleted || task.Result == TaskDeleteCookiesCallback.InvalidNoOfCookiesDeleted)
				{
					Logger.Warn("Failed to delete cookies!");
				}
				else
				{
					Logger.Debug($"Deleted {task.Result} cookies.");
				}
			});

			if (cookieManager != default && cookieManager.DeleteCookies(callback: callback))
			{
				Logger.Debug("Successfully initiated cookie deletion.");
			}
			else
			{
				Logger.Warn("Failed to initiate cookie deletion!");
			}
		}

		private void FinalizeCache()
		{
			if (Settings.DeleteCacheOnShutdown && Settings.DeleteCookiesOnShutdown)
			{
				try
				{
					Directory.Delete(AppConfig.BrowserCachePath, true);
					Logger.Info("Deleted browser cache.");
				}
				catch (Exception e)
				{
					Logger.Error("Failed to delete browser cache!", e);
				}
			}
			else
			{
				Logger.Info("Retained browser cache.");
			}
		}

		private void FinalizeCookies()
		{
			// Child-bound session (Focus OR a child-bound lockdown — both carry blinkeredFocusChildId): PRESERVE the
			// child's jar on shutdown too, so the sign-in survives to their next focus/lock (mirrors InitializeCookies).
			// Never clear when a child is bound, even if DeleteCookiesOnShutdown is set.
			if (!string.IsNullOrEmpty(Settings.FocusChildId))
			{
				return;
			}

			if (Settings.DeleteCookiesOnShutdown)
			{
				DeleteCookies();
			}
		}

		private void InitializeCookies()
		{
			// A child-bound session — Focus OR a child-bound lockdown, both of which carry blinkeredFocusChildId and both
			// of which open that child's own Cache-focus-<id> dir (see ConfigurationResponsibility.CachePath) — must
			// PRESERVE the child's jar so they stay signed in across focus<->lock (Romy signs into Alto in Focus ->
			// parent locks Romy -> still signed in). So we never clear or wipe here for a child-bound session, even if
			// DeleteCookiesOnStartup is set (its SEB default is true, and a launch.seb commonly carries
			// examSessionClearCookiesOnStart). Per-child clearing happens only on device unpair (agent WipeProfileCookies).
			if (!string.IsNullOrEmpty(Settings.FocusChildId))
			{
				return;
			}

			// No child bound (home / generic session): DeleteCookiesOnStartup IS the "parent cleared browsing data" signal
			// (examSessionClearCookiesOnStart) -> clear this session's cookies AND every child's Focus cache dir, so a
			// cleared shared device drops all children's sign-ins. A generic session runs on the shared cache, so no Focus
			// dir is in use to delete.
			if (Settings.DeleteCookiesOnStartup)
			{
				DeleteCookies();
				ProfileCookieStore.WipeAllFocusCaches(AppConfig.BrowserCachePath, Logger);
			}
		}

	}
}
