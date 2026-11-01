namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Host-facing CODE RED recovery boundary. It asks the host-owned approval gate to
/// authorize the exact incident/task snapshots, then rechecks both snapshots before
/// persisting recovery. Human authentication and approval policy never run in Foundry.
/// </summary>
public sealed class PersistentCodeRedRecoveryCoordinator
{
    private readonly JsonlCodeRedIncidentStore _incidents;
    private readonly PersistentWorkTaskCoordinator _tasks;
    private readonly ICodeRedRecoveryApprovalGate _approvalGate;

    public PersistentCodeRedRecoveryCoordinator(JsonlCodeRedIncidentStore incidents,
        PersistentWorkTaskCoordinator tasks, ICodeRedRecoveryApprovalGate approvalGate)
    {
        _incidents = incidents ?? throw new ArgumentNullException(nameof(incidents));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _approvalGate = approvalGate ?? throw new ArgumentNullException(nameof(approvalGate));
    }

    public WorkTaskSnapshot RecoverFromCodeRed(string taskId, Guid leaseId, string incidentId,
        DateTimeOffset now, string reason)
    {
        if (now == default)
            throw new ArgumentException("Recovery time is required.", nameof(now));
        var incident = _incidents.Get(incidentId);
        if (incident.Status != CodeRedIncidentStatus.Active)
            throw new InvalidOperationException("Only the latest active incident can authorize CODE RED recovery.");
        var task = _tasks.GetSnapshot(taskId);

        // The host callback may consult a user approval service. Never hold Foundry locks
        // while invoking host code or waiting for a human decision.
        _approvalGate.DemandApproval(incident, task, now);

        return _incidents.ExecuteWithActiveIncident(incidentId, currentIncident =>
        {
            if (currentIncident.Sequence != incident.Sequence ||
                currentIncident.IncidentId != incident.IncidentId)
                throw new UnauthorizedAccessException("The CODE RED incident changed after host approval; obtain approval for the current incident.");
            return _tasks.RecoverFromCodeRed(taskId, leaseId, currentIncident,
                task.Events.Count, now, reason);
        });
    }
}
