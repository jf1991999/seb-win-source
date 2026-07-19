/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SafeExamBrowser.Browser.Contracts.Events;
using SafeExamBrowser.Core.Contracts.Resources.Icons;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Settings.Browser;
using SafeExamBrowser.UserInterface.Contracts;
using SafeExamBrowser.UserInterface.Contracts.Browser;
using SafeExamBrowser.UserInterface.Contracts.Browser.Data;
using SafeExamBrowser.UserInterface.Contracts.Browser.Events;
using SafeExamBrowser.UserInterface.Contracts.Windows;
using SafeExamBrowser.UserInterface.Contracts.Windows.Events;
using SafeExamBrowser.UserInterface.Desktop.Controls.Browser;
using SafeExamBrowser.UserInterface.Shared.Utilities;

namespace SafeExamBrowser.UserInterface.Desktop.Windows
{
	internal partial class BrowserWindow : Window, IBrowserWindow
	{
		private const string CLEAR_FIND_TERM = "thisisahacktoclearthesearchresultsasitappearsthatthereisnosuchfunctionalityincef";

		private readonly bool isMainWindow;
		private readonly BrowserSettings settings;
		private readonly IText text;
		private readonly ILogger logger;
		private readonly IBrowserControl browserControl;

		// #2 (launch cleanup): the fullscreen main window is shown OFF-SCREEN so CefSharp realizes + loads the start URL
		// without the transient about:blank + SEB chrome flashing on screen; it is revealed (Left -> 0) on the first real
		// content load. A WinForms-hosted CEF control can't be hidden by WPF opacity/overlay (airspace), so off-screen is
		// the mechanism. Only applies to the fullscreen main window.
		private const double OffScreenLeft = -32000;
		private bool pendingReveal;
		// Reveal only once loading has SETTLED: a load-finished on a real address arms this; a new navigation
		// (isLoading=true) cancels it. So an intermediate/redirect load can't reveal the window mid-transition.
		private System.Windows.Threading.DispatcherTimer revealTimer;

		// #3: the Taskbar window suppresses itself off-screen during launch and reveals once the MAIN browser content
		// is up, so the SEB taskbar chrome doesn't flash before the lock page. Light static handoff within this assembly.
		internal static event Action MainContentRevealed;
		internal static bool HasMainContentRevealed { get; private set; }
		// #3: true once the main fullscreen window has gone off-screen to load, before it reveals — lets the launch
		// splash know a reveal is coming, so it HOLDS instead of hiding into a black load-gap while the page loads.
		internal static bool MainContentPending { get; private set; }

		private WindowClosedEventHandler closed;
		private WindowClosingEventHandler closing;
		private bool browserControlGetsFocusFromTaskbar;
		private IInputElement tabKeyDownFocusElement;

		private WindowSettings WindowSettings
		{
			get { return isMainWindow ? settings.MainWindow : settings.AdditionalWindow; }
		}

		public bool CanNavigateBackwards { set => Dispatcher.Invoke(() => BackwardButton.IsEnabled = value); }
		public bool CanNavigateForwards { set => Dispatcher.Invoke(() => ForwardButton.IsEnabled = value); }
		public IntPtr Handle { get; private set; }

		public event AddressChangedEventHandler AddressChanged;
		public event ActionRequestedEventHandler BackwardNavigationRequested;
		public event ActionRequestedEventHandler DeveloperConsoleRequested;
		public event FindRequestedEventHandler FindRequested;
		public event ActionRequestedEventHandler ForwardNavigationRequested;
		public event ActionRequestedEventHandler HomeNavigationRequested;
		public event ActionRequestedEventHandler ReloadRequested;
		public event ActionRequestedEventHandler ZoomInRequested;
		public event ActionRequestedEventHandler ZoomOutRequested;
		public event ActionRequestedEventHandler ZoomResetRequested;
		public event LoseFocusRequestedEventHandler LoseFocusRequested;

