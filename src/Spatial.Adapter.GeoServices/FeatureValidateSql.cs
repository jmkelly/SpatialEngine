using Spatial.Interop.Esri;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The layer-level <c>validateSQL</c> operation (S4
/// validate-sql-feature-service-layer/): server-side validation of the
/// <c>sql</c> WHERE clause against the closed <see cref="EsriFilterClause"/>
/// grammar and the layer's schema. The response follows the S4 shape:
/// <c>{"isValidSQL": true}</c>, or <c>{"isValidSQL": false,
/// "validationErrors": [{"errorCode", "description"}]}</c> with the spec's
/// 3001 (not supported) / 3002 (syntax) / 3008 (unknown field) codes. The
/// <c>expression</c> and <c>statement</c> sql types have no engine
/// evaluation model, so they validate as 3001 rather than as SQL the facade
/// would never run.
/// </summary>
internal static class FeatureValidateSql
{
    /// <summary>The S4 validation error codes surfaced in <c>validationErrors</c>.</summary>
    internal const int CodeNotSupported = 3001;

    /// <summary>The S4 syntax-error code: the clause is outside the closed grammar.</summary>
    internal const int CodeSyntaxError = 3002;

    /// <summary>The S4 invalid-field code: the clause names a field the layer does not carry.</summary>
    internal const int CodeInvalidFieldName = 3008;

    /// <summary>
    /// Validates <paramref name="sql"/> of type <paramref name="sqlType"/>
    /// (<c>where</c> by default) against the layer's schema. A missing
    /// <c>sql</c> is a 400 invalid-argument failure, like every other
    /// required parameter; an unknown <c>sqlType</c> names the three S4
    /// values.
    /// </summary>
    internal static EsriValidateSqlResponse Validate(DatasetDescription dataset, string? sql, string? sqlType)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw EsriInteropException.Invalid("The 'sql' parameter is required.");
        }

        var type = string.IsNullOrWhiteSpace(sqlType) ? "where" : sqlType.Trim().ToLowerInvariant();
        if (type is "expression" or "statement")
        {
            return Invalid(CodeNotSupported,
                $"The sqlType '{type}' is not supported: only 'where' clauses are evaluated; 'expression' and 'statement' have no engine model.");
        }

        if (type != "where")
        {
            throw EsriInteropException.Invalid(
                $"The 'sqlType' value '{sqlType}' is not supported; use 'where' (the default), 'expression' or 'statement'.");
        }

        if (!EsriFilterClause.TryParse(sql, out var clause, out var error))
        {
            return Invalid(CodeSyntaxError, $"Sql expression syntax error: {error}.");
        }

        foreach (var field in clause!.ReferencedFields)
        {
            if (!IsKnownField(dataset, field))
            {
                return Invalid(CodeInvalidFieldName, $"Invalid field name [{field}].");
            }
        }

        return new EsriValidateSqlResponse(true);
    }

    /// <summary>
    /// Whether the layer carries the field: the schema (exactly the
    /// case-sensitive match the query path applies) or the synthetic
    /// <c>OBJECTID</c> the facade resolves for every layer (ADR-0037).
    /// </summary>
    private static bool IsKnownField(DatasetDescription dataset, string field) =>
        string.Equals(field, EsriLayerModel.ObjectIdField, StringComparison.OrdinalIgnoreCase)
        || dataset.Schema.IndexOf(field) >= 0;

    private static EsriValidateSqlResponse Invalid(int code, string description) =>
        new(false, [new EsriSqlValidationError(code, description)]);
}

/// <summary>The S4 <c>validateSQL</c> response.</summary>
internal sealed record EsriValidateSqlResponse(bool IsValidSQL, IReadOnlyList<EsriSqlValidationError>? ValidationErrors = null);

/// <summary>One S4 validation error: the spec code and its description.</summary>
internal sealed record EsriSqlValidationError(int ErrorCode, string Description);
