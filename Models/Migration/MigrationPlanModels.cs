namespace CosmosToSqlAssessment.Models.Migration
{
    /// <summary>
    /// Overall readiness verdict for an incremental (change-feed-based) Cosmos DB → SQL migration (#137),
    /// synthesized from change-feed availability (#134), sync estimates (#135), and the cutover window (#136).
    /// </summary>
    public enum MigrationReadiness
    {
        /// <summary>Readiness could not be determined (e.g. no containers in scope).</summary>
        Unknown,

        /// <summary>All signals are healthy; the phased plan can proceed with standard precautions.</summary>
        Ready,

        /// <summary>Viable, but with caveats that must be addressed (e.g. delete-fidelity verification, elevated risk, cutover needs pre-catch-up).</summary>
        ReadyWithCaveats,

        /// <summary>A blocking issue prevents a reliable incremental migration (e.g. unsustainable steady-state sync, no migration scope).</summary>
        NotReady
    }

    /// <summary>
    /// A single phase of the phased migration plan (#137): its objective, tooling, ordered steps, entry and
    /// exit criteria, an optional modeled duration, risks, and rollback guidance where relevant.
    /// </summary>
    public sealed class MigrationPlanPhase
    {
        /// <summary>1-based ordinal position of the phase in the plan.</summary>
        public int Order { get; set; }

        /// <summary>Short phase name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The phase's objective / definition of done.</summary>
        public string Objective { get; set; } = string.Empty;

        /// <summary>The primary Azure tooling for the phase.</summary>
        public string PrimaryTooling { get; set; } = string.Empty;

        /// <summary>Ordered, actionable steps for the phase.</summary>
        public List<string> Steps { get; set; } = new();

        /// <summary>Conditions that must hold before the phase can start.</summary>
        public List<string> EntryCriteria { get; set; } = new();

        /// <summary>Conditions that must hold before the phase is considered complete.</summary>
        public List<string> ExitCriteria { get; set; } = new();

        /// <summary>
        /// Modeled duration for the phase, when one can be estimated. <c>null</c> for operationally-scheduled
        /// phases (e.g. cutover preparation, verification) or when an upstream estimate is unavailable.
        /// </summary>
        public TimeSpan? EstimatedDuration { get; set; }

        /// <summary>Explanation of how <see cref="EstimatedDuration"/> was derived (or why it is unavailable).</summary>
        public string DurationBasis { get; set; } = string.Empty;

        /// <summary>Risks and watch-outs specific to the phase.</summary>
        public List<string> Risks { get; set; } = new();

        /// <summary>Rollback / contingency guidance relevant to the phase, when applicable.</summary>
        public string? RollbackGuidance { get; set; }
    }

    /// <summary>
    /// A phased migration plan for an incremental (change-feed-based) Cosmos DB → SQL migration (#137).
    /// Synthesizes the #134/#135/#136 analyses into an ordered, criteria-driven plan with an overall
    /// readiness verdict. Deliberately separates elapsed preparation time from business downtime so the
    /// two are never conflated. All durations are heuristic planning estimates, not guaranteed SLAs.
    /// </summary>
    public sealed class PhasedMigrationPlan
    {
        /// <summary>The ordered migration phases.</summary>
        public List<MigrationPlanPhase> Phases { get; set; } = new();

        /// <summary>Overall readiness verdict.</summary>
        public MigrationReadiness OverallReadiness { get; set; } = MigrationReadiness.Unknown;

        /// <summary>The specific signals that drove <see cref="OverallReadiness"/>.</summary>
        public List<string> ReadinessFactors { get; set; } = new();

        /// <summary>
        /// Estimated elapsed (wall-clock) preparation time before cutover: the initial bulk load plus the
        /// incremental-sync catch-up and minimum stabilization. <c>null</c> when an upstream estimate is
        /// unavailable. This is NOT business downtime — see <see cref="EstimatedBusinessDowntime"/>.
        /// </summary>
        public TimeSpan? EstimatedElapsedPreparationDuration { get; set; }

        /// <summary>
        /// Estimated business downtime during cutover only (the maintenance window). <c>null</c> when the
        /// cutover window cannot be bounded until pre-cutover catch-up is achieved; in that case
        /// <see cref="MinimumBusinessDowntime"/> still gives the known floor.
        /// </summary>
        public TimeSpan? EstimatedBusinessDowntime { get; set; }

        /// <summary>The known lower bound on business downtime (cutover fixed overhead with safety buffer).</summary>
        public TimeSpan MinimumBusinessDowntime { get; set; }

        /// <summary>Aggregated key risks across the plan, most significant first.</summary>
        public List<string> KeyRisks { get; set; } = new();

        /// <summary>
        /// Consistency diagnostics surfaced when upstream analyses are incomplete or appear to disagree, so
        /// the plan cannot be silently misread.
        /// </summary>
        public List<string> PlanWarnings { get; set; } = new();

        /// <summary>The explicit assumptions behind the plan.</summary>
        public List<string> Assumptions { get; set; } = new();

        /// <summary>Additional informational notes.</summary>
        public List<string> Notes { get; set; } = new();
    }
}