		event WindowClosedEventHandler IWindow.Closed
		{
			add { closed += value; }
			remove { closed -= value; }
		}

		event WindowClosingEventHandler IWindow.Closing
		{
			add { closing += value; }
			remove { closing -= value; }
		}

		internal BrowserWindow(IBrowserControl browserControl, BrowserSettings settings, bool isMainWindow, IText text, ILogger logger)
		{
			this.browserControl = browserControl;
			this.isMainWindow = isMainWindow;
			this.logger = logger;
			this.settings = settings;
			this.text = text;

			InitializeComponent();
			InitializeBrowserWindow(browserControl);
		}

		public void BringToForeground()
		{
			Dispatcher.Invoke(() =>
			{
				if (WindowState == WindowState.Minimized)
				{
					WindowState = WindowState.Normal;
				}

				// WPF Activate()/Topmost alone do not reliably raise a window above a sibling in the kiosk /
				// isolated desktop (Windows foreground restrictions): a just-opened or re-focused site window stays
				// hidden behind the home (main) window, so the user sees the "open in a separate window" notice / a
				// black screen and has to click. Use the Win32 topmost-toggle that works for the accent popup +
				// offset windows: raise to HWND_TOPMOST, drop to HWND_NOTOPMOST (leaves it at the FRONT of the
				// non-topmost band without staying pinned), then SetForegroundWindow to activate it. The Client owns
				// the kiosk desktop's foreground, so SetForegroundWindow is permitted.
				var handle = new WindowInteropHelper(this).Handle;
				var before = GetForegroundWindow();

				if (handle != IntPtr.Zero)
				{
					SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

					// Only drop back out of the topmost band if this window isn't meant to STAY topmost. Home-mode
					// site windows are persistently topmost (SetAlwaysOnTop / SetTopInset) so the main window can't
					// surface over them; dropping them to NOTOPMOST here would reintroduce the black flash.
					if (!Topmost)
					{
						SetWindowPos(handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
					}

					SetForegroundWindow(handle);
				}

				Activate();
				Focus();

				// Diagnostics: did we actually take the foreground, and does another window (the topmost home/main)
				// re-assert it right after? Compare the first-open path vs the working tab-switch path.
				var after = GetForegroundWindow();
				logger.Info($"[Foreground] BringToForeground main={isMainWindow} hwnd={handle} topmost={Topmost} fg {before}->{after} gotForeground={after == handle}");

				Task.Delay(250).ContinueWith(_ => Dispatcher.Invoke(() =>
				{
					var later = GetForegroundWindow();
					logger.Info($"[Foreground] +250ms main={isMainWindow} hwnd={handle} fg={later} stillForeground={later == handle}");
				}), TaskScheduler.Default);
			});
		}

		private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
		private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
		private const uint SWP_NOSIZE = 0x0001;
		private const uint SWP_NOMOVE = 0x0002;
		private const uint SWP_NOACTIVATE = 0x0010;

		[DllImport("user32.dll")]
		private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

		[DllImport("user32.dll")]
		private static extern bool SetForegroundWindow(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		public new void Close()
		{
			Dispatcher.Invoke(() =>
			{
				Closing -= BrowserWindow_Closing;
				closing?.Invoke();
				base.Close();
			});
		}

		public void FocusToolbar(bool forward)
		{
			Dispatcher.BeginInvoke((Action) (async () =>
			{
				Activate();
				await Task.Delay(50);

				// focus all elements in the toolbar, such that the last element that is enabled gets focus
				var buttons = new System.Windows.Controls.Control[] { ForwardButton, BackwardButton, ReloadButton, UrlTextBox, MenuButton, };
				for (var i = forward ? 0 : buttons.Length - 1; i >= 0 && i < buttons.Length; i += forward ? 1 : -1)
				{
					if (buttons[i].IsEnabled && buttons[i].Visibility == Visibility.Visible)
					{
						buttons[i].Focus();
						break;
					}
				}
			}));
		}

		public void FocusBrowser()
		{
			Dispatcher.BeginInvoke((Action) (async () =>
			{
				FocusToolbar(false);
				await Task.Delay(100);

				browserControlGetsFocusFromTaskbar = true;

				var focusedElement = FocusManager.GetFocusedElement(this) as UIElement;
				focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Right));

				await Task.Delay(150);
				browserControlGetsFocusFromTaskbar = false;
			}));
		}

