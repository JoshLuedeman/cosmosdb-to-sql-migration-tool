using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Thread-safe blackboard implementation of <see cref="ISharedAssessmentContext"/>. A single private
/// lock guards every piece of mutable state (the four domain outputs, the message log, and the result
/// log) so concurrently scheduled agents observe a consistent view.
/// </summary>
/// <remarks>
/// Domain outputs are write-once: each <c>Set*</c> method throws <see cref="InvalidOperationException"/>
/// on a second write, which surfaces accidental concurrent double-production as a clear failure rather
/// than silent corruption. Reads of the domain outputs return the committed reference (the underlying
/// model objects are not mutated after being committed); reads of <see cref="Messages"/> and
/// <see cref="Results"/> return immutable snapshots taken under the lock.
/// </remarks>
public sealed class SharedAssessmentContext : ISharedAssessmentContext
{
    private readonly object _gate = new();
    private readonly List<AgentMessage> _messages = new();
    private readonly List<AgentResult> _results = new();

    private CosmosDbAnalysis? _cosmosAnalysis;
    private SqlMigrationAssessment? _sqlAssessment;
    private DataQualityAnalysis? _dataQualityAnalysis;
    private DataFactoryEstimate? _dataFactoryEstimate;
    private ValidationReport? _validationReport;

    /// <summary>
    /// Creates a new context for a single database assessment.
    /// </summary>
    /// <param name="databaseName">Name of the Cosmos DB database under assessment.</param>
    /// <param name="cosmosAccountName">Friendly name of the Cosmos DB account under assessment.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="databaseName"/> is null or empty.</exception>
    public SharedAssessmentContext(string databaseName, string cosmosAccountName)
    {
        if (string.IsNullOrEmpty(databaseName))
        {
            throw new ArgumentException("Database name cannot be null or empty.", nameof(databaseName));
        }

        DatabaseName = databaseName;
        CosmosAccountName = cosmosAccountName ?? string.Empty;
    }

    /// <inheritdoc />
    public string DatabaseName { get; }

    /// <inheritdoc />
    public string CosmosAccountName { get; }

    /// <inheritdoc />
    public CosmosDbAnalysis? CosmosAnalysis { get { lock (_gate) { return _cosmosAnalysis; } } }

    /// <inheritdoc />
    public SqlMigrationAssessment? SqlAssessment { get { lock (_gate) { return _sqlAssessment; } } }

    /// <inheritdoc />
    public DataQualityAnalysis? DataQualityAnalysis { get { lock (_gate) { return _dataQualityAnalysis; } } }

    /// <inheritdoc />
    public DataFactoryEstimate? DataFactoryEstimate { get { lock (_gate) { return _dataFactoryEstimate; } } }

    /// <inheritdoc />
    public ValidationReport? ValidationReport { get { lock (_gate) { return _validationReport; } } }

    /// <inheritdoc />
    public bool HasCosmosAnalysis { get { lock (_gate) { return _cosmosAnalysis is not null; } } }

    /// <inheritdoc />
    public bool HasSqlAssessment { get { lock (_gate) { return _sqlAssessment is not null; } } }

    /// <inheritdoc />
    public bool HasDataQualityAnalysis { get { lock (_gate) { return _dataQualityAnalysis is not null; } } }

    /// <inheritdoc />
    public bool HasDataFactoryEstimate { get { lock (_gate) { return _dataFactoryEstimate is not null; } } }

    /// <inheritdoc />
    public bool HasValidationReport { get { lock (_gate) { return _validationReport is not null; } } }

    /// <inheritdoc />
    public IReadOnlyList<AgentMessage> Messages { get { lock (_gate) { return _messages.ToArray(); } } }

    /// <inheritdoc />
    public IReadOnlyList<AgentResult> Results { get { lock (_gate) { return _results.ToArray(); } } }

