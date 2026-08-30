namespace Spatial.PluginSdk.Providers;

/// <summary>
/// Builds the argument dictionaries of the provider contracts' embedded
/// conformance examples (shared by every contract file so example shapes stay
/// uniform).
/// </summary>
internal static class ContractArguments
{
    /// <summary>Builds the argument dictionary for one example.</summary>
    public static Dictionary<string, object?> Build(params object?[] pairs)
    {
        var arguments = new Dictionary<string, object?>(pairs.Length / 2);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            arguments[(string)pairs[i]!] = pairs[i + 1];
        }

        return arguments;
    }
}
