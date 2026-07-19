/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.DevTools;
using SafeExamBrowser.Browser.Wrapper;
using SafeExamBrowser.Browser.Wrapper.Events;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.UserInterface.Contracts.Browser;
using SafeExamBrowser.UserInterface.Contracts.Browser.Data;
using SafeExamBrowser.UserInterface.Contracts.Browser.Events;

namespace SafeExamBrowser.Browser
{
	internal class BrowserControl : IBrowserControl
	{
		private readonly Clipboard clipboard;
		private readonly ICefSharpControl control;
		private readonly IContextMenuHandler contextMenuHandler;
		private readonly IDialogHandler dialogHandler;
		private readonly IDisplayHandler displayHandler;
		private readonly IDownloadHandler downloadHandler;
		private readonly IDragHandler dragHandler;
		private readonly IFocusHandler focusHandler;
		private readonly IJsDialogHandler javaScriptDialogHandler;
		private readonly IKeyboardHandler keyboardHandler;
		private readonly ILogger logger;
		private readonly IRenderProcessMessageHandler renderProcessMessageHandler;
		private readonly IRequestHandler requestHandler;
		private readonly string blinkeredShimSource;
		private readonly string deferredStartUrl;
		private readonly string firstSiteUrl;

		private bool blinkeredShimRegistered;
		private DevToolsClient blinkeredDevTools;

		public string Address => control.Address;
		public bool CanNavigateBackwards => control.IsBrowserInitialized && control.BrowserCore.CanGoBack;
		public bool CanNavigateForwards => control.IsBrowserInitialized && control.BrowserCore.CanGoForward;
		public object EmbeddableControl => control;

		public event AddressChangedEventHandler AddressChanged;
		public event LoadFailedEventHandler LoadFailed;
		public event LoadingStateChangedEventHandler LoadingStateChanged;
		public event TitleChangedEventHandler TitleChanged;

		public BrowserControl(
			Clipboard clipboard,
			ICefSharpControl control,
			IContextMenuHandler contextMenuHandler,
			IDialogHandler dialogHandler,
			IDisplayHandler displayHandler,
			IDownloadHandler downloadHandler,
			IDragHandler dragHandler,
			IFocusHandler focusHandler,
			IJsDialogHandler javaScriptDialogHandler,
			IKeyboardHandler keyboardHandler,
			ILogger logger,
			IRenderProcessMessageHandler renderProcessMessageHandler,
			IRequestHandler requestHandler,
			string blinkeredShimSource,
			string deferredStartUrl,
			string firstSiteUrl)
		{
			this.clipboard = clipboard;
			this.control = control;
			this.contextMenuHandler = contextMenuHandler;
			this.dialogHandler = dialogHandler;
			this.displayHandler = displayHandler;
			this.downloadHandler = downloadHandler;
			this.dragHandler = dragHandler;
			this.focusHandler = focusHandler;
			this.javaScriptDialogHandler = javaScriptDialogHandler;
			this.keyboardHandler = keyboardHandler;
			this.logger = logger;
			this.renderProcessMessageHandler = renderProcessMessageHandler;
			this.requestHandler = requestHandler;
			this.blinkeredShimSource = blinkeredShimSource;
			this.deferredStartUrl = deferredStartUrl;
			this.firstSiteUrl = firstSiteUrl;
		}

		public void Destroy()
		{
			if (control.IsDisposed)
			{
				return;
			}

			// The control is a WinForms control with thread affinity. Blinkered bridge messages can
			// invoke Close()/Destroy() from the CefSharp IPC thread, so marshal the disposal back onto
			// the control's owning (UI) thread to avoid a cross-thread operation. (ICefSharpControl
			// is an ISynchronizeInvoke.) Normal close paths already run on the UI thread (no-op here).
			if (control.InvokeRequired)
			{
				control.Invoke(new Action(Destroy), null);
				return;
			}

			control.CloseDevTools();
			blinkeredDevTools?.Dispose();
			control.Dispose(true);
		}

