using System;
using System.IO;

namespace McNNTP.Common {
#nullable enable
    public static class ArticleBodyPath {
        /// <summary>
        /// Returns the full file path where the article body should be stored.
        /// Layout:
        ///   {baseFolder}\{enc(firstChar)}\{enc(secondChar)}\{enc(fullKey)}.body
        ///
        /// "fullKey" is the part before '@' (and without angle brackets).
        /// Encoding is lowercase-only but preserves original case.
        /// </summary>
        public static string GetBodyFilePath(string baseFolder, string fullMessageId) {
            if (string.IsNullOrWhiteSpace(baseFolder))
                throw new ArgumentException("Base folder is required.", nameof(baseFolder));

            if (string.IsNullOrWhiteSpace(fullMessageId))
                throw new ArgumentException("MessageId is required.", nameof(fullMessageId));

            var key = ExtractKeyBeforeAt(fullMessageId);

            if (key.Length < 2)
                throw new ArgumentException(
                    $"MessageId key part is too short: '{key}'",
                    nameof(fullMessageId));

            var l1 = EncodeChar(key[0]);
            var l2 = EncodeChar(key[1]);

            var fileName = EncodeKey(key) + ".body";

            return Path.Combine(baseFolder, l1, l2, fileName);
        }

        /// <summary>
        /// Ensures the directory exists for the file path returned by GetBodyFilePath.
        /// </summary>
        public static void EnsureDirectoryForFile(string filePath) {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir))
                throw new InvalidOperationException("Could not determine directory name from path.");

            Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// Extracts the part before '@' from typical NNTP message-id formats:
        ///   &lt;key@domain&gt;
        ///   key@domain
        ///   &lt;key&gt;   (no domain)
        /// </summary>
        private static string ExtractKeyBeforeAt(string fullMessageId) {
            var s = fullMessageId.Trim();

            // strip angle brackets if present
            if (s.Length >= 2 && s[0] == '<' && s[^1] == '>')
                s = s.Substring(1, s.Length - 2);

            var at = s.IndexOf('@');
            return at >= 0 ? s[..at] : s;
        }

        /// <summary>
        /// Encodes one character into a two-character, lowercase-only token.
        /// digit: d0..d9
        /// lower: la..lz
        /// upper: ua..uz  (case preserved by prefix)
        /// </summary>
        private static string EncodeChar(char c) {
            return c switch {
                >= '0' and <= '9' => "d" + c,
                >= 'a' and <= 'z' => "l" + c,
                >= 'A' and <= 'Z' => "u" + char.ToLowerInvariant(c),
                _ => throw new ArgumentException(
                    $"Unsupported character '{c}'. Expected [0-9a-zA-Z].",
                    nameof(c))
            };
        }

        /// <summary>
        /// Encodes the whole key by concatenating EncodeChar for each character.
        /// </summary>
        private static string EncodeKey(string key) {
            return string.Create(key.Length * 2, key, static (span, state) => {
                var j = 0;
                foreach (var c in state) {
                    if (c >= '0' && c <= '9') {
                        span[j++] = 'd';
                        span[j++] = c;
                    } else if (c >= 'a' && c <= 'z') {
                        span[j++] = 'l';
                        span[j++] = c;
                    } else if (c >= 'A' && c <= 'Z') {
                        span[j++] = 'u';
                        span[j++] = char.ToLowerInvariant(c);
                    } else {
                        throw new ArgumentException(
                            $"Unsupported character '{c}'. Expected [0-9a-zA-Z].",
                            nameof(key));
                    }
                }
            });
        }
    }
}
