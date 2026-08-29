using SysException = System.Exception;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;

namespace Tool_Struct.Security
{
    internal static class LicenseConfig
    {
        // Cloudflare Pages URL (edge node in Vietnam — ~20-80ms)
        // Format license.txt:
        //   PC01;001122AABBCC
        //   PC02;00-11-22-AA-BB-CC, 112233445566
        //
        // To update: change the URL below to your Cloudflare Pages project URL
        // Example: https://tool-struct-lic.pages.dev/license.txt
        public const string LicenseUrl =
            "https://tool-struct-lic.pages.dev/license.txt";

        // Cache OK for 30 minutes — reduces server calls from ~288/day to ~48/day
        public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

        // Cache fail for 60 seconds — retry after 1 min, avoids spamming server
        public static readonly TimeSpan FailedCacheTtl = TimeSpan.FromSeconds(60);

        // When server is down but last check was OK, keep license valid for 2 hours
        // Prevents locking users due to temporary network issues
        public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(2);

        // Timeout — Cloudflare responds in ~50ms from Vietnam,
        // so 4s is generous enough for slow networks
        public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(4);
    }

    internal sealed class LicenseCheckResult
    {
        public bool Ok { get; }
        public string Reason { get; }
        public LicenseRecord MatchedRecord { get; }

        public LicenseCheckResult(bool ok, string reason, LicenseRecord matched = null)
        {
            Ok = ok;
            Reason = reason ?? "";
            MatchedRecord = matched;
        }
    }

    internal sealed class LicenseRecord
    {
        public string Pc;
        public string[] Macs;   // normalized
        public string RawLine;
    }

    internal static class LicenseService
    {
        private sealed class MachineIdentity
        {
            public string PcName;
            public HashSet<string> Macs;
        }

        private static readonly HttpClient _http = CreateHttpClient();

