namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Host-owned approval boundary for resuming a task from CODE RED.
/// The host authenticates the human and enforces its approval policy; Foundry supplies
/// the exact active incident and task snapshots so the host can bind that decision.
/// </summary>
public interface ICodeRedRecoveryApprovalGate
{
    /// <summary>
    /// Returns only when the host has an explicit, current human approval for this exact
    /// incident/task state. Implementations must throw when approval is absent or stale.
    /// This callback is invoked outside Foundry journal/coordinator locks.
    /// </summary>
    void DemandApproval(CodeRedIncident incident, WorkTaskSnapshot task, DateTimeOffset requestedAt);
}
