using Cortex.Contained.Contracts.Config;
using YamlDotNet.RepresentationModel;

namespace Cortex.Contained.Bridge.Setup;

/// <summary>
/// Surgical partial updates to <c>cortex.yml</c>. Each <c>UpdateXxx</c> method
/// loads the file as a YAML representation model, mutates ONLY the sections it
/// owns, and serialises back. Sections this mutator doesn't touch
/// (<c>tenants</c>, <c>channels</c>, <c>voice</c>, <c>memory</c>,
/// <c>speech</c>, etc.) are preserved verbatim — fixing the
/// 2026-05-20 bug where the LLM-provider save path regenerated the whole YAML
/// from scratch via <see cref="SetupHelpers.GenerateYaml"/>, dropping every
/// other section in the process.
/// </summary>
public static class CortexConfigMutator
{
    /// <summary>
    /// Replace the <c>llmProviders</c> block and <c>llmProxy.fallbackOrder</c>
    /// list with the given values. Every other key in the file is left as-is.
    /// </summary>
    public static string UpdateLlmProviders(
        string existingYaml,
        IReadOnlyList<LlmProviderConfig> providers,
        IReadOnlyList<string> fallbackOrder)
    {
        ArgumentNullException.ThrowIfNull(existingYaml);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(fallbackOrder);

        var stream = new YamlStream();
        using (var reader = new StringReader(existingYaml))
        {
            stream.Load(reader);
        }

        var root = (YamlMappingNode)stream.Documents[0].RootNode;

        // Build the new llmProviders sequence from the provided configs.
        var providersSeq = new YamlSequenceNode();
        foreach (var p in providers)
        {
            providersSeq.Add(BuildProviderNode(p));
        }

        SetMappingChild(root, "llmProviders", providersSeq);

        // llmProxy.fallbackOrder: replace the list while keeping any other
        // keys that may live under llmProxy.
        var fallbackSeq = new YamlSequenceNode();
        foreach (var name in fallbackOrder)
        {
            fallbackSeq.Add(new YamlScalarNode(name));
        }

        if (root.Children.TryGetValue(new YamlScalarNode("llmProxy"), out var proxyNode)
            && proxyNode is YamlMappingNode proxyMap)
        {
            SetMappingChild(proxyMap, "fallbackOrder", fallbackSeq);
        }
        else
        {
            var proxy = new YamlMappingNode();
            proxy.Add("fallbackOrder", fallbackSeq);
            SetMappingChild(root, "llmProxy", proxy);
        }

        return Serialise(stream);
    }

    private static YamlMappingNode BuildProviderNode(LlmProviderConfig p)
    {
        var node = new YamlMappingNode();
        node.Add("name", p.Name);
        node.Add("api", p.Api);
        if (!string.IsNullOrEmpty(p.BaseUrl))
        {
            node.Add("baseUrl", p.BaseUrl);
        }

        if (!string.IsNullOrEmpty(p.TokenType) && !string.Equals(p.TokenType, "bearer", StringComparison.Ordinal))
        {
            node.Add("tokenType", p.TokenType);
        }

        // Match SetupHelpers.GenerateYaml (SetupHelpers.cs:341-345): skip
        // emitting clientId when it equals the default GitHub Copilot OAuth
        // client ID, so a save round-trip doesn't add a clientId line that
        // wasn't there in the bootstrap output.
        if (!string.IsNullOrWhiteSpace(p.ClientId) &&
            !string.Equals(p.ClientId, SetupHelpers.DefaultCopilotOAuthClientId, StringComparison.Ordinal))
        {
            node.Add("clientId", p.ClientId);
        }

        // Match SetupHelpers.GenerateYaml: defaultModel comes from p.DefaultModel
        // when set, otherwise falls back to the first listed model (preserves
        // the existing on-disk wire format so the Bridge still resolves the same
        // model after a save).
        var defaultModel = !string.IsNullOrEmpty(p.DefaultModel)
            ? p.DefaultModel
            : (p.Models.Count > 0 ? p.Models[0] : null);
        if (!string.IsNullOrEmpty(defaultModel))
        {
            node.Add("defaultModel", defaultModel);
        }

        if (!string.IsNullOrEmpty(p.MemoryModel))
        {
            node.Add("memoryModel", p.MemoryModel);
        }

        if (p.Models is { Count: > 0 } models)
        {
            var modelsSeq = new YamlSequenceNode();
            foreach (var m in models)
            {
                modelsSeq.Add(new YamlScalarNode(m));
            }

            node.Add("models", modelsSeq);
        }

        if (p.ModelDefinitions is { Count: > 0 } defs)
        {
            var defsSeq = new YamlSequenceNode();
            foreach (var d in defs)
            {
                var dNode = new YamlMappingNode();
                dNode.Add("id", d.Id);
                if (d.ContextWindow > 0)
                {
                    dNode.Add("contextWindow", d.ContextWindow.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                if (d.MaxOutputTokens > 0)
                {
                    dNode.Add("maxOutputTokens", d.MaxOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                defsSeq.Add(dNode);
            }

            node.Add("modelDefinitions", defsSeq);
        }

        return node;
    }

    private static void SetMappingChild(YamlMappingNode parent, string key, YamlNode value)
    {
        var keyNode = new YamlScalarNode(key);
        if (parent.Children.ContainsKey(keyNode))
        {
            parent.Children[keyNode] = value;
        }
        else
        {
            parent.Add(keyNode, value);
        }
    }

    private static string Serialise(YamlStream stream)
    {
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }
}
