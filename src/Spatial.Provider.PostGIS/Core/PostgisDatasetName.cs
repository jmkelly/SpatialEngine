namespace Spatial.Provider.PostGIS.Core;

/// <summary>
/// A validated dataset identifier (ADR-0028, data-provider-contracts.md): a
/// strict <c>schema.table</c> — or bare <c>table</c> (implicit
/// <c>public</c>) — where every part is a lowercase PostgreSQL identifier
/// (<c>[a-z_][a-z0-9_]*</c>). The grammar is the injection barrier for the
/// adapter's generated SQL: an identifier that matches it can be safely
/// double-quoted and the part names are never taken from free text. Anything
/// else is an <c>invalid.arguments</c> value naming the exact problem.
/// </summary>
internal readonly record struct PostgisDatasetName
{
    private PostgisDatasetName(string schema, string table)
    {
        Schema = schema;
        Table = table;
    }

    /// <summary>The schema part (defaults to <c>public</c>).</summary>
    public string Schema { get; }

    /// <summary>The table part.</summary>
    public string Table { get; }

    /// <summary>The full <c>schema.table</c> identifier.</summary>
    public string Qualified => $"{Schema}.{Table}";

    /// <summary>Parses and validates a dataset identifier; false with an actionable reason when invalid.</summary>
    public static bool TryParse(string? text, out PostgisDatasetName name, out string reason)
    {
        name = default;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "a dataset identifier is required (schema.table or table).";
            return false;
        }

        var dot = text.IndexOf('.');
        if (dot < 0)
        {
            return ParsePart(text, isSchema: false, out name, out reason);
        }

        if (dot == 0 || dot == text.Length - 1 || text.IndexOf('.', dot + 1) >= 0)
        {
            reason = $"'{text}' is not a dataset identifier: expected schema.table with no other dots.";
            return false;
        }

        var schema = text[..dot];
        var table = text[(dot + 1)..];
        if (!IsIdentifierPart(schema, out var schemaProblem))
        {
            reason = $"'{text}' is not a valid dataset identifier: {schemaProblem}";
            return false;
        }

        if (!IsIdentifierPart(table, out var tableProblem))
        {
            reason = $"'{text}' is not a valid dataset identifier: {tableProblem}";
            return false;
        }

        name = new PostgisDatasetName(schema, table);
        return true;
    }

    /// <summary>Whether <paramref name="name"/> is a valid unquoted column/table identifier part.</summary>
    public static bool IsValidIdentifier(string? name) => name is not null && IsIdentifierPart(name, out _);

    private static bool ParsePart(string part, bool isSchema, out PostgisDatasetName name, out string reason)
    {
        name = default;
        if (!IsIdentifierPart(part, out var problem))
        {
            reason = isSchema
                ? $"'{part}' is not a valid schema name: {problem}"
                : $"'{part}' is not a valid table name: {problem}";
            return false;
        }

        name = new PostgisDatasetName("public", part);
        reason = string.Empty;
        return true;
    }

    private static bool IsIdentifierPart(string part, out string? problem)
    {
        problem = null;
        if (part.Length == 0)
        {
            problem = "the part is empty";
            return false;
        }

        if (!IsLowerAscii(part[0]) && part[0] != '_')
        {
            problem = $"the part '{part}' must start with a lowercase letter or underscore (unquoted PostgreSQL identifiers fold to lowercase).";
            return false;
        }

        for (var i = 1; i < part.Length; i++)
        {
            var c = part[i];
            if (IsLowerAscii(c) || char.IsAsciiDigit(c) || c == '_')
            {
                continue;
            }

            problem = $"the part '{part}' contains '{c}', which is not allowed in an unquoted identifier; use only [a-z0-9_].";
            return false;
        }

        return true;
    }

    private static bool IsLowerAscii(char c) => c is >= 'a' and <= 'z';

    /// <summary>Quotes the validated identifier for SQL (safe because <see cref="TryParse"/> already vetted it).</summary>
    public string QuoteQualified() => $"\"{Schema}\".\"{Table}\"";

    public override string ToString() => Qualified;
}
