using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher
{
    /// <summary>What a server said to a request for its client with (or without) an access code.</summary>
    internal enum AccessAnswer
    {
        /// <summary>The server gave its client: it asks for no code, or the one sent is right.</summary>
        Allowed,
        /// <summary>401: the server wants a code and got none, or a wrong one.</summary>
        Refused,
        /// <summary>429: too many wrong codes from this address; the server says to wait.</summary>
        TooManyTries,
        /// <summary>No answer, or one that says nothing about the code (the server is down or restarting).</summary>
        Unreachable,
    }

    /// <summary>
    /// The access codes of the servers that ask for one (the development server), and the handler that sends them. A
    /// code goes only to the /client/ routes of the server it was entered for (Arcana repo: ClientReleaseStore.cs,
    /// header X-Arcana-Access-Code): never to the live server, GitHub or anywhere a redirect points. The server checks
    /// it; the launcher remembers it, encrypted for this Windows user (DPAPI), so a copied settings.json opens
    /// nothing on another PC or account.
    /// The launcher also knows the development code itself (the user, 2026-11-15: "hard code the code into the
    /// launcher"), as a SHA-256 so the word is not in this public repo or readable in the exe: a typed code that is
    /// not it is refused here, without asking the server. The server's Secret must hold the same code.
    /// </summary>
    internal sealed class AccessCodes : DelegatingHandler
    {
        public const string Header = "X-Arcana-Access-Code";
        public const int MaxLength = 128;
        /// <summary>SHA-256 (lower hex, UTF-8) of the development access code.</summary>
        public const string DevelopmentCodeHash = "2171952a109fc3a326b4878f23fa16f5b01e3f61699809c4f145fd17e864d8a1";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TidanGames.ArcanaLauncher.AccessCode");

        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public AccessCodes(HttpMessageHandler inner) : base(inner)
        {
        }

        /// <summary>Remembers <paramref name="code"/> for <paramref name="serverUrl"/>; null or blank forgets it.</summary>
        public void Set(string serverUrl, string code)
        {
            var key = KeyOf(new Uri(serverUrl));
            code = Clean(code);
            lock (_gate)
            {
                if (code == null) _codes.Remove(key);
                else _codes[key] = code;
            }
        }

        /// <summary>The code to send with a request for <paramref name="url"/>, or null.</summary>
        public string For(Uri url)
        {
            if (url == null || !url.IsAbsoluteUri || !url.AbsolutePath.StartsWith("/client/", StringComparison.Ordinal))
                return null;
            lock (_gate)
                return _codes.TryGetValue(KeyOf(url), out var code) ? code : null;
        }

        private static string KeyOf(Uri url) => url.Scheme + "://" + url.Host + ":" + url.Port;

        /// <summary>
        /// A typed code as it is sent: trimmed, or null when it is blank, too long or holds anything a header cannot
        /// carry (only printable ASCII goes on the wire).
        /// </summary>
        public static string Clean(string code)
        {
            code = code?.Trim();
            if (string.IsNullOrEmpty(code) || code.Length > MaxLength) return null;
            foreach (var c in code)
                if (c < 0x20 || c > 0x7E) return null;
            return code;
        }

        /// <summary>Whether <paramref name="code"/> is the development access code (exact, case and all).</summary>
        public static bool IsDevelopmentCode(string code) => Matches(code, DevelopmentCodeHash);

        /// <summary>Whether <paramref name="code"/>, cleaned, hashes to <paramref name="sha256"/>. Constant time.</summary>
        public static bool Matches(string code, string sha256)
        {
            code = Clean(code);
            if (code == null || string.IsNullOrEmpty(sha256)) return false;
            var actual = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant());
            return CryptographicOperations.FixedTimeEquals(actual, Encoding.ASCII.GetBytes(sha256.ToLowerInvariant()));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            // A request that names its own code (a code being tried) keeps it.
            if (!request.Headers.Contains(Header))
            {
                var code = For(request.RequestUri);
                if (code != null) request.Headers.TryAddWithoutValidation(Header, code);
            }
            return base.SendAsync(request, cancel);
        }

        /// <summary>The code as settings.json keeps it: encrypted for this Windows user. Null for no code.</summary>
        public static string Protect(string code)
        {
            code = Clean(code);
            if (code == null) return null;
            try
            {
                return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(code), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException ex)
            {
                // Not fatal: the code still works for this run and is asked for again next time.
                Log.Warn("Could not encrypt the access code; it will not be remembered.", ex);
                return null;
            }
        }

        /// <summary>The code behind a settings.json value, or null when it is missing, damaged or another user's.</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            try
            {
                return Clean(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser)));
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            {
                return null;
            }
        }
    }
}
