using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using IntelliMon.Core.Knowledge;
using IntelliMon.Core.Metadata;

namespace IntelliMon.Core.Ai;

/// <summary>
/// Gemini-backed process knowledge enricher.
/// Accepts process identities via a bounded channel, deduplicates in-flight and recently-failed
/// requests, calls the Gemini Interactions API with a minimal structured JSON schema, and
/// writes successful results to the SQLite knowledge store.
/// </summary>
public sealed class GeminiProcessEnricher : IProcessKnowledgeEnricher, IDisposable
{
    private const string GeminiApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/interactions";
    private const string DefaultModel = "gemini-3.5-flash-lite";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    private readonly string _apiKey;
    private readonly SqliteKnowledgeStore _knowledgeStore;
    private readonly HttpClient _httpClient;

    private readonly Channel<ProcessIdentityInfo> _queue;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _recentFailures = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public GeminiProcessEnricher(string apiKey, SqliteKnowledgeStore knowledgeStore, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("Gemini API key must not be empty.", nameof(apiKey));
        }

        _apiKey = apiKey;
        _knowledgeStore = knowledgeStore ?? throw new ArgumentNullException(nameof(knowledgeStore));
        _httpClient = httpClient ?? new HttpClient { Timeout = RequestTimeout };

        _queue = Channel.CreateBounded<ProcessIdentityInfo>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <inheritdoc />
    public void EnqueueForEnrichment(ProcessIdentityInfo identity)
    {
        if (identity == null) return;

        string fingerprint = BuildFingerprint(identity);

        // Skip if in-flight or recently failed
        if (_inFlight.ContainsKey(fingerprint)) return;
        if (_recentFailures.TryGetValue(fingerprint, out var failedAt) && DateTime.UtcNow - failedAt < FailureCooldown) return;

        // Non-blocking try-write — channel will drop if full
        _queue.Writer.TryWrite(identity);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await foreach (var identity in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            string fingerprint = BuildFingerprint(identity);
            if (!_inFlight.TryAdd(fingerprint, 0)) continue;

            try
            {
                var entry = await CallGeminiAsync(identity, cancellationToken).ConfigureAwait(false);
                if (entry != null)
                {
                    _knowledgeStore.SaveKnowledge(entry);
                    _recentFailures.TryRemove(fingerprint, out _);
                }
                else
                {
                    _recentFailures[fingerprint] = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                _recentFailures[fingerprint] = DateTime.UtcNow;
            }
            finally
            {
                _inFlight.TryRemove(fingerprint, out _);
            }
        }
    }

    private async Task<ProcessKnowledgeEntry?> CallGeminiAsync(
        ProcessIdentityInfo identity,
        CancellationToken cancellationToken)
    {
        string prompt = BuildPrompt(identity);

        var requestBody = new GeminiInteractionRequest
        {
            Model = DefaultModel,
            Input = prompt,
            Store = false,
            ResponseFormat = new GeminiResponseFormat
            {
                Type = "text",
                MimeType = "application/json",
                Schema = BuildJsonSchema()
            }
        };

        string json = JsonSerializer.Serialize(requestBody, GeminiJsonContext.Default.GeminiInteractionRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        string url = $"{GeminiApiBaseUrl}?key={Uri.EscapeDataString(_apiKey)}";
        using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseResponse(responseJson, identity);
    }

    private static ProcessKnowledgeEntry? ParseResponse(string responseJson, ProcessIdentityInfo identity)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Walk: steps[] -> last model_output step -> content[] -> text type -> text value
            string? outputText = null;
            if (root.TryGetProperty("steps", out var steps))
            {
                for (int i = steps.GetArrayLength() - 1; i >= 0; i--)
                {
                    var step = steps[i];
                    if (step.TryGetProperty("type", out var typeEl) &&
                        typeEl.GetString() == "model_output" &&
                        step.TryGetProperty("content", out var contentArr))
                    {
                        foreach (var part in contentArr.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out var partType) &&
                                partType.GetString() == "text" &&
                                part.TryGetProperty("text", out var textEl))
                            {
                                outputText = textEl.GetString();
                                break;
                            }
                        }
                        if (outputText != null) break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(outputText)) return null;

            using var aiDoc = JsonDocument.Parse(outputText);
            var ai = aiDoc.RootElement;

            string displayName = GetString(ai, "displayName") ?? identity.ProcessName;
            string summary = GetString(ai, "summary") ?? "An external process.";
            string category = GetString(ai, "category") ?? "Unknown";
            string expectedHighResourceReason = GetString(ai, "expectedHighResourceReason") ?? string.Empty;
            string? product = GetString(ai, "product");
            string? publisher = GetString(ai, "publisher");
            double confidence = ai.TryGetProperty("confidence", out var confEl) && confEl.TryGetDouble(out double c) ? Math.Clamp(c, 0.0, 1.0) : 0.5;

            // If confidence is very low, discard — we don't want bad data in the knowledge base
            if (confidence < 0.3) return null;

            string fullSummary = string.IsNullOrWhiteSpace(expectedHighResourceReason)
                ? summary
                : $"{summary} High resource usage is typically caused by: {expectedHighResourceReason}";

            var now = DateTime.UtcNow;
            return new ProcessKnowledgeEntry(
                Id: 0,
                BinaryName: identity.ProcessName,
                Publisher: publisher ?? identity.SignerName ?? identity.CompanyName,
                ServiceName: null,
                PathPattern: null,
                DisplayName: displayName,
                ProductName: product ?? identity.ProductName,
                Summary: fullSummary,
                Category: category,
                Source: KnowledgeSource.AiLearned,
                Confidence: confidence,
                FirstSeenUtc: now,
                LastSeenUtc: now
            );
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement el, string property)
    {
        return el.TryGetProperty(property, out var prop) ? prop.GetString() : null;
    }

    private static string BuildPrompt(ProcessIdentityInfo identity)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a Windows process identification assistant. Identify the following Windows process and explain its typical behavior.");
        sb.AppendLine();
        sb.AppendLine("Process Details:");
        sb.AppendLine($"  Binary Name: {identity.ProcessName}");
        sb.AppendLine($"  Executable Path: {CommandLineSanitizer.SanitizePath(identity.ExecutablePath)}");

        if (!string.IsNullOrWhiteSpace(identity.FileDescription))
            sb.AppendLine($"  File Description: {identity.FileDescription}");
        if (!string.IsNullOrWhiteSpace(identity.ProductName))
            sb.AppendLine($"  Product Name: {identity.ProductName}");
        if (!string.IsNullOrWhiteSpace(identity.CompanyName))
            sb.AppendLine($"  Company: {identity.CompanyName}");
        if (!string.IsNullOrWhiteSpace(identity.SignerName))
            sb.AppendLine($"  Digital Signature: {identity.SignerName}");
        if (!string.IsNullOrWhiteSpace(identity.CommandLine))
            sb.AppendLine($"  Command Line: {CommandLineSanitizer.SanitizeCommandLine(identity.CommandLine)}");
        if (identity.HostedServices.Count > 0)
            sb.AppendLine($"  Hosted Services: {string.Join(", ", identity.HostedServices)}");
        if (identity.Ancestry.Count > 0)
            sb.AppendLine($"  Process Ancestry: {identity.FormattedAncestry}");

        sb.AppendLine();
        sb.AppendLine("This process was detected consuming elevated CPU, memory, or disk resources on a Windows desktop.");
        sb.AppendLine("Identify what it is and why it might use high resources. Be specific and concise.");
        sb.AppendLine("Set confidence to 0.0 if you genuinely cannot identify this process.");

        return sb.ToString();
    }

