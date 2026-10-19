using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace VideoOptimiser.Infrastructure.Configuration;

/// <summary>
/// Turns a deserialisation error into a message that names the setting's full path (e.g. <c>processing.remoteSsh.hetzner.locations</c>).
/// YamlDotNet reports only the offending key or value and its position, so the path is recovered by finding the node at that position.
/// It also names settings written twice, which the deserialiser doesn't treat as an error.
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

    /// <summary>
    /// Names every setting written twice in one section, or returns null. The deserialiser keeps the last value without a
    /// word, so <c>action: keep</c> then <c>action: delete</c> deleted originals (VOPT-165). YamlDotNet's own duplicate check
    /// compares keys case-sensitively, but settings match case-insensitively, so <c>Action:</c> and <c>action:</c> are one.
    /// This reads parser events because <see cref="YamlStream"/> fails on an exact duplicate before its nodes can be walked.
    /// </summary>
    public static string? DescribeDuplicates(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var messages = new List<string>();
        try
        {
            parser.Consume<StreamStart>();
            // An empty file has no document, and the loader has its own message for that.
            if (!parser.TryConsume<DocumentStart>(out _)) return null;
            Walk("");
        }
        catch (YamlException)
        {
            // Malformed YAML: the deserialiser reports the parser's own message and position.
            return null;
        }

        return messages.Count == 0 ? null : string.Join(" ", messages);

        void Walk(string path)
        {
            if (parser.TryConsume<MappingStart>(out _))
            {
                var seen = new Dictionary<string, Mark>(StringComparer.OrdinalIgnoreCase);
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    if (!parser.TryConsume<Scalar>(out var key))
                    {
                        // A key that isn't text, and its value, are left for the deserialiser to reject.
                        parser.SkipThisAndNestedEvents();
                        parser.SkipThisAndNestedEvents();
                        continue;
                    }

                    if (!seen.TryAdd(key.Value, key.Start))
                    {
                        messages.Add($"Duplicate setting '{ChildPath(path, key.Value)}' at line {key.Start.Line}, column {key.Start.Column}. It is already set at line {seen[key.Value].Line}; keep only one.");
                    }

                    Walk(ChildPath(path, key.Value));
                }
            }
            else if (parser.TryConsume<SequenceStart>(out _))
            {
                for (var index = 0; !parser.TryConsume<SequenceEnd>(out _); index++) Walk($"{path}[{index}]");
            }
            else
            {
                // A scalar or an alias holds no keys.
                parser.SkipThisAndNestedEvents();
            }
        }
    }

    private static string ChildPath(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

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
                    var childPath = ChildPath(path, key.ToString());
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