		public void FocusAddressBar()
		{
			Dispatcher.BeginInvoke((Action) (() =>
			{
				UrlTextBox.Focus();
			}));
		}

		public new void Hide()
		{
			Dispatcher.Invoke(base.Hide);
		}

		public new void Show()
		{
			Dispatcher.Invoke(base.Show);
		}

		public void ShowFindbar()
		{
			Dispatcher.InvokeAsync(() =>
			{
				Findbar.Visibility = Visibility.Visible;
				FindTextBox.Focus();
			});
		}

		public void UpdateAddress(string url)
		{
			Dispatcher.Invoke(() => UrlTextBox.Text = url);
		}

		public void UpdateIcon(IconResource icon)
		{
			Dispatcher.InvokeAsync(() =>
			{
				if (icon is BitmapIconResource bitmap)
				{
					Icon = new BitmapImage(bitmap.Uri);
				}
			});
		}

		public void UpdateDownloadState(DownloadItemState state)
		{
			Dispatcher.InvokeAsync(() =>
			{
				var control = Downloads.Children.OfType<DownloadItemControl>().FirstOrDefault(c => c.Id == state.Id);

				if (control == default)
				{
					control = new DownloadItemControl(state.Id, text);
					Downloads.Children.Add(control);
				}

				control.Update(state);
				DownloadsButton.Visibility = Visibility.Visible;
				DownloadsPopup.IsOpen = IsActive;
			});
		}

		public void UpdateLoadingState(bool isLoading)
		{
			Dispatcher.Invoke(() =>
			{
				ProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Hidden;

				if (!pendingReveal)
				{
					return;
				}

				// #2/#3: reveal the off-screen main window only once loading has SETTLED on a real address — NOT on the
				// first non-blank load-finished, which can be an intermediate/redirect (login bounce, the lock page's
				// initial hop) that would reveal the window mid-transition with the chrome still up. A load-finished on a
				// real address arms a short debounce; a new navigation (isLoading=true) cancels it. The reveal fires only
				// when nothing has navigated for the debounce window. The 15s safety-net (BrowserWindow_Loaded) still wins
				// if the page never settles.
				if (isLoading)
				{
					logger.Perf("Content: first nav");
					revealTimer?.Stop();
				}
				else if (!IsBlankAddress(browserControl.Address))
				{
					logger.Perf("Content: load finished");
					revealTimer?.Stop();
					revealTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
					revealTimer.Tick += (o, e) => { revealTimer.Stop(); RevealAfterLoad(); };
					revealTimer.Start();
				}
			});
		}

		private static bool IsBlankAddress(string address)
		{
			return string.IsNullOrEmpty(address) || address.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
		}