    private static JsonElement BuildJsonSchema()
    {
        const string schemaJson = @"{
            ""type"": ""object"",
            ""properties"": {
                ""displayName"":             { ""type"": ""string"" },
                ""product"":                 { ""type"": ""string"" },
                ""publisher"":               { ""type"": ""string"" },
                ""summary"":                 { ""type"": ""string"" },
                ""category"":                { ""type"": ""string"" },
                ""expectedHighResourceReason"": { ""type"": ""string"" },
                ""fingerprintMatch"":        { ""type"": ""string"" },
                ""confidence"":              { ""type"": ""number"" }
            },
            ""required"": [""displayName"", ""summary"", ""category"", ""expectedHighResourceReason"", ""confidence""]
        }";
        return JsonDocument.Parse(schemaJson).RootElement;
    }

    private static string BuildFingerprint(ProcessIdentityInfo identity)
    {
        // Fingerprint: binaryName + signerName (or companyName) — stable across PIDs
        return $"{identity.ProcessName.ToLowerInvariant()}|{identity.SignerName ?? identity.CompanyName ?? string.Empty}".ToLowerInvariant();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _queue.Writer.TryComplete();
            _httpClient.Dispose();
        }
    }
}

// ─── Minimal JSON serialization models ───────────────────────────────────────

internal sealed class GeminiInteractionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;

    [JsonPropertyName("store")]
    public bool Store { get; set; }

    [JsonPropertyName("response_format")]
    public GeminiResponseFormat? ResponseFormat { get; set; }
}

internal sealed class GeminiResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = "application/json";

    [JsonPropertyName("schema")]
    public JsonElement Schema { get; set; }
}

[JsonSerializable(typeof(GeminiInteractionRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class GeminiJsonContext : JsonSerializerContext { }
