// Copyright (c) 2025-2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace Sibber.AppInstanceLock;

internal static class StringExtensions
{
    /// <exception cref="ArgumentNullException"></exception>
    // ExceptionAdjustment: M:System.Guid.ToString(System.String) -T:System.FormatException
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
