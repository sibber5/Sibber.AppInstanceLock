// Copyright (c) 2025-2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace Sibber.AppInstanceLock;

internal static class StringExtensions
{
    /// <summary>
    /// Sanitizes the string by keeping only ASCII alphanumeric characters, '-', and '_'.
    /// Characters that don't match are replaced with '_'. If the input is 254 characters or longer,
    /// non-matching characters are dropped instead of replaced to reduce length.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is <see langword="null"/>.</exception>
    public static string Sanitize(this string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length == 0) return "";

        var sb = new StringBuilder(s.Length);
        var shorten = s.Length >= 254;
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
            else if (!shorten) sb.Append('_');
        }

        return sb.ToString();
    }
}