        // Cache machine identity for the entire session to avoid scanning NICs repeatedly
        private static readonly Lazy<MachineIdentity> _identity =
            new Lazy<MachineIdentity>(LoadIdentity, LazyThreadSafetyMode.ExecutionAndPublication);

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var http = new HttpClient(handler);
            http.Timeout = LicenseConfig.HttpTimeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Tool_Struct_Core/1.0");
            http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true
            };
            return http;
        }

        public static LicenseCheckResult CheckOnline()
        {
            try
            {
                if (IsPlaceholderUrl(LicenseConfig.LicenseUrl))
                    return new LicenseCheckResult(false, "LicenseUrl not configured.");

                var identity = _identity.Value;

                string licenseText = DownloadText(LicenseConfig.LicenseUrl);

                if (string.IsNullOrWhiteSpace(licenseText))
                    return new LicenseCheckResult(false, "License file is empty or could not be downloaded.");

                if (LooksLikeHtml(licenseText))
                    return new LicenseCheckResult(false, "License returned HTML (invalid URL or server error).");

                var records = ParseLicenseRecords(licenseText, out string parseWarning);

                if (records.Length == 0)
                    return new LicenseCheckResult(false, "No valid license entries found." + parseWarning);

                for (int i = 0; i < records.Length; i++)
                {
                    var r = records[i];
                    if (r == null) continue;

                    if (!string.Equals(identity.PcName, r.Pc, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var recordMacs = r.Macs;
                    for (int j = 0; j < recordMacs.Length; j++)
                    {
                        if (identity.Macs.Contains(recordMacs[j]))
                            return new LicenseCheckResult(true, "OK", r);
                    }
                }

                return new LicenseCheckResult(
                    false,
                    "This PC is not licensed.\nPlease send your PC name and MAC address to the administrator.");
            }
            catch (TaskCanceledException)
            {
                return new LicenseCheckResult(false, "License check timed out.");
            }
            catch (HttpRequestException ex)
            {
                return new LicenseCheckResult(false, "Failed to download license: " + ex.Message);
            }
            catch (SysException ex)
            {
                return new LicenseCheckResult(false, ex.Message);
            }
        }

        public static (string PcName, string[] Macs) GetCurrentIdentity()
        {
            var id = _identity.Value;
            return (id.PcName, id.Macs.ToArray());
        }

        public static void WarmUpIdentity()
        {
            var _ = _identity.Value;
        }

        public static string DownloadText(string url)
        {
            using (var response = _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                                       .GetAwaiter()
                                       .GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                return response.Content.ReadAsStringAsync()
                    .GetAwaiter()
                    .GetResult() ?? "";
            }
        }

        private static LicenseRecord[] ParseLicenseRecords(string text, out string warning)
        {
            warning = "";
            var results = new List<LicenseRecord>();
            int bad = 0;

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
            {
                if (TryParseLine(lines[i], out var record))
                {
                    results.Add(record);
                    continue;
                }

                var t = (lines[i] ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(t) && !t.StartsWith("#"))
                    bad++;
            }

            if (bad > 0)
                warning = $" ({bad} invalid lines skipped)";

            return results.ToArray();
        }

        // Format:
        // PC;MACS
        // MACS can be: MAC1,MAC2 | MAC3
        private static bool TryParseLine(string raw, out LicenseRecord record)
        {
            record = null;

            if (raw == null) return false;

            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (line.StartsWith("#")) return false;

            int sep = line.IndexOf(';');
            if (sep <= 0 || sep >= line.Length - 1) return false;

            string pc = line.Substring(0, sep).Trim();
            string macField = line.Substring(sep + 1).Trim();

            if (string.IsNullOrWhiteSpace(pc)) return false;
            if (string.IsNullOrWhiteSpace(macField)) return false;

            var macs = macField
                .Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeMac)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (macs.Length == 0) return false;

            record = new LicenseRecord
            {
                Pc = pc,
                Macs = macs,
                RawLine = raw
            };

            return true;
        }

        private static MachineIdentity LoadIdentity()
        {
            string pc = Environment.MachineName ?? "UNKNOWN-PC";
            var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var nics = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < nics.Length; i++)
            {
                var nic = nics[i];
                if (nic == null) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var pa = nic.GetPhysicalAddress();
                if (pa == null) continue;

                var bytes = pa.GetAddressBytes();
                if (bytes == null || bytes.Length != 6) continue;

                var mac = NormalizeMac(pa.ToString());
                if (!string.IsNullOrWhiteSpace(mac))
                    macs.Add(mac);
            }

            if (macs.Count == 0)
                macs.Add("NO_MAC");

            return new MachineIdentity
            {
                PcName = pc,
                Macs = macs
            };
        }

        private static bool LooksLikeHtml(string s)
        {
            var t = (s ?? "").TrimStart();
            return t.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
                   || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                   || t.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeMac(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";

            var hex = new string(s.Where(Uri.IsHexDigit).ToArray());
            return hex.ToUpperInvariant();
        }

        private static bool IsPlaceholderUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;

            return url.IndexOf("OWNER", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("REPO", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("BRANCH", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("path/to", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class LicenseGuard
    {
        private static readonly object _stateLock = new object();
        private static readonly object _refreshLock = new object();

        private static DateTime _nextCheckUtc = DateTime.MinValue;
        private static DateTime _graceDeadlineUtc = DateTime.MinValue;
        private static LicenseCheckResult _cached = new LicenseCheckResult(false, "Not checked yet");
        private static bool _hasCache = false;
        private static bool _lastOkWasValid = false; // tracks last successful validation

        // Prevent spawning multiple background refreshes simultaneously
        private static int _backgroundRefreshing = 0;

        /// <summary>
        /// Standard guard for use in commands/ribbon handlers.
        /// </summary>
        public static bool EnsureLicensed(Editor ed, bool forceRefresh = false)
        {
            var res = CheckCached(forceRefresh);

            if (res.Ok) return true;

            ed?.WriteMessage("\n[TOOL_STRUCT] LICENSE FAIL: {0}\n", res.Reason);
            try { Application.ShowAlertDialog(res.Reason); } catch { }
            return false;
        }

        /// <summary>
        /// Fast check. If cached OK has expired, returns immediately to avoid lag
        /// and triggers background refresh.
        /// </summary>
        public static bool IsLicensedFast()
        {
            return CheckCached(false).Ok;
        }

        /// <summary>
        /// Call on plugin startup to preload identity + license in background.
        /// </summary>
        public static void WarmUp()
        {
            LicenseService.WarmUpIdentity();
            StartBackgroundRefresh();
        }

        public static LicenseCheckResult CheckCached(bool forceRefresh)
        {
            LicenseCheckResult snapshot;
            DateTime nextCheckUtc;
            bool hasCache;

            lock (_stateLock)
            {
                snapshot = _cached;
                nextCheckUtc = _nextCheckUtc;
                hasCache = _hasCache;
            }

            var now = DateTime.UtcNow;

            // Cache still valid -> return immediately
            if (!forceRefresh && hasCache && now < nextCheckUtc)
                return snapshot;

            // Cache expired but last result was OK -> return immediately, refresh in background
            if (!forceRefresh && hasCache && snapshot.Ok)
            {
                StartBackgroundRefresh();
                return snapshot;
            }

            // First check / cache fail / force refresh -> synchronous refresh
            return RefreshSync(forceRefresh);
        }

        private static void StartBackgroundRefresh()
        {
            if (Interlocked.Exchange(ref _backgroundRefreshing, 1) == 1)
                return;

            Task.Run(() =>
            {
                try
                {
                    RefreshSync(false);
                }
                catch
                {
                    // Swallow background errors, don't block UI
                }
                finally
                {
                    Interlocked.Exchange(ref _backgroundRefreshing, 0);
                }
            });
        }

        private static LicenseCheckResult RefreshSync(bool forceRefresh)
        {
            lock (_refreshLock)
            {
                // Another thread may have just finished refreshing while this one waited
                if (!forceRefresh)
                {
                    lock (_stateLock)
                    {
                        if (_hasCache && DateTime.UtcNow < _nextCheckUtc)
                            return _cached;
                    }
                }

                var result = LicenseService.CheckOnline();
                var now = DateTime.UtcNow;

                lock (_stateLock)
                {
                    if (result.Ok)
                    {
                        // License OK -> cache for 30 min, update grace deadline
                        _cached = result;
                        _hasCache = true;
                        _lastOkWasValid = true;
                        _nextCheckUtc = now.Add(LicenseConfig.CacheTtl);
                        _graceDeadlineUtc = now.Add(LicenseConfig.GracePeriod);
                    }
                    else if (_lastOkWasValid && now < _graceDeadlineUtc)
                    {
                        // Server error but last check was OK and still within grace period
                        // -> keep OK status, retry after 60s
                        // Do NOT lock user just because server is temporarily down
                        _nextCheckUtc = now.Add(LicenseConfig.FailedCacheTtl);
                        // Keep existing _cached (OK) — do not overwrite with failed result
                    }
                    else
                    {
                        // License truly failed (PC not in list)
                        // or grace period has expired
                        _cached = result;
                        _hasCache = true;
                        _lastOkWasValid = false;
                        _nextCheckUtc = now.Add(LicenseConfig.FailedCacheTtl);
                    }

                    return _cached;
                }
            }
        }
    }
}