		private void RevealAfterLoad()
		{
			if (!pendingReveal)
			{
				return;
			}

			pendingReveal = false;
			revealTimer?.Stop();

			// #3: reveal IN PLACE, not with a visible slide. Moving a shown fullscreen window from off-screen
			// (Left -32000 -> 0) was being composited as a horizontal slide across the screen. Hide the window, move it
			// to the origin while hidden, then show it — the content is already loaded + painted off-screen, so it
			// appears directly at its final position with no on-screen travel (and no white flash, since the CEF surface
			// is already rendered).
			Visibility = Visibility.Hidden;
			Left = 0;
			Dispatcher.BeginInvoke((Action) (() =>
			{
				Visibility = Visibility.Visible;
				BringToForeground();
				logger.Info($"[Blinkered] main window revealed in place after first real load (address='{browserControl.Address}') — no transient window and no slide.");

				// #3: hand off to the Taskbar window + the launch splash so they appear/disappear together with the
				// content, not before it.
				MainContentPending = false;
				HasMainContentRevealed = true;
				MainContentRevealed?.Invoke();

				// #4: one explicit TOTAL launch-duration line for clean before/after (no noisy phase math). Measured from
				// the Runtime process start (the first Blinkered process the agent launches on lock) to this reveal — the
				// full lock->Alto-content time, in-process, no cross-log diffing.
				try
				{
					var runtime = System.Diagnostics.Process.GetProcessesByName("SafeExamBrowser").FirstOrDefault();
					if (runtime != null)
					{
						logger.Info($"[Launch] TOTAL lock->content {(DateTime.Now - runtime.StartTime).TotalSeconds:0.0}s (Runtime start -> Alto revealed).");
						logger.Perf("Content: revealed");
					}
				}
				catch { }
			}), System.Windows.Threading.DispatcherPriority.Render);
		}

		public void UpdateProgress(double value)
		{
			Dispatcher.Invoke(() => ProgressBar.Value = value * 100);
		}

		public void UpdateTitle(string title)
		{
			Dispatcher.Invoke(() => Title = title);
		}

		public void UpdateZoomLevel(double value)
		{
			Dispatcher.Invoke(() =>
			{
				ZoomLevel.Text = $"{value}%";
				var zoomButtonName = text.Get(TextKey.BrowserWindow_ZoomLevelReset).Replace("%%ZOOM%%", value.ToString("0"));
				ZoomResetButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, zoomButtonName);
			});
		}

		private void BrowserWindow_Closing(object sender, CancelEventArgs e)
		{
			if (isMainWindow)
			{
				e.Cancel = true;
			}
			else
			{
				closing?.Invoke();
			}
		}