    /// <inheritdoc />
    public void SetCosmosAnalysis(string producerName, CosmosDbAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        lock (_gate)
        {
            ThrowIfAlreadySet(_cosmosAnalysis, nameof(CosmosAnalysis), producerName);
            _cosmosAnalysis = analysis;
        }
    }

    /// <inheritdoc />
    public void SetSqlAssessment(string producerName, SqlMigrationAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        lock (_gate)
        {
            ThrowIfAlreadySet(_sqlAssessment, nameof(SqlAssessment), producerName);
            _sqlAssessment = assessment;
        }
    }

    /// <inheritdoc />
    public void SetDataQualityAnalysis(string producerName, DataQualityAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        lock (_gate)
        {
            ThrowIfAlreadySet(_dataQualityAnalysis, nameof(DataQualityAnalysis), producerName);
            _dataQualityAnalysis = analysis;
        }
    }

    /// <inheritdoc />
    public void SetDataFactoryEstimate(string producerName, DataFactoryEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        lock (_gate)
        {
            ThrowIfAlreadySet(_dataFactoryEstimate, nameof(DataFactoryEstimate), producerName);
            _dataFactoryEstimate = estimate;
        }
    }

    /// <inheritdoc />
    public void SetValidationReport(string producerName, ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            ThrowIfAlreadySet(_validationReport, nameof(ValidationReport), producerName);
            _validationReport = report;
        }
    }

    /// <inheritdoc />
    public void AddMessage(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            _messages.Add(message);
        }
    }

    /// <inheritdoc />
    public void LogInfo(string agentName, string text) => AddMessage(AgentMessage.Info(agentName, text));

    /// <inheritdoc />
    public void LogWarning(string agentName, string text) => AddMessage(AgentMessage.Warning(agentName, text));

    /// <inheritdoc />
    public void LogError(string agentName, string text) => AddMessage(AgentMessage.Error(agentName, text));

    /// <inheritdoc />
    public void RecordResult(AgentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _results.Add(result);
        }
    }

    /// <inheritdoc />
    public AgentResult? GetResult(string agentName)
    {
        lock (_gate)
        {
            for (var i = _results.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_results[i].AgentName, agentName, StringComparison.Ordinal))
                {
                    return _results[i];
                }
            }

            return null;
        }
    }

    /// <inheritdoc />
    public bool HasSucceeded(AgentRole role)
    {
        lock (_gate)
        {
            foreach (var result in _results)
            {
                if (result.Role == role && result.Status == AgentRunStatus.Succeeded)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetMissingRequiredOutputs()
    {
        lock (_gate)
        {
            var missing = new List<string>();
            if (_cosmosAnalysis is null) missing.Add(nameof(CosmosAnalysis));
            if (_sqlAssessment is null) missing.Add(nameof(SqlAssessment));
            if (_dataFactoryEstimate is null) missing.Add(nameof(DataFactoryEstimate));
            return missing;
        }
    }

    /// <inheritdoc />
    public bool IsCompleteForAssessmentResult() => GetMissingRequiredOutputs().Count == 0;

    /// <inheritdoc />
    public AssessmentResult ToAssessmentResult()
    {
        lock (_gate)
        {
            return new AssessmentResult
            {
                CosmosAccountName = CosmosAccountName,
                DatabaseName = DatabaseName,
                CosmosAnalysis = _cosmosAnalysis ?? new CosmosDbAnalysis(),
                SqlAssessment = _sqlAssessment ?? new SqlMigrationAssessment(),
                DataFactoryEstimate = _dataFactoryEstimate ?? new DataFactoryEstimate(),
                DataQualityAnalysis = _dataQualityAnalysis
            };
        }
    }

    private static void ThrowIfAlreadySet(object? current, string outputName, string producerName)
    {
        if (current is not null)
        {
            throw new InvalidOperationException(
                $"{outputName} has already been set and cannot be overwritten by '{producerName}'. " +
                "Each domain output is write-once on the shared assessment context.");
        }
    }
}
