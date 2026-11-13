using System.ClientModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;
using OpenAI;
using SECFilingsAgenticRag.Common.AI;

namespace SECFilingsAgenticRag.Evals;

public sealed class EvalOptions
{
    public const string SectionName = "Evals";

    [Required]
    public string DatasetPath { get; set; } = "";

    [Required]
    public string ResultsPath { get; set; } = "";
}

public enum JudgeProvider { Ollama, OpenAICompatible }

// The judge is deliberately separate from the system under test: a model grading its own answers flatters itself, and the
// Quality evaluators are tuned for large cloud models that a local CPU cannot run.
public sealed class JudgeOptions
{
    public const string SectionName = "Evals:Judge";
    public const string ServiceKey = "judge";

    [Required]
    public JudgeProvider? Provider { get; set; }

    [Required]
    public string? Model { get; set; }

    // OpenAICompatible only; an Ollama judge uses ConnectionStrings:ollama like every other Ollama client.
    public Uri? Endpoint { get; set; }

    // Never in appsettings.json: user-secrets or Evals__Judge__ApiKey.
    public string? ApiKey { get; set; }

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 300;

    public IChatClient CreateClient(IServiceProvider services) => Provider switch
    {
        JudgeProvider.Ollama => ChatClientFactory.Create(services, Model!),
        _ => new OpenAI.Chat.ChatClient(Model,
                new ApiKeyCredential(ApiKey ?? throw new InvalidOperationException(
                    "Evals:Judge:ApiKey is required for the OpenAICompatible judge: set it with dotnet user-secrets or Evals__Judge__ApiKey.")),
                new OpenAIClientOptions { Endpoint = Endpoint, NetworkTimeout = TimeSpan.FromSeconds(TimeoutSeconds) })
            .AsIChatClient(),
    };
}