		public void ExecuteJavaScript(string code, Action<JavaScriptResult> callback = default)
		{
			try
			{
				if (control.BrowserCore != default && control.BrowserCore.MainFrame != default)
				{
					control.BrowserCore.EvaluateScriptAsync(code).ContinueWith(t =>
					{
						callback?.Invoke(new JavaScriptResult { Message = t.Result.Message, Result = t.Result.Result, Success = t.Result.Success });
					});
				}
				else
				{
					Task.Run(() => callback?.Invoke(new JavaScriptResult { Message = "Could not execute JavaScript in main frame!", Success = false }));
				}
			}
			catch (Exception e)
			{
				var message = "Failed to execute JavaScript in main frame!";

				logger.Error(message, e);
				Task.Run(() => callback?.Invoke(new JavaScriptResult { Message = $"{message} Reason: {e.Message}", Success = false }));
			}
		}

		public void Focus()
		{
			// Blinkered: after the main window is activated for a home-page modal (FocusMainContent), hand OS keyboard
			// focus to the CEF browser so the page's own element.focus() lands the caret. CefSharp does not fire the DOM
			// window-focus event, and el.focus() alone can't take keyboard focus while the control isn't OS-focused.
			// BeginInvoke so this runs once the window activation has settled (control is a WinForms CefSharp control —
			// ISynchronizeInvoke). Best-effort: the browser may not be initialized yet on early calls.
			try
			{
				control.BeginInvoke((Action) (() =>
				{
					try
					{
						(control as System.Windows.Forms.Control)?.Focus();
						control.BrowserCore?.GetHost()?.SetFocus(true);
					}
					catch { /* browser not yet initialized / torn down — ignore */ }
				}), null);
			}
			catch (Exception e)
			{
				logger.Warn($"Failed to focus the browser control: {e.Message}");
			}
		}

		public void Find(string term, bool isInitial, bool caseSensitive, bool forward = true)
		{
			control.Find(term, forward, caseSensitive, !isInitial);
		}

