namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// Shared validation rule for the dotted, lowercase identifiers used by
/// capability ids (<c>spatial.geometry.buffer</c>), provider ids (<c>nts</c>)
/// and permissions (<c>spatial.feature.read</c>) — plan §9.
/// A name is one or more dot-separated segments; every segment starts with a
/// lowercase letter and continues with lowercase letters or digits.
/// </summary>
internal static class QualifiedNameRules
{
    /// <summary>
    /// A user-facing description of the first problem with <paramref name="name"/>,
    /// or <c>null</c> when the name is valid.
    /// </summary>
    public static string? DescribeProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "the name must not be null or whitespace";
        }

        var expectSegment = true;
        foreach (var c in name.AsSpan())
        {
            if (expectSegment)
            {
                if (c is < 'a' or > 'z')
                {
                    return $"'{name}' is not valid: every dot-separated segment must start with a lowercase letter";
                }

                expectSegment = false;
                continue;
            }

            if (c == '.')
            {
                expectSegment = true;
            }
            else if (!IsLowerLetterOrDigit(c))
            {
                return $"'{name}' is not valid: only lowercase letters, digits and dots are allowed";
            }
        }

        return expectSegment ? $"'{name}' is not valid: a name must not end with a dot" : null;
    }

    private static bool IsLowerLetterOrDigit(char c) =>
        c is >= 'a' and <= 'z' or >= '0' and <= '9';
}