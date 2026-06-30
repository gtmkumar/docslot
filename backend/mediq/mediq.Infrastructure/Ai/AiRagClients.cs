using System.Net.Http.Json;
using mediq.Application.Abstractions;
using mediq.Utilities.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace mediq.Infrastructure.Ai;

/// <summary>
/// Calls the AI sibling service's <c>POST /ai/v1/rag/ask</c> (STRICTLY READ-ONLY — there is no index method on
/// this client, so the read-that-writes <c>/rag/index</c> path is structurally unreachable from .NET, bug #3).
/// The caller's bearer JWT + declared purpose-of-use are forwarded (the AI service validates the JWT, runs its
/// consent + medical_history.read gate, governs any external-LLM PHI egress, and writes the purpose log). On a
/// 5xx / network failure the adapter returns null; an AI 4xx is propagated as a typed exception. The question
/// and answer (PHI) are NEVER logged (status only).
/// </summary>
public sealed class HttpAiRagClient(
    HttpClient http, IHttpContextAccessor context, ILogger<HttpAiRagClient> logger) : IAiRagClient
{
    public async Task<RagAnswerResult?> AskAsync(RagAskInput input, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/ai/v1/rag/ask")
            {
                Content = JsonContent.Create(new
                {
                    patientId = input.PatientId.ToString(),
                    question = input.Question,
                }),
            };
            var auth = context.HttpContext?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);
            if (!string.IsNullOrWhiteSpace(input.DeclaredPurpose))
                req.Headers.TryAddWithoutValidation("X-Purpose-Of-Use", input.DeclaredPurpose);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("AI RAG ask returned {Status}", (int)resp.StatusCode);
                AiErrorMapper.ThrowIfClientError(resp.StatusCode);
                return null;
            }

            var dto = await resp.Content.ReadFromJsonAsync<AiRagAskResponse>(ct);
            if (dto is null) return null;
            return new RagAnswerResult(
                input.PatientId,
                dto.Answer ?? "",
                dto.Mode ?? "extractive",
                (dto.Citations ?? []).Select(c => new RagCitationResult(
                    c.HistoryId ?? "", c.RecordType, c.Title, c.Severity, c.Score)).ToList(),
                dto.Retrieved,
                "ai-service-http");
        }
        catch (Exception ex) when (ex is not AppExceptionBase and not KeyNotFoundException)
        {
            logger.LogWarning(ex, "AI RAG call failed; answer unavailable.");   // no question/answer (PHI) in the message
            return null;
        }
    }

    public async Task<RagStatusResult?> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/ai/v1/rag/status");
            var auth = context.HttpContext?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("AI RAG status returned {Status}", (int)resp.StatusCode);
                AiErrorMapper.ThrowIfClientError(resp.StatusCode);
                return null;
            }

            var dto = await resp.Content.ReadFromJsonAsync<AiRagStatusResponse>(ct);
            if (dto is null) return null;
            return new RagStatusResult(
                dto.Embeddings, dto.PatientsIndexed,
                (dto.KnowledgeBases ?? []).Select(k => new RagKnowledgeBaseResult(
                    k.KbKey ?? "", k.Name ?? "", k.DocumentCount)).ToList(),
                "ai-service-http");
        }
        catch (Exception ex) when (ex is not AppExceptionBase and not KeyNotFoundException)
        {
            logger.LogWarning(ex, "AI RAG status call failed; status unavailable.");
            return null;
        }
    }

    // Matches the AI service's RagAskResponse schema (camelCase). The echoed question is INTENTIONALLY not bound
    // (the caller already holds it; keeps PHI out of the response envelope).
    private sealed record AiRagAskResponse(
        string? PatientId, string? Answer, string? Mode, List<AiRagCitation>? Citations, int Retrieved);
    private sealed record AiRagCitation(string? HistoryId, string? RecordType, string? Title, string? Severity, double Score);

    // Matches the AI service's RagStatusResponse / KnowledgeBaseInfo schema (camelCase) — operational counts.
    private sealed record AiRagStatusResponse(int Embeddings, int PatientsIndexed, List<AiKnowledgeBaseInfo>? KnowledgeBases);
    private sealed record AiKnowledgeBaseInfo(string? KbKey, string? Name, int DocumentCount);
}

/// <summary>
/// Deterministic DEV/test stub for RAG ask — a clearly-labelled extractive mock so the endpoint works WITHOUT
/// the AI service. It NEVER indexes (no index method exists) and NEVER logs/returns the question; the answer is
/// an explicitly-labelled "[stub]" string with no citations. Production swaps the HTTP adapter behind the seam.
/// </summary>
public sealed class StubAiRagClient : IAiRagClient
{
    public Task<RagAnswerResult?> AskAsync(RagAskInput input, CancellationToken ct) =>
        Task.FromResult<RagAnswerResult?>(new RagAnswerResult(
            input.PatientId,
            Answer: "[stub] No AI model ran in this environment; connect the AI service for a real answer.",
            Mode: "extractive",
            Citations: [],
            Retrieved: 0,
            Source: "stub-dev"));

    public Task<RagStatusResult?> GetStatusAsync(CancellationToken ct) =>
        // Deterministic empty status so the ops view renders WITHOUT the AI service.
        Task.FromResult<RagStatusResult?>(new RagStatusResult(
            Embeddings: 0, PatientsIndexed: 0, KnowledgeBases: [], Source: "stub-dev"));
}