		public void Initialize()
		{
			clipboard.Changed += Clipboard_Changed;

			control.AddressChanged += (o, e) => AddressChanged?.Invoke(e.Address);
			control.AuthCredentialsRequired += (w, b, o, i, h, p, r, s, c, a) => a.Value = requestHandler.GetAuthCredentials(w, b, o, i, h, p, r, s, c);
			control.BeforeBrowse += (w, b, f, r, u, i, a) => a.Value = requestHandler.OnBeforeBrowse(w, b, f, r, u, i);
			control.BeforeContextMenu += (w, b, f, p, m) => contextMenuHandler.OnBeforeContextMenu(w, b, f, p, m);
			control.BeforeDownload += (w, b, d, c, a) => a.Value = a.Value = downloadHandler.OnBeforeDownload(w, b, d, c);
			control.BeforeUnloadDialog += (w, b, m, r, c, a) => a.Value = javaScriptDialogHandler.OnBeforeUnloadDialog(w, b, m, r, c);
			control.CanDownload += (w, b, u, r, a) => a.Value = downloadHandler.CanDownload(w, b, u, r);
			control.ContextCreated += (w, b, f) => renderProcessMessageHandler.OnContextCreated(w, b, f);
			control.ContextMenuCommand += (w, b, f, p, c, e, a) => a.Value = contextMenuHandler.OnContextMenuCommand(w, b, f, p, c, e);
			control.ContextMenuDismissed += (w, b, f) => contextMenuHandler.OnContextMenuDismissed(w, b, f);
			control.ContextReleased += (w, b, f) => renderProcessMessageHandler.OnContextReleased(w, b, f);
			control.DialogClosed += (w, b) => javaScriptDialogHandler.OnDialogClosed(w, b);
			control.DownloadUpdated += (w, b, d, c) => downloadHandler.OnDownloadUpdated(w, b, d, c);
			control.DragEnterCefSharp += (w, b, d, m, a) => a.Value = dragHandler.OnDragEnter(w, b, d, m);
			control.DraggableRegionsChanged += (w, b, f, r) => dragHandler.OnDraggableRegionsChanged(w, b, f, r);
			control.FaviconUrlChanged += (w, b, u) => displayHandler.OnFaviconUrlChange(w, b, u);
			control.FileDialogRequested += (w, b, m, t, p, f, e, d, c) => dialogHandler.OnFileDialog(w, b, m, t, p, f, e, d, c);
			control.FocusedNodeChanged += (w, b, f, n) => renderProcessMessageHandler.OnFocusedNodeChanged(w, b, f, n);
			control.GotFocusCefSharp += (w, b) => focusHandler.OnGotFocus(w, b);
			control.IsBrowserInitializedChanged += Control_IsBrowserInitializedChanged;
			control.JavaScriptDialog += (IWebBrowser w, IBrowser b, string u, CefJsDialogType t, string m, string p, IJsDialogCallback c, ref bool s, GenericEventArgs a) => a.Value = javaScriptDialogHandler.OnJSDialog(w, b, u, t, m, p, c, ref s);
			control.KeyEvent += (w, b, t, k, n, m, s) => keyboardHandler.OnKeyEvent(w, b, t, k, n, m, s);
			control.LoadError += (o, e) => LoadFailed?.Invoke((int) e.ErrorCode, e.ErrorText, e.Frame.IsMain, e.FailedUrl);
			control.LoadingProgressChanged += (w, b, p) => displayHandler.OnLoadingProgressChange(w, b, p);
			control.LoadingStateChanged += (o, e) =>
			{
				LoadingStateChanged?.Invoke(e.IsLoading);

				// Blinkered launch reveal: feed every window's load start/end into the bridge's quiescence tracker, which
				// signals the runtime to reveal only once ALL windows have painted (not on a single matched URL).
				BlinkeredBridge.ReportLoadingStateChanged(e.IsLoading);
			};
			control.OpenUrlFromTab += (w, b, f, u, t, g, a) => a.Value = requestHandler.OnOpenUrlFromTab(w, b, f, u, t, g);
			control.PreKeyEvent += (IWebBrowser w, IBrowser b, KeyType t, int k, int n, CefEventFlags m, bool i, ref bool s, GenericEventArgs a) => a.Value = keyboardHandler.OnPreKeyEvent(w, b, t, k, n, m, i, ref s);
			control.ResetDialogState += (w, b) => javaScriptDialogHandler.OnResetDialogState(w, b);
			control.ResourceRequestHandlerRequired += (IWebBrowser w, IBrowser b, IFrame f, IRequest r, bool n, bool d, string i, ref bool h, ResourceRequestEventArgs a) => a.Handler = requestHandler.GetResourceRequestHandler(w, b, f, r, n, d, i, ref h);
			control.RunContextMenu += (w, b, f, p, m, c, a) => a.Value = contextMenuHandler.RunContextMenu(w, b, f, p, m, c);
			control.SetFocus += (w, b, s, a) => a.Value = focusHandler.OnSetFocus(w, b, s);
			control.TakeFocus += (w, b, n) => focusHandler.OnTakeFocus(w, b, n);
			control.TitleChanged += (o, e) => TitleChanged?.Invoke(e.Title);
			control.UncaughtExceptionEvent += (w, b, f, e) => renderProcessMessageHandler.OnUncaughtException(w, b, f, e);

			if (control is IWebBrowser webBrowser)
			{
				webBrowser.JavascriptMessageReceived += WebBrowser_JavascriptMessageReceived;
			}
		}

		public void NavigateBackwards()
		{
			control.BrowserCore.GoBack();
		}

		public void NavigateForwards()
		{
			control.BrowserCore.GoForward();
		}

		public void NavigateTo(string address)
		{
			control.Load(address);
		}

		public void ShowDeveloperConsole()
		{
			control.BrowserCore.ShowDevTools();
		}

		public void Reload()
		{
			control.BrowserCore.Reload();
		}

		public void Zoom(double level)
		{
			control.BrowserCore.SetZoomLevel(level);
		}

		private void Clipboard_Changed(string id)
		{
			try
			{
				var script = $"SafeExamBrowser.clipboard.update('{id}', '{clipboard.Content}');";

				foreach (var frame in control.BrowserCore?.GetAllFrames() ?? Enumerable.Empty<IFrame>())
				{
					frame.EvaluateScriptAsync(script);
				}
			}
			catch (Exception e)
			{
				logger.Error($"Failed to update JavaScript clipboard!", e);
			}
		}

