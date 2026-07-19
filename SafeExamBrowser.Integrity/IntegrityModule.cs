/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.Integrity.Contracts;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.Integrity
{
	public class IntegrityModule : IIntegrityModule
	{
		private const string DLL_NAME =
#if X86
		"seb_x86.dll";
#else
		"seb_x64.dll";
#endif

		private static readonly byte[] SESSION_DATA_IV =
		{
			0x12, 0x07, 0x14, 0x02, 0x03, 0x10, 0x14, 0x18,
			0x11, 0x01, 0x04, 0x15, 0x06, 0x16, 0x05, 0x12
		};
		private static readonly byte[] SESSION_DATA_KEY =
		{
			0x01, 0x04, 0x07, 0x08, 0x09, 0x10, 0x13, 0x06,
			0x11, 0x14, 0x15, 0x16, 0x05, 0x03, 0x13, 0x06,
			0x01, 0x04, 0x02, 0x03, 0x14, 0x15, 0x07, 0x08,
			0x11, 0x12, 0x16, 0x05, 0x09, 0x10, 0x12, 0x02
		};

		// Optional hardcoded production pin for the own-binary integrity check (defense-in-depth against a
		// wholesale re-sign with an attacker's certificate). Empty = pin to the running build's OWN signing cert
		// (AppConfig.CodeSignatureHash), which is what self-signed dev/test verification needs.
		// TODO(5c-final): set to Blinkered's OV code-signing cert thumbprint (uppercase hex, no spaces) once it
		// lands, so all binaries (incl. this executable) must match that specific cert, not merely "the same cert
		// that signed me".
		private const string ProductionCodeSignatureHash = "";

		private readonly AppConfig appConfig;
		private readonly ILogger logger;

		public IntegrityModule(AppConfig appConfig, ILogger logger)
		{
			this.appConfig = appConfig;
			this.logger = logger;
		}

		public void CacheSession(string configurationKey)
		{
			if (TryReadSessionCache(out var sessions) && TryWriteSessionCache(sessions.Append(configurationKey)))
			{
				logger.Debug("Successfully cached session.");
			}
			else
			{
				logger.Error("Failed to cache session!");
			}
		}

		public void ClearSession(string configurationKey)
		{
			if (TryReadSessionCache(out var sessions) && TryWriteSessionCache(sessions.Where(s => s != configurationKey)))
			{
				logger.Debug("Successfully cleared session.");
			}
			else
			{
				logger.Error("Failed to clear session!");
			}
		}

		public bool IsRemoteSession()
		{
			var isRemoteSession = false;

			try
			{
				isRemoteSession = Native.IsRemoteSession();
			}
			catch (DllNotFoundException)
			{
				logger.Warn("Integrity module is not available!");
			}
			catch (Exception e)
			{
				logger.Error("Unexpected error while attempting to query remote session status!", e);
			}

			return isRemoteSession;
		}

		public bool IsVirtualMachine(out string manufacturer, out int probability)
		{
			var isVirtualMachine = false;

			manufacturer = default;
			probability = default;

			try
			{
				isVirtualMachine = Native.IsVirtualMachine(out var bstr, out probability);

				if (bstr != IntPtr.Zero)
				{
					manufacturer = Marshal.PtrToStringBSTR(bstr);
					Marshal.FreeBSTR(bstr);
				}
			}
			catch (DllNotFoundException)
			{
				logger.Warn("Integrity module is not available!");
			}
			catch (Exception e)
			{
				logger.Error("Unexpected error while attempting to query virtual machine information!", e);
			}

			return isVirtualMachine;
		}

		public bool TryCalculateAppSignatureKey(string connectionToken, string salt, out string appSignatureKey)
		{
			appSignatureKey = default;

			try
			{
				appSignatureKey = Native.CalculateAppSignatureKey(connectionToken, salt);
			}
			catch (DllNotFoundException)
			{
				logger.Warn("Integrity module is not available!");
			}
			catch (Exception e)
			{
				logger.Error("Unexpected error while attempting to calculate app signature key!", e);
			}

			return appSignatureKey != default;
		}

		public bool TryCalculateBrowserExamKey(string configurationKey, string salt, out string browserExamKey)
		{
			browserExamKey = default;

			try
			{
				browserExamKey = Native.CalculateBrowserExamKey(configurationKey, salt);
			}
			catch (DllNotFoundException)
			{
				logger.Warn("Integrity module is not available!");
			}
			catch (Exception e)
			{
				logger.Error("Unexpected error while attempting to calculate browser exam key!", e);
			}

			return browserExamKey != default;
		}

		public bool TryGenerateVerificatorCode(string payload, out string code)
		{
			var success = false;

			code = default;

			try
			{
				success = Native.TryGenerateVerificatorCode(payload, out var bstr);

				if (bstr != IntPtr.Zero)
				{
					code = Marshal.PtrToStringBSTR(bstr);
					Marshal.FreeBSTR(bstr);
				}
			}
			catch (DllNotFoundException)
			{
				logger.Warn("Integrity module is not available!");
			}
			catch (Exception e)
			{
				logger.Error("Unexpected error while attempting to generate verificator code!", e);
			}

			return success;
		}

		public bool TryVerifyApplicationIntegrity(out bool isValid)
		{
			isValid = false;

#if DEBUG
			// Dev affordance: DEBUG builds are UNSIGNED (no Authenticode), so they cannot self-verify. Skip the
			// check in DEBUG so the app runs locally / on the kiosk test box; RELEASE compiles this out and
			// enforces. (This replaces the old ETH-native runtime-integrity bypass.)
			logger.Warn("Application integrity check skipped — DEBUG dev build (unsigned, cannot self-verify).");
			isValid = true;
			return true;
#else
			try
			{
				// Pin: prefer the hardcoded production thumbprint (defense-in-depth vs a wholesale re-sign);
				// fall back to this build's own signing cert so a self-signed dev/test build verifies unchanged.
				var expected = string.IsNullOrEmpty(ProductionCodeSignatureHash) ? appConfig.CodeSignatureHash : ProductionCodeSignatureHash;

				if (string.IsNullOrEmpty(expected))
				{
					logger.Error("Application integrity: no signing-certificate thumbprint to pin to (unsigned Release?) — failing closed.");
					isValid = false;
					return true;   // a determinate verdict: compromised
				}

				var directory = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? Assembly.GetExecutingAssembly().Location);
				var binaries = Directory.GetFiles(directory, "SafeExamBrowser*.exe").Concat(Directory.GetFiles(directory, "SafeExamBrowser*.dll")).ToArray();

				if (binaries.Length == 0)
				{
					logger.Error($"Application integrity: no Blinkered binaries found in '{directory}' — failing closed.");
					isValid = false;
					return true;
				}

				var anyInvalid = false;
				var anyIndeterminate = false;
				var verified = 0;

				foreach (var binary in binaries)
				{
					switch (AuthenticodeVerifier.Verify(binary, expected, out var reason))
					{
						case FileIntegrity.Valid:
							verified++;
							break;
						case FileIntegrity.Invalid:
							anyInvalid = true;
							logger.Error($"Application integrity VIOLATION: '{Path.GetFileName(binary)}' — {reason}.");
							break;
						case FileIntegrity.Indeterminate:
							anyIndeterminate = true;
							logger.Warn($"Application integrity indeterminate: '{Path.GetFileName(binary)}' — {reason}.");
							break;
					}
				}

				if (anyInvalid)
				{
					isValid = false;   // a definitive violation -> fail-closed (takes precedence over indeterminate)
					return true;
				}

				if (anyIndeterminate)
				{
					return false;   // could not determine (locked/AV) -> caller retries (bootstrap) / skips tick (monitor)
				}

				logger.Info($"Application integrity verified: {verified} Blinkered binaries signed by {expected}.");
				isValid = true;
				return true;
			}
			catch (Exception e)
			{
				logger.Error("Unexpected error while verifying application integrity.", e);
				return false;   // indeterminate
			}
#endif
		}

		public bool TryVerifyCodeSignature(out bool isValid)
		{
			var success = false;

			isValid = default;

			try
			{
				isValid = Native.VerifyCodeSignature();
				success = true;
			}
			catch (DllNotFoundException)
			{
				logger.Warn("Integrity module is not available!");
			}
			catch (Exception e)
			{
				logger.Error("Unexpected error while attempting to verify code signature!", e);
			}

			return success;
		}

		public bool TryVerifyRuntimeIntegrity(out bool isValid)
		{
			var success = false;

			isValid = default;

			try
			{
				isValid = Native.VerifyRuntimeIntegrity(out var data, out var count);

				for (var index = 0; index < count; index++)
				{
					var pointer = Marshal.ReadIntPtr(data, index * IntPtr.Size);
					var raw = Marshal.PtrToStringBSTR(pointer);
					var item = string.Join(" ", raw.ToCharArray().Select(c => Convert.ToInt32(c)));

					logger.Warn($"Runtime Integrity Violation #{index}: {item}");

					Marshal.FreeBSTR(pointer);
				}

				Marshal.FreeCoTaskMem(data);
				success = true;
			}
			catch (DllNotFoundException)
			{
				logger.Warn("Integrity module is not available!");
			}
			catch (Exception e)
			{
				logger.Error("Unexpected error while attempting to verify runtime integrity!", e);
			}

			// NOTE: Blinkered no longer relies on this ETH native runtime-integrity check (it verifies against
			// ETH's signed build, so it rejects/flaps on our fork regardless of our own code signing — proven in
			// the Phase 5 self-signed rehearsal). The gates use TryVerifyApplicationIntegrity (our own Authenticode
			// check) instead; this method is retained for API compatibility but is no longer called by our gating.
			return success;
		}

		public bool TryVerifySessionIntegrity(string configurationKey, out bool isValid)
		{
			var success = false;

			isValid = false;

			if (TryReadSessionCache(out var sessions))
			{
				isValid = sessions.All(s => s != configurationKey);
				success = true;
				logger.Debug($"Successfully verified session integrity, session is {(isValid ? "valid." : "compromised!")}");
			}
			else
			{
				logger.Error("Failed to verify session integrity!");
			}

			return success;
		}

		private bool TryReadSessionCache(out IList<string> sessions)
		{
			var success = false;

			sessions = new List<string>();

			try
			{
				if (File.Exists(appConfig.SessionCacheFilePath))
				{
					using (var file = new FileStream(appConfig.SessionCacheFilePath, FileMode.Open))
					using (var aes = Aes.Create())
					using (var stream = new CryptoStream(file, aes.CreateDecryptor(SESSION_DATA_KEY, SESSION_DATA_IV), CryptoStreamMode.Read))
					using (var reader = new StreamReader(stream))
					{
						for (var session = reader.ReadLine(); session != default; session = reader.ReadLine())
						{
							sessions.Add(session);
						}
					}
				}

				success = true;
			}
			catch (Exception e)
			{
				logger.Error("Failed to read session cache!", e);
			}

			return success;
		}

		private bool TryWriteSessionCache(IEnumerable<string> sessions)
		{
			var success = false;

			try
			{
				if (sessions.Any())
				{
					using (var file = new FileStream(appConfig.SessionCacheFilePath, FileMode.Create))
					using (var aes = Aes.Create())
					{
						aes.Key = SESSION_DATA_KEY;
						aes.IV = SESSION_DATA_IV;

						using (var stream = new CryptoStream(file, aes.CreateEncryptor(), CryptoStreamMode.Write))
						using (var writer = new StreamWriter(stream))
						{
							foreach (var session in sessions)
							{
								writer.WriteLine(session);
							}
						}
					}
				}
				else
				{
					File.Delete(appConfig.SessionCacheFilePath);
				}

				success = true;
			}
			catch (Exception e)
			{
				logger.Error("Failed to write session cache!", e);
			}

			return success;
		}

		private static class Native
		{
			[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.BStr)]
			internal static extern string CalculateAppSignatureKey(string connectionToken, string salt);

			[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
			[return: MarshalAs(UnmanagedType.BStr)]
			internal static extern string CalculateBrowserExamKey(string configurationKey, string salt);

			[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
			internal static extern bool IsRemoteSession();

			[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
			internal static extern bool IsVirtualMachine(out IntPtr manufacturer, out int probability);

			[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
			internal static extern bool TryGenerateVerificatorCode(string payload, out IntPtr code);

			[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
			internal static extern bool VerifyCodeSignature();

			[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
			internal static extern bool VerifyRuntimeIntegrity(out IntPtr data, out int count);
		}
	}
}