		private void BrowserWindow_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Tab)
			{
				var hasShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

				if (Toolbar.IsKeyboardFocusWithin && hasShift)
				{
					var firstActiveElementInToolbar = Toolbar.PredictFocus(FocusNavigationDirection.Right);

					if (firstActiveElementInToolbar is UIElement)
					{
						var control = firstActiveElementInToolbar as UIElement;

						if (control.IsKeyboardFocusWithin)
						{
							LoseFocusRequested?.Invoke(false);
							e.Handled = true;
						}
					}
				}

				tabKeyDownFocusElement = FocusManager.GetFocusedElement(this);
			}
			else
			{
				tabKeyDownFocusElement = null;
			}
		}

		private void BrowserWindow_KeyUp(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.F5)
			{
				ReloadRequested?.Invoke();
			}

			if (e.Key == Key.Home)
			{
				HomeNavigationRequested?.Invoke();
			}

			if (settings.AllowFind && (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) && e.Key == Key.F)
			{
				ShowFindbar();
			}

			if (e.Key == Key.Tab)
			{
				var hasCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
				var hasShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

				if (BrowserControlHost.IsFocused && hasCtrl)
				{
					if (Findbar.Visibility == Visibility.Hidden || hasShift)
					{
						Toolbar.Focus();
					}
					else if (Toolbar.Visibility == Visibility.Hidden)
					{
						Findbar.Focus();
					}
				}
				else if (MenuPopup.IsKeyboardFocusWithin)
				{
					var focusedElement = FocusManager.GetFocusedElement(this);

					if (focusedElement is Control focusedControl && tabKeyDownFocusElement is Control prevFocusedControl)
					{
						if (!hasShift && focusedControl.TabIndex < prevFocusedControl.TabIndex)
						{
							MenuPopup.IsOpen = false;
							FocusBrowser();
						}
						else if (hasShift && focusedControl.TabIndex > prevFocusedControl.TabIndex)
						{
							MenuPopup.IsOpen = false;
							MenuButton.Focus();
						}
					}
				}
			}

			if (e.Key == Key.Escape && MenuPopup.IsOpen)
			{
				MenuPopup.IsOpen = false;
				MenuButton.Focus();
			}
		}

		private void BrowserWindow_Loaded(object sender, RoutedEventArgs e)
		{
			Handle = new WindowInteropHelper(this).Handle;

			if (isMainWindow)
			{
				this.DisableCloseButton();
			}

			if (pendingReveal)
			{
				// #2 safety net: if the start URL never finishes loading (network error / hang), reveal anyway after a
				// bounded wait so the user is never left on a blank kiosk desktop with the window stuck off-screen.
				Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Dispatcher.Invoke(RevealAfterLoad));
			}
		}

		private void FindbarCloseButton_Click(object sender, RoutedEventArgs e)
		{
			FindRequested?.Invoke(CLEAR_FIND_TERM, true, false);
			Findbar.Visibility = Visibility.Collapsed;
		}

		private void FindNextButton_Click(object sender, RoutedEventArgs e)
		{
			FindRequested?.Invoke(FindTextBox.Text, false, FindCaseSensitiveCheckBox.IsChecked == true);
		}

		private void FindPreviousButton_Click(object sender, RoutedEventArgs e)
		{
			FindRequested?.Invoke(FindTextBox.Text, false, FindCaseSensitiveCheckBox.IsChecked == true, false);
		}

		private void FindTextBox_KeyUp(object sender, KeyEventArgs e)
		{
			if (string.IsNullOrEmpty(FindTextBox.Text))
			{
				FindRequested?.Invoke(CLEAR_FIND_TERM, true, false);
			}
			else if (e.Key == Key.Enter)
			{
				FindRequested?.Invoke(FindTextBox.Text, false, FindCaseSensitiveCheckBox.IsChecked == true);
			}
			else
			{
				FindRequested?.Invoke(FindTextBox.Text, true, FindCaseSensitiveCheckBox.IsChecked == true);
			}
		}

		private CustomPopupPlacement[] Popup_PlacementCallback(Size popupSize, Size targetSize, Point offset)
		{
			return new[]
			{
				new CustomPopupPlacement(new Point(targetSize.Width - Toolbar.Margin.Right - popupSize.Width, -2), PopupPrimaryAxis.None)
			};
		}

		private void SystemParameters_StaticPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(SystemParameters.WorkArea))
			{
				Dispatcher.InvokeAsync(InitializeBounds);
			}
		}

		private void UrlTextBox_GotMouseCapture(object sender, MouseEventArgs e)
		{
			if (UrlTextBox.Tag as bool? != true)
			{
				UrlTextBox.SelectAll();
				UrlTextBox.Tag = true;
			}
		}

		private void UrlTextBox_KeyUp(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter)
			{
				AddressChanged?.Invoke(UrlTextBox.Text);
			}
		}

		private void InitializeBrowserWindow(IBrowserControl browserControl)
		{
			if (browserControl.EmbeddableControl is System.Windows.Forms.Control control)
			{
				BrowserControlHost.Child = control;
			}

			RegisterEvents();
			InitializeBounds();
			ApplySettings();
			LoadIcons();
			LoadText();
		}

		private void RegisterEvents()
		{
			BackwardButton.Click += (o, args) => BackwardNavigationRequested?.Invoke();
			Closed += (o, args) => closed?.Invoke();
			Closing += BrowserWindow_Closing;
			DeveloperConsoleButton.Click += (o, args) => DeveloperConsoleRequested?.Invoke();
			DownloadsButton.Click += (o, args) => DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
			DownloadsButton.MouseLeave += (o, args) => Task.Delay(250).ContinueWith(_ => Dispatcher.Invoke(() => DownloadsPopup.IsOpen = DownloadsPopup.IsMouseOver));
			DownloadsPopup.CustomPopupPlacementCallback = new CustomPopupPlacementCallback(Popup_PlacementCallback);
			DownloadsPopup.MouseLeave += (o, args) => Task.Delay(250).ContinueWith(_ => Dispatcher.Invoke(() => DownloadsPopup.IsOpen = DownloadsPopup.IsMouseOver));
			FindbarCloseButton.Click += FindbarCloseButton_Click;
			FindNextButton.Click += FindNextButton_Click;
			FindPreviousButton.Click += FindPreviousButton_Click;
			FindMenuButton.Click += (o, args) => ShowFindbar();
			FindTextBox.KeyUp += FindTextBox_KeyUp;
			ForwardButton.Click += (o, args) => ForwardNavigationRequested?.Invoke();
			HomeButton.Click += (o, args) => HomeNavigationRequested?.Invoke();
			Loaded += BrowserWindow_Loaded;
			Activated += (o, args) => logger.Info($"[Foreground] ACTIVATED main={isMainWindow} hwnd={new WindowInteropHelper(this).Handle}");
			Deactivated += (o, args) => logger.Info($"[Foreground] DEACTIVATED main={isMainWindow} hwnd={new WindowInteropHelper(this).Handle}");
			MenuButton.Click += MenuButton_Click;
			MenuPopup.CustomPopupPlacementCallback = new CustomPopupPlacementCallback(Popup_PlacementCallback);
			MenuPopup.LostFocus += (o, args) => Task.Delay(250).ContinueWith(_ => Dispatcher.Invoke(() => MenuPopup.IsOpen = MenuPopup.IsKeyboardFocusWithin));
			KeyDown += BrowserWindow_KeyDown;
			KeyUp += BrowserWindow_KeyUp;
			LocationChanged += (o, args) => { DownloadsPopup.IsOpen = false; MenuPopup.IsOpen = false; };
			ReloadButton.Click += (o, args) => ReloadRequested?.Invoke();
			SizeChanged += (o, args) => { DownloadsPopup.IsOpen = false; MenuPopup.IsOpen = false; };
			SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
			UrlTextBox.GotKeyboardFocus += (o, args) => UrlTextBox.SelectAll();
			UrlTextBox.GotMouseCapture += UrlTextBox_GotMouseCapture;
			UrlTextBox.LostKeyboardFocus += (o, args) => UrlTextBox.Tag = null;
			UrlTextBox.LostFocus += (o, args) => UrlTextBox.Tag = null;
			UrlTextBox.KeyUp += UrlTextBox_KeyUp;
			UrlTextBox.MouseDoubleClick += (o, args) => UrlTextBox.SelectAll();
			ZoomInButton.Click += (o, args) => ZoomInRequested?.Invoke();
			ZoomOutButton.Click += (o, args) => ZoomOutRequested?.Invoke();
			ZoomResetButton.Click += (o, args) => ZoomResetRequested?.Invoke();
			BrowserControlHost.GotKeyboardFocus += BrowserControlHost_GotKeyboardFocus;
		}

		private void MenuButton_Click(object sender, RoutedEventArgs e)
		{
			MenuPopup.IsOpen = !MenuPopup.IsOpen;
			ZoomInButton.Focus();
		}

		private void BrowserControlHost_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
		{
			var forward = !browserControlGetsFocusFromTaskbar;

			// focus the first / last element on the page
			var javascript = @"
if (typeof __SEB_focusElement === 'undefined') {
  __SEB_focusElement = function (forward) {
	if (!document.body)
		return;
	var items = [].map
	  .call(document.body.querySelectorAll(['input', 'select', 'a[href]', 'textarea', 'button', '[tabindex]']), function(el, i) { return { el, i } })
	  .filter(function(e) { return e.el.tabIndex >= 0 && !e.el.disabled && e.el.offsetParent; })
	  .sort(function(a,b) { return a.el.tabIndex === b.el.tabIndex ? a.i - b.i : (a.el.tabIndex || 9E9) - (b.el.tabIndex || 9E9); })
	var item = items[forward ? 1 : items.length - 1];
	if (item && item.focus && typeof item.focus !== 'function')
		throw ('item.focus is not a function, ' + typeof item.focus)
	setTimeout(function () { item && item.focus && item.focus(); }, 20);
  }
}";
			browserControl.ExecuteJavaScript(javascript, result =>
			{
				if (!result.Success)
				{
					logger.Warn($"Failed to initialize JavaScript: {result.Message}");
				}
			});

			browserControl.ExecuteJavaScript("__SEB_focusElement(" + forward.ToString().ToLower() + ")", result =>
			{
				if (!result.Success)
				{
					logger.Warn($"Failed to execute JavaScript: {result.Message}");
				}
			});
		}

		private void ApplySettings()
		{
			BackwardButton.IsEnabled = WindowSettings.AllowBackwardNavigation;
			BackwardButton.Visibility = WindowSettings.AllowBackwardNavigation ? Visibility.Visible : Visibility.Collapsed;
			DeveloperConsoleMenuItem.Visibility = WindowSettings.AllowDeveloperConsole ? Visibility.Visible : Visibility.Collapsed;
			FindMenuItem.Visibility = settings.AllowFind ? Visibility.Visible : Visibility.Collapsed;
			ForwardButton.IsEnabled = WindowSettings.AllowForwardNavigation;
			ForwardButton.Visibility = WindowSettings.AllowForwardNavigation ? Visibility.Visible : Visibility.Collapsed;
			HomeButton.IsEnabled = WindowSettings.ShowHomeButton;
			HomeButton.Visibility = WindowSettings.ShowHomeButton ? Visibility.Visible : Visibility.Collapsed;
			ReloadButton.IsEnabled = WindowSettings.AllowReloading;
			ReloadButton.Visibility = WindowSettings.ShowReloadButton ? Visibility.Visible : Visibility.Collapsed;
			Toolbar.Visibility = WindowSettings.ShowToolbar ? Visibility.Visible : Visibility.Collapsed;
			UrlTextBox.Visibility = WindowSettings.AllowAddressBar ? Visibility.Visible : Visibility.Hidden;
			ZoomMenuItem.Visibility = settings.AllowPageZoom ? Visibility.Visible : Visibility.Collapsed;
		}

		public void SetTopInset(int height)
		{
			// Home mode only: the Blinkered home page reports its tab-bar height via the bridge. Additional
			// (site) windows are made chromeless and dropped below the bar so it stays visible. The main window
			// (which renders the bar) and exam-mode popups (height == 0, no bridge) are left untouched.
			if (isMainWindow || height <= 0)
			{
				return;
			}

			Dispatcher.Invoke(() =>
			{
				WindowStyle = WindowStyle.None;
				ResizeMode = ResizeMode.NoResize;
				WindowState = WindowState.Normal;
				Left = 0;
				Top = height;
				Width = SystemParameters.WorkArea.Width;
				Height = Math.Max(0, SystemParameters.WorkArea.Height - height);

				// Keep the site window pinned above the (non-topmost) main window. The main window renders the tab bar
				// and briefly activates on a tab-bar mouse-down (~140ms); if the site window weren't topmost, that
				// activation would raise the main window's empty body over it — the black flash. See SetAlwaysOnTop.
				Topmost = true;
			});
		}

		public void SetAlwaysOnTop(bool alwaysOnTop)
		{
			Dispatcher.Invoke(() => Topmost = alwaysOnTop);
		}

		private void InitializeBounds()
		{
			if (isMainWindow && WindowSettings.FullScreenMode)
			{
				Top = 0;
				Left = 0;
				Height = SystemParameters.WorkArea.Height;
				Width = SystemParameters.WorkArea.Width;
				ResizeMode = ResizeMode.NoResize;
				WindowStyle = WindowStyle.None;

				// #2: start off-screen so the load happens invisibly; RevealAfterLoad() slides it to Left = 0 on the
				// first real content load (see UpdateLoadingState + the BrowserWindow_Loaded safety-net timeout).
				pendingReveal = true;
				MainContentPending = true;   // signal the launch splash to hold until this window reveals (no black load-gap)
				Left = OffScreenLeft;
			}
			else if (WindowSettings.RelativeHeight == 100 && WindowSettings.RelativeWidth == 100)
			{
				WindowState = WindowState.Maximized;
			}
			else
			{
				if (WindowSettings.RelativeHeight > 0)
				{
					Height = SystemParameters.WorkArea.Height * WindowSettings.RelativeHeight.Value / 100;
					Top = (SystemParameters.WorkArea.Height / 2) - (Height / 2);
				}
				else if (WindowSettings.AbsoluteHeight > 0)
				{
					Height = this.TransformFromPhysical(0, WindowSettings.AbsoluteHeight.Value).Y;
					Top = (SystemParameters.WorkArea.Height / 2) - (Height / 2);
				}

				if (WindowSettings.RelativeWidth > 0)
				{
					Width = SystemParameters.WorkArea.Width * WindowSettings.RelativeWidth.Value / 100;
				}
				else if (WindowSettings.AbsoluteWidth > 0)
				{
					Width = this.TransformFromPhysical(WindowSettings.AbsoluteWidth.Value, 0).X;
				}

				if (Height > SystemParameters.WorkArea.Height)
				{
					Top = 0;
					Height = SystemParameters.WorkArea.Height;
				}

				if (Width > SystemParameters.WorkArea.Width)
				{
					Left = 0;
					Width = SystemParameters.WorkArea.Width;
				}

				switch (WindowSettings.Position)
				{
					case WindowPosition.Left:
						Left = 0;
						break;
					case WindowPosition.Center:
						Left = (SystemParameters.WorkArea.Width / 2) - (Width / 2);
						break;
					case WindowPosition.Right:
						Left = SystemParameters.WorkArea.Width - Width;
						break;
				}
			}
		}

		private void LoadIcons()
		{
			var backward = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/NavigateBack.xaml") };
			var forward = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/NavigateForward.xaml") };
			var home = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/Home.xaml") };
			var menu = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/Menu.xaml") };
			var reload = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/Reload.xaml") };

			BackwardButton.Content = IconResourceLoader.Load(backward);
			ForwardButton.Content = IconResourceLoader.Load(forward);
			HomeButton.Content = IconResourceLoader.Load(home);
			MenuButton.Content = IconResourceLoader.Load(menu);
			ReloadButton.Content = IconResourceLoader.Load(reload);
		}

		private void LoadText()
		{
			DeveloperConsoleText.Text = text.Get(TextKey.BrowserWindow_DeveloperConsoleMenuItem);
			DeveloperConsoleButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_DeveloperConsoleMenuItem));
			FindCaseSensitiveCheckBox.Content = text.Get(TextKey.BrowserWindow_FindCaseSensitive);
			FindMenuText.Text = text.Get(TextKey.BrowserWindow_FindMenuItem);
			FindMenuButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_FindMenuItem));
			ZoomText.Text = text.Get(TextKey.BrowserWindow_ZoomMenuItem);
			ZoomInButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_ZoomMenuPlus));
			ZoomOutButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_ZoomMenuMinus));
			ReloadButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_ReloadButton));
			BackwardButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_BackwardButton));
			ForwardButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_ForwardButton));
			DownloadsButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_DownloadsButton));
			HomeButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_HomeButton));
			MenuButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_MenuButton));
			UrlTextBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_UrlTextBox));
			FindTextBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_SearchTextBox));
			FindPreviousButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_SearchPrevious));
			FindNextButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_SearchNext));
			FindbarCloseButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_CloseButton));
		}
	}
}