		private async void Control_IsBrowserInitializedChanged(object sender, EventArgs e)
		{
			if (!control.IsBrowserInitialized)
			{
				return;
			}

			// Blinkered: register the bridge shim as a document-start script so window.webkit.messageHandlers
			// .blinkered is guaranteed present before any page script - including on warm-cache (instant) loads,
			// which the old per-context injection lost the race to. This MUST complete before the start URL
			// navigates, so the main window is constructed on about:blank and only loads the real start URL here,
			// after the shim is registered. Popups have no deferred start URL (CEF drives their navigation).
			await RegisterBlinkeredShimAsync();

			// Blinkered launch speedup: register the document-start first-site early-open BEFORE the start URL navigates
			// (same rationale as the shim — AddScriptToEvaluateOnNewDocument applies to documents created after this call,
			// so it runs on the real home document from the Load below, not the initial about:blank).
			await RegisterFirstSiteEarlyOpenAsync();

			if (deferredStartUrl != null)
			{
				control.Load(deferredStartUrl);
			}

			control.BrowserCore.GetHost().SetFocus(true);
		}

		private async Task RegisterBlinkeredShimAsync()
		{
			if (blinkeredShimRegistered || string.IsNullOrEmpty(blinkeredShimSource))
			{
				return;
			}

			try
			{
				// Keep the DevTools client alive for this control's lifetime (disposed in Destroy): the
				// document-start script is registered on this CDP session and could be detached if the client is
				// disposed early. Registering once is enough - it persists across navigations and reloads.
				blinkeredDevTools = control.BrowserCore.GetDevToolsClient();

				// The Page domain must be enabled for addScriptToEvaluateOnNewDocument to actually inject.
				await blinkeredDevTools.Page.EnableAsync();
				await blinkeredDevTools.Page.AddScriptToEvaluateOnNewDocumentAsync(blinkeredShimSource);

				blinkeredShimRegistered = true;
				logger.Debug("[Blinkered] Registered the document-start bridge shim.");
			}
			catch (Exception ex)
			{
				logger.Error("[Blinkered] Failed to register the document-start bridge shim.", ex);
			}
		}

		private async Task RegisterFirstSiteEarlyOpenAsync()
		{
			if (string.IsNullOrEmpty(firstSiteUrl) || blinkeredDevTools == null)
			{
				return;
			}

			try
			{
				// Document-start (synchronous, before ANY page script): sets __blinkeredFirstSiteHandled so home-content's
				// whenBlinkeredReady -> setActiveTab(0) reliably sees it (suppression wins the race — no queued-eval slip), and
				// fires window.open at true document-start so the site loads parallel with the shell. sessionStorage makes it
				// launch-only (survives reloads -> no duplicate on reload); guards exclude subframes + about:blank.
				var url = Newtonsoft.Json.JsonConvert.SerializeObject(firstSiteUrl);
				var script =
					"(function(){try{" +
					"if(window.top!==window||/^about:/i.test(location.href))return;" +
					"try{if(sessionStorage.getItem('__blinkeredFirstSiteDone'))return;sessionStorage.setItem('__blinkeredFirstSiteDone','1');}catch(e){return;}" +
					"window.__blinkeredFirstSiteHandled=true;" +
					"try{window.open(" + url + ");}catch(e){}" +
					"}catch(e){}})();";
				await blinkeredDevTools.Page.AddScriptToEvaluateOnNewDocumentAsync(script);
				logger.Info($"[Blinkered] launch speedup: registered document-start first-site early-open for '{firstSiteUrl}' (flag + window.open, launch-only).");
			}
			catch (System.Exception ex)
			{
				logger.Error("[Blinkered] launch speedup: failed to register the first-site early-open script.", ex);
			}
		}

		private void WebBrowser_JavascriptMessageReceived(object sender, JavascriptMessageReceivedEventArgs e)
		{
			clipboard.Update(e);
		}
	}
}
