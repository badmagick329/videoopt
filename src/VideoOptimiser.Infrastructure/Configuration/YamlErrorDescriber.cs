using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace VideoOptimiser.Infrastructure.Configuration;

/// <summary>
/// Turns a deserialisation error into a message that names the setting's full path (e.g. <c>processing.remoteSsh.hetzner.locations</c>).
/// YamlDotNet reports only the offending key or value and its position, so the path is recovered by finding the node at that position.
/// </summary>
internal static class YamlErrorDescriber
{
    // Removed keys whose replacement the generic spelling hint would not reveal.
    private static readonly Dictionary<string, string> Replacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["processing.remoteSsh.hetzner.serverType"] = "It was replaced by serverTypes, a list tried in order, such as serverTypes: [\"cx43\", \"cx53\", \"cpx42\", \"ccx33\"]."
    };

    public static string Describe(string yaml, YamlException exception)
    {
        var location = $"line {exception.Start.Line}, column {exception.Start.Column}";
        var match = FindNode(yaml, exception.Start);
        return match switch
        {
            // A null for a number or switch fails at its key, like an unknown key, but as a NullReferenceException from storing it.
            (var path, true, var value) when IsNull(value) && exception.GetBaseException() is NullReferenceException => NullSettings.Message(path),
            (var path, true, _) when Replacements.TryGetValue(path, out var replacement) => $"Unknown setting '{path}' at {location}. {replacement}",
            (var path, true, _) => $"Unknown setting '{path}' at {location}. Check its spelling, or remove it if it is no longer supported.",
            (var path, false, _) => $"Invalid value for '{path}' at {location}: {Reason(exception)}",
            _ => $"{Reason(exception)} ({location})"
        };
    }

    private static string Reason(YamlException exception) => exception.InnerException?.Message ?? exception.Message;

    // The YAML 1.2 core schema's nulls: nothing after the colon, ~ and null, unquoted and untagged.
    private static bool IsNull(YamlNode? node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any, Value: null or "" or "~" or "null" or "Null" or "NULL" } scalar && scalar.Tag.IsEmpty;

    private static (string Path, bool IsKey, YamlNode? Value)? FindNode(string yaml, Mark position)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException)
        {
            // Malformed YAML: the parser's own message and position are all there is.
            return null;
        }

        return stream.Documents.Count == 0 ? null : Search(stream.Documents[0].RootNode, "", position);
    }

    private static (string Path, bool IsKey, YamlNode? Value)? Search(YamlNode node, string path, Mark position)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    var childPath = path.Length == 0 ? key.ToString() : $"{path}.{key}";
                    if (key.Start.Index == position.Index) return (childPath, true, value);
                    if (Search(value, childPath, position) is { } found) return found;
                }
                break;
            case YamlSequenceNode sequence:
                for (var index = 0; index < sequence.Children.Count; index++)
                {
                    if (Search(sequence.Children[index], $"{path}[{index}]", position) is { } found) return found;
                }
                break;
        }

        return node.Start.Index == position.Index && path.Length > 0 ? (path, false, null) : null;
    }
}
