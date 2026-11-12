using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Extensions.AI;

Console.OutputEncoding = Encoding.UTF8;

var api = Option("--api") ?? Environment.GetEnvironmentVariable("SECFILINGS_API") ?? "http://localhost:5180";
var agent = Option("--agent") ?? "standard-rag";
var thread = Option("--thread");
var ask = Option("--ask");
var showToolArgs = args.Contains("--show-tool-args");

// Answers on CPU take minutes; the request CancellationToken bounds a run instead.
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var endpoint = $"{api.TrimEnd('/')}/agents/{agent}";
var client = new AGUIChatClient(new AGUIChatClientOptions(http, endpoint));

if (ask is not null)
    return await RunAsync(ask, CancellationToken.None);

Console.WriteLine($"SEC Filings Chat — {endpoint}  (commands: /new, /exit)");
CancellationTokenSource? running = null;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = running is not null;
    running?.Cancel();
};
while (true)
{
    Console.Write("you> ");
    var line = Console.ReadLine()?.Trim();
    if (line is null or "/exit")
        return 0;
    if (line == "/new")
        thread = null;
    else if (line.Length > 0)
    {
        using var cancel = new CancellationTokenSource();
        running = cancel;
        await RunAsync(line, cancel.Token);
        running = null;
    }
}

async Task<int> RunAsync(string question, CancellationToken ct)
{
    thread ??= Guid.NewGuid().ToString("N");
    // The server reconciles incoming messages with the stored session by id; a blank id (the default) loses the history.
    var message = new ChatMessage(ChatRole.User, question) { MessageId = Guid.NewGuid().ToString("N") };
    // AGUIChatClient is stateless: the server's session store holds the history, so a run sends only the new message,
    // addressed to the conversation by its AG-UI thread id. The client fills in the run id and the messages.
    var options = new ChatOptions { RawRepresentationFactory = _ => new RunAgentInput { ThreadId = thread } };

    var started = Stopwatch.GetTimestamp();
    var toolCalls = 0;
    Console.Write("assistant> ");
    try
    {
        await foreach (var update in client.GetStreamingResponseAsync([message], options, ct))
        {
            // update.Text holds only TextContent, so a reasoning model's thinking (TextReasoningContent) is never printed.
            Console.Write(update.Text);
            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    toolCalls++;
                    Console.WriteLine($"\n🔎 {call.Name}({(showToolArgs ? JsonSerializer.Serialize(call.Arguments) : "...")})");
                }
                else if (content is FunctionResultContent result)
                    Console.WriteLine($"  ↳ {Summarize(result.Result)}");
            }
        }
    }
    catch (HttpRequestException e) when (e.StatusCode is null)
    {
        WriteError($"API unreachable at {endpoint}: {e.Message}");
        return 2;
    }
    catch (Exception e)
    {
        WriteError(ct.IsCancellationRequested ? "Cancelled." : e.Message);
        return 1;
    }

    var elapsed = Stopwatch.GetElapsedTime(started);
    Console.WriteLine();
    Console.WriteLine(ask is null
        ? $"({elapsed.TotalSeconds:0.0} s)"
        : $"agent={agent} thread={thread} toolCalls={toolCalls} elapsedMs={elapsed.TotalMilliseconds:0}");
    return 0;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Summarize(object? result)
{
    var text = (result?.ToString() ?? "").ReplaceLineEndings(" ");
    return text.Length <= 100 ? text : text[..100] + "…";
}

static void WriteError(string message)
{
    if (!Console.IsErrorRedirected)
        Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\n{message}");
    Console.ResetColor();
}
