using System.Threading;
using System.Threading.Tasks;
using IntelliMon.Core.Metadata;

namespace IntelliMon.Core.Ai;

/// <summary>
/// Abstraction for asynchronously enriching an unknown process with AI-derived knowledge.
/// </summary>
public interface IProcessKnowledgeEnricher
{
    /// <summary>
    /// Submits a process identity for asynchronous AI enrichment.
    /// The implementation must be non-blocking and queue the request internally.
    /// </summary>
    void EnqueueForEnrichment(ProcessIdentityInfo identity);

    /// <summary>
    /// Starts the background enrichment loop.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);
}
