using System.Net;
using System.Text;
using Cortex.Contained.Bridge;
using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests;

/// <summary>
/// Pins that <see cref="SetupHelpers.FetchCopilotApiModelsAsync"/> preserves the
/// <c>supported_endpoints</c> metadata reported by the Copilot <c>/models</c> API, and that
/// both YAML writers (<see cref="CortexConfigMutator"/> and <see cref="BridgeSettingsWriter"/>)
/// persist it. Without this metadata the Bridge cannot tell whether a model requires the
/// newer <c>/responses</c> endpoint instead of <c>/chat/completions</c>.
/// </summary>
public class CopilotEndpointMetadataTests
{
    private const string ModelsResponseBody = """
        {
          "data": [
            {
              "id": "gpt-5.6-sol",
              "name": "GPT-5.6 Sol",
              "vendor": "openai",
              "model_picker_description": "GPT-5.6 Sol",
              "supported_endpoints": ["/responses", "ws:/responses"],
              "capabilities": {
                "type": "chat",
                "family": "gpt-5.6",
                "limits": {
                  "max_context_window_tokens": 1050000,
                  "max_output_tokens": 128000
                }
              }
            }
          ]
        }
        """;

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task FetchCopilotApiModelsAsync_PreservesSupportedEndpoints()
    {
        using var handler = new StubHandler(Json(ModelsResponseBody));
        using var client = new HttpClient(handler);

        var models = await SetupHelpers.FetchCopilotApiModelsAsync(
            "oauth-token", "https://api.githubcopilot.com", client, CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal("gpt-5.6-sol", model.Id);
        Assert.Equal(1_050_000, model.ContextWindow);
        Assert.Equal(128_000, model.MaxOutputTokens);
        Assert.Equal(["/responses", "ws:/responses"], model.SupportedEndpoints);
    }

    [Fact]
    public async Task FetchCopilotApiModelsAsync_FiltersBlankAndDedupesCaseInsensitively()
    {
        const string body = """
            {
              "data": [
                {
                  "id": "gpt-5.6-sol",
                  "name": "GPT-5.6 Sol",
                  "supported_endpoints": ["/responses", "", "  ", "/Responses", "ws:/responses"],
                  "capabilities": { "type": "chat" }
                }
              ]
            }
            """;
        using var handler = new StubHandler(Json(body));
        using var client = new HttpClient(handler);

        var models = await SetupHelpers.FetchCopilotApiModelsAsync(
            "oauth-token", "https://api.githubcopilot.com", client, CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal(["/responses", "ws:/responses"], model.SupportedEndpoints);
    }

    [Fact]
    public async Task FetchCopilotApiModelsAsync_MissingSupportedEndpoints_DefaultsToEmpty()
    {
        const string body = """
            {
              "data": [
                {
                  "id": "gpt-5.5",
                  "name": "GPT-5.5",
                  "capabilities": { "type": "chat" }
                }
              ]
            }
            """;
        using var handler = new StubHandler(Json(body));
        using var client = new HttpClient(handler);

        var models = await SetupHelpers.FetchCopilotApiModelsAsync(
            "oauth-token", "https://api.githubcopilot.com", client, CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Empty(model.SupportedEndpoints);
    }

    [Fact]
    public void CortexConfigMutator_UpdateLlmProviders_PersistsSupportedEndpoints()
    {
        const string existingYaml = """
            agentHubUrl: http://localhost:5100/hub/agent
            llmProviders:
              - name: github-copilot-api
                api: github-copilot-api
            llmProxy:
              fallbackOrder:
                - github-copilot-api
            """;

        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            Models = ["gpt-5.6-sol"],
            DefaultModel = "gpt-5.6-sol",
            ModelDefinitions =
            [
                new LlmModelDefinition
                {
                    Id = "gpt-5.6-sol",
                    ContextWindow = 1_050_000,
                    MaxOutputTokens = 128_000,
                    SupportedEndpoints = ["/responses", "ws:/responses"],
                },
            ],
        };

        var updated = CortexConfigMutator.UpdateLlmProviders(
            existingYaml,
            [provider],
            ["github-copilot-api"]);

        Assert.Contains("supportedEndpoints:", updated, StringComparison.Ordinal);
        Assert.Contains("- /responses", updated, StringComparison.Ordinal);
        Assert.Contains("- ws:/responses", updated, StringComparison.Ordinal);

        var endpointsIdx = updated.IndexOf("supportedEndpoints:", StringComparison.Ordinal);
        var after = updated[endpointsIdx..];
        var responsesIdx = after.IndexOf("/responses", StringComparison.Ordinal);
        var wsIdx = after.IndexOf("ws:/responses", StringComparison.Ordinal);
        Assert.True(responsesIdx > 0);
        Assert.True(wsIdx > responsesIdx, "/responses should be listed before ws:/responses");
    }

    [Fact]
    public void BridgeSettingsWriter_PersistSettingsToYaml_PersistsSupportedEndpoints()
    {
        var config = new BridgeConfig
        {
            LlmProviders =
            [
                new LlmProviderConfig
                {
                    Name = "github-copilot-api",
                    Api = "github-copilot-api",
                    Models = ["gpt-5.6-sol"],
                    DefaultModel = "gpt-5.6-sol",
                    ModelDefinitions =
                    [
                        new LlmModelDefinition
                        {
                            Id = "gpt-5.6-sol",
                            ContextWindow = 1_050_000,
                            MaxOutputTokens = 128_000,
                            SupportedEndpoints = ["/responses", "ws:/responses"],
                        },
                    ],
                },
            ],
        };
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");

        try
        {
            BridgeSettingsWriter.PersistSettingsToYaml(config, path);
            var yaml = File.ReadAllText(path);

            Assert.Contains("supportedEndpoints:", yaml, StringComparison.Ordinal);
            Assert.Contains("- /responses", yaml, StringComparison.Ordinal);
            Assert.Contains("- ws:/responses", yaml, StringComparison.Ordinal);

            var endpointsIdx = yaml.IndexOf("supportedEndpoints:", StringComparison.Ordinal);
            var after = yaml[endpointsIdx..];
            var responsesIdx = after.IndexOf("/responses", StringComparison.Ordinal);
            var wsIdx = after.IndexOf("ws:/responses", StringComparison.Ordinal);
            Assert.True(responsesIdx > 0);
            Assert.True(wsIdx > responsesIdx, "/responses should be listed before ws:/responses");
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    [Fact]
    public void BridgeSettingsWriter_PersistSettingsToYaml_EmptySupportedEndpoints_OmitsSection()
    {
        var config = new BridgeConfig
        {
            LlmProviders =
            [
                new LlmProviderConfig
                {
                    Name = "openai",
                    Api = "openai-completions",
                    Models = ["gpt-5.5"],
                    DefaultModel = "gpt-5.5",
                    ModelDefinitions =
                    [
                        new LlmModelDefinition { Id = "gpt-5.5", ContextWindow = 200_000, MaxOutputTokens = 32_000 },
                    ],
                },
            ],
        };
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");

        try
        {
            BridgeSettingsWriter.PersistSettingsToYaml(config, path);
            var yaml = File.ReadAllText(path);

            Assert.DoesNotContain("supportedEndpoints:", yaml, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;

        public StubHandler(params HttpResponseMessage[] responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(this.responses.Dequeue());
        }
    }
}
