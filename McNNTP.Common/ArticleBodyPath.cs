using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace McNNTP.Common;

#nullable enable

public static class ArticleBodyPath {
    /// <summary>
    /// Builds a 2-level folder layout from the message-id key:
    ///   {baseFolder}\{enc(firstChar)}\{enc(secondChar)}\{key}
    ///
    /// Where:
    /// - key = part before '@' with surrounding angle brackets removed
    /// - enc(lowercase/digit) = itself
    /// - enc(uppercase) = lowercase letter repeated twice (e.g. 'D' -> "dd")
    ///
    /// Note: folder names are derived from single characters, so variable-length
    /// encoding (1 or 2 chars) is unambiguous.
    /// </summary>
    public static string GetBodyFilePath(string baseFolder, string fullMessageId) {
        if (string.IsNullOrWhiteSpace(baseFolder))
            throw new ArgumentException("Base folder is required.", nameof(baseFolder));
        if (string.IsNullOrWhiteSpace(fullMessageId))
            throw new ArgumentException("Message-Id is required.", nameof(fullMessageId));

        var key = ExtractKey(fullMessageId);

        // If message-id key is too short, still keep deterministic structure.
        var c1 = key.Length >= 1 ? key[0] : '_';
        var c2 = key.Length >= 2 ? key[1] : '_';

        var p1 = EncodeCharForFolder(c1);
        var p2 = EncodeCharForFolder(c2);

        return Path.Combine(baseFolder, p1, p2, key);
    }

    private static string ExtractKey(string fullMessageId) {
        // Typical: <key@domain>
        var s = fullMessageId.Trim();

        if (s.Length >= 2 && s[0] == '<' && s[^1] == '>')
            s = s.Substring(1, s.Length - 2);

        var at = s.IndexOf('@');
        var key = at >= 0 ? s.Substring(0, at) : s;

        if (string.IsNullOrWhiteSpace(key))
            throw new FormatException($"Invalid Message-Id: '{fullMessageId}'");

        // The user said it's [0-9a-zA-Z] random strings.
        // Still guard against path separators just in case.
        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new FormatException($"Message-Id key contains invalid filename characters: '{key}'");

        return key;
    }

    private static string EncodeCharForFolder(char c) {
        // digits stay digits
        if (c is >= '0' and <= '9')
            return c.ToString();

        // lowercase a-z stays lowercase
        if (c is >= 'a' and <= 'z')
            return c.ToString();

        // uppercase A-Z becomes doubled lowercase, e.g. 'D' => "dd"
        if (c is >= 'A' and <= 'Z') {
            var lower = (char)(c - 'A' + 'a');
            return string.Create(2, lower, static (span, ch) => { span[0] = ch; span[1] = ch; });
        }

        // Fallback (shouldn't happen with your stated charset): make it stable + readable.
        // Example: '_' => "_", '-' => "-", etc.
        if (c is '_' or '-' or '.')
            return c.ToString();

        // Last-resort: hex
        return $"x{(int)c:X4}";
    }
}
