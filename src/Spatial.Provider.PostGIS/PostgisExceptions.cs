namespace Spatial.Provider.PostGIS;

/// <summary>The dataset does not exist or is not a readable spatial table (mapped to invalid.arguments).</summary>
internal sealed class PostgisUnknownDatasetException(string message) : Exception(message)
{
}

/// <summary>The referenced transaction is no longer active (mapped to invalid.arguments).</summary>
internal sealed class PostgisInactiveTransactionException(string message) : Exception(message)
{
}

/// <summary>A create target already exists (mapped to invalid.arguments).</summary>
internal sealed class PostgisDatasetExistsException(string message) : Exception(message)
{
}
