namespace Echelon.Application.ReleasePlanning;

/// <summary>
/// The pattern language selectors use: literal text with <c>*</c> for "any run of characters".
/// </summary>
/// <remarks>
/// <para>
/// A glob, not a regex, and not by accident. <c>group/svc-*</c> reads correctly to everyone who will
/// ever open the ordering rules, while a regex in a file that decides deploy order is something
/// nobody can debug at the moment they need to. The task-key rule does use a regex - there it earns
/// it, because issue keys genuinely vary - and this deliberately does not follow that precedent.
/// </para>
/// <para>
/// Matched by hand rather than by translating to <see cref="System.Text.RegularExpressions.Regex"/>:
/// translating means escaping the pattern correctly, and a missed escape turns a literal <c>.</c> in
/// <c>group/svc.api</c> into "any character" - a selector that silently matches more than it says. A
/// dozen lines of two-pointer matching has no such failure.
/// </para>
/// </remarks>
public static class Glob
{
    /// <summary>Whether <paramref name="value"/> matches <paramref name="pattern"/>, case-sensitively.</summary>
    /// <param name="pattern">Literal text, with <c>*</c> matching any run of characters including none.</param>
    /// <param name="value">The text to test.</param>
    /// <remarks>
    /// Case-sensitive because every axis it is applied to is an identifier owned by a case-sensitive
    /// system: repository paths and branch names come from Git. The same reasoning that put a binary
    /// collation on those columns applies to matching them.
    /// </remarks>
    public static bool IsMatch(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);

        // Two pointers plus a remembered star, which is the standard linear-ish wildcard match: on a
        // mismatch, fall back to the last star and let it swallow one more character. Recursion here
        // is what makes naive implementations blow up on patterns like "a*a*a*a*b".
        int p = 0, v = 0, starAt = -1, matchAt = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                starAt = p++;
                matchAt = v;
            }
            else if (p < pattern.Length && pattern[p] == value[v])
            {
                p++;
                v++;
            }
            else if (starAt >= 0)
            {
                p = starAt + 1;
                v = ++matchAt;
            }
            else
            {
                return false;
            }
        }

        // Trailing stars match the empty remainder; anything else left over does not.
        while (p < pattern.Length && pattern[p] == '*') p++;

        return p == pattern.Length;
    }
}
