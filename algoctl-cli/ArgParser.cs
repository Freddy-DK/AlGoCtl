namespace AlGoCtl;

/// <summary>
/// Parses <c>--key value</c> / <c>--key=value</c> style arguments into a
/// case-insensitive dictionary.
/// </summary>
internal static class ArgParser
{
    public static Dictionary<string, string> Parse(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tokens = args.ToList();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = token[2..];
            var eq = key.IndexOf('=');
            if (eq >= 0)
            {
                result[key[..eq]] = key[(eq + 1)..];
                continue;
            }

            // Next token is the value unless it is another flag.
            if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = tokens[++i];
            }
            else
            {
                result[key] = string.Empty; // treat as a bare flag
            }
        }

        return result;
    }
}
