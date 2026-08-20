using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SoundSync.Services
{
    /// <summary>
    /// Access control for the SoundSync Link network stream.
    ///
    /// The stream server listens on every network interface, so without a token anyone who
    /// can reach the machine could open the page and listen to whatever the PC is playing.
    ///
    /// Two ways to get that token:
    ///
    ///  - Default: a random 128-bit token, generated on first run and kept in the app's own
    ///    folder. Nothing to set up, and the phone URL stays the same between sessions.
    ///
    ///  - Optional: point at a key file you already have, such as an SSH private key. The
    ///    token is then HMAC-SHA256 of that file, so every machine holding the same key
    ///    computes the same token and the same URL works everywhere. The file is read, hashed
    ///    and wiped from memory - never sent over the wire, never written anywhere.
    ///
    /// Either way the token is only ever compared, never echoed back, using a constant-time
    /// comparison so a wrong guess reveals nothing about how close it was.
    /// </summary>
    public static class LinkAuth
    {
        private const string DerivationContext = "soundsync-link-v1";

        /// <summary>The path most people will want if they choose the key-file mode.</summary>
        public static string SuggestedKeyFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa");

        /// <summary>
        /// Same path with the account name replaced by a placeholder, for the hint shown in
        /// the UI. The real user name has no business being printed on a screenshot.
        /// </summary>
        public static string SuggestedKeyFileHint
        {
            get
            {
                string user = Environment.UserName;
                string path = SuggestedKeyFile;
                return string.IsNullOrEmpty(user) ? path : path.Replace(user, "<USER>");
            }
        }

        /// <summary>Where the random token lives when no key file is configured.</summary>
        public static string TokenPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundSync",
            "link_token");

        private static string? keyFile;
        private static string? cachedToken;

        /// <summary>Describes where the current token came from, for the UI to show.</summary>
        public static string SourceDescription { get; private set; } = "random token stored in the app folder";

        /// <summary>
        /// Chooses the token source. Pass an empty path for the random stored token, or the
        /// path of a key file to derive from. Safe to call again to switch modes.
        /// </summary>
        public static void Configure(string? keyFilePath)
        {
            keyFile = string.IsNullOrWhiteSpace(keyFilePath) ? null : keyFilePath.Trim();
            cachedToken = null;
            _ = Token; // resolve now so SourceDescription reflects reality straight away
        }

        /// <summary>Token a client must present as the "t" query parameter.</summary>
        public static string Token => cachedToken ??= Resolve();

        private static string Resolve()
        {
            if (keyFile != null)
            {
                string? derived = DeriveFromKeyFile(keyFile);
                if (derived != null)
                {
                    SourceDescription = $"derived from {keyFile}";
                    return derived;
                }

                SourceDescription = $"could not read {keyFile} - using the random stored token instead";
                return LoadOrCreateRandom();
            }

            SourceDescription = "random token stored in the app folder";
            return LoadOrCreateRandom();
        }

        /// <summary>HMAC-SHA256 of the key file. The key bytes are wiped afterwards.</summary>
        private static string? DeriveFromKeyFile(string path)
        {
            byte[]? keyBytes = null;
            try
            {
                if (!File.Exists(path)) return null;
                keyBytes = File.ReadAllBytes(path);
                if (keyBytes.Length == 0) return null;

                byte[] tag = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(DerivationContext));
                return Convert.ToHexString(tag, 0, 16).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (keyBytes != null) CryptographicOperations.ZeroMemory(keyBytes);
            }
        }

        private static string LoadOrCreateRandom()
        {
            try
            {
                if (File.Exists(TokenPath))
                {
                    string existing = File.ReadAllText(TokenPath).Trim();
                    if (IsWellFormed(existing)) return existing;
                }
            }
            catch
            {
                // Unreadable file: fall through and mint a new token for this run.
            }

            string fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

            try
            {
                string? directory = Path.GetDirectoryName(TokenPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(TokenPath, fresh);
            }
            catch
            {
                // Cannot persist it: the stream still works, the URL just changes next launch.
            }

            return fresh;
        }

        private static bool IsWellFormed(string candidate)
        {
            if (candidate.Length != 32) return false;
            foreach (char c in candidate)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>Constant-time comparison of a client supplied token.</summary>
        public static bool Matches(string? candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate),
                Encoding.UTF8.GetBytes(Token));
        }

        /// <summary>Reads the "t" query parameter out of an HTTP request line.</summary>
        public static bool IsRequestAuthorized(string requestLine)
        {
            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2) return false;

            string target = parts[1];
            int queryStart = target.IndexOf('?');
            if (queryStart < 0) return false;

            foreach (string pair in target.Substring(queryStart + 1).Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                if (pair.Substring(0, equals) != "t") continue;

                try
                {
                    return Matches(Uri.UnescapeDataString(pair.Substring(equals + 1)));
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }
}
