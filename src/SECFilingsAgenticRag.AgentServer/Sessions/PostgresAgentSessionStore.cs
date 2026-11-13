using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Npgsql;
using NpgsqlTypes;

namespace SECFilingsAgenticRag.AgentServer.Sessions;

#pragma warning disable MAAI001 // AgentSessionStore is experimental; Learn's self-hosting page names it the durable-storage extension point

// Custom (no built-in fits): MAF ships only InMemoryAgentSessionStore and NoopAgentSessionStore; Learn: "provide an
// AgentSessionStore implementation backed by storage appropriate for your application". The serialized session holds the chat
// history (InMemoryChatHistoryProvider keeps it in session state), so this one table persists whole conversations.
public sealed class PostgresAgentSessionStore(NpgsqlDataSource dataSource, ILogger<PostgresAgentSessionStore> logger) : AgentSessionStore
{
    // Rows are keyed by agent as well as thread, so one AG-UI thread id used against another agent starts a fresh conversation
    // instead of resuming one built with other instructions and tools. Partitions are part of the key because an isolation
    // decorator puts the caller's identity there. session is json, not jsonb: jsonb reorders object keys, and System.Text.Json
    // reads a polymorphic "$type" discriminator only when it comes first, so a jsonb round trip makes the session unreadable.
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS agent_sessions (
          agent_id        text        NOT NULL,
          conversation_id text        NOT NULL,
          partitions      jsonb       NOT NULL DEFAULT '{}',
          session         json        NOT NULL,
          turns           int         NOT NULL DEFAULT 0,
          created_at      timestamptz NOT NULL DEFAULT now(),
          updated_at      timestamptz NOT NULL DEFAULT now(),
          PRIMARY KEY (agent_id, conversation_id, partitions)
        )
        """;

    private const string Key = "agent_id = @agent AND conversation_id = @conversation AND partitions = @partitions";

    // turns doubles as the row version. A save succeeds only if nobody saved since this run loaded the session, so two runs on
    // one thread (double submit, a second tab) cannot interleave their histories: the first save wins.
    private const string SaveSql = """
        INSERT INTO agent_sessions AS s (agent_id, conversation_id, partitions, session, turns)
        VALUES (@agent, @conversation, @partitions, @session, 1)
        ON CONFLICT (agent_id, conversation_id, partitions) DO UPDATE
          SET session = EXCLUDED.session, turns = s.turns + 1, updated_at = now()
          WHERE s.turns = @turns
        """;

    private readonly SemaphoreSlim schemaLock = new(1, 1);
    private readonly ConditionalWeakTable<AgentSession, StrongBox<int>> loadedTurns = [];
    private volatile bool schemaReady;

    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        await using var command = await CommandAsync($"SELECT session::text, turns FROM agent_sessions WHERE {Key}", agent, key, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        // Not a disposable JsonDocument: the session's state bag keeps referring to the element after deserialization.
        var session = await agent.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)), cancellationToken: cancellationToken);
        loadedTurns.Add(session, new(reader.GetInt32(1)));
        return session;
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        await using var command = await CommandAsync(SaveSql, agent, key, cancellationToken);
        command.Parameters.Add(new NpgsqlParameter("session", NpgsqlDbType.Json) { Value = serialized.GetRawText() });
        command.Parameters.AddWithValue("turns", loadedTurns.TryGetValue(session, out var turns) ? turns.Value : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            logger.LogWarning("Conversation {Conversation} of {Agent} was saved by a concurrent run; this run's turn was not stored",
                key.SessionId, agent.Name);
    }

    public override async ValueTask DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        await using var command = await CommandAsync($"DELETE FROM agent_sessions WHERE {Key}", agent, key, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlCommand> CommandAsync(string sql, AIAgent agent, AgentSessionStoreKey key, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("agent", agent.Id);
        command.Parameters.AddWithValue("conversation", key.SessionId);
        command.Parameters.Add(new NpgsqlParameter("partitions", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(key.Partitions ?? new Dictionary<string, string>()) });
        return command;
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (schemaReady)
            return;

        await schemaLock.WaitAsync(ct);
        try
        {
            if (!schemaReady)
            {
                await using var command = dataSource.CreateCommand(SchemaSql);
                await command.ExecuteNonQueryAsync(ct);
                schemaReady = true;
            }
        }
        finally
        {
            schemaLock.Release();
        }
    }
}
