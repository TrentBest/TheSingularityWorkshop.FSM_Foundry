using System.Text;
using System.Globalization;

namespace TheSingularityWorkshop.Foundry;

/// <summary>Legacy recovery action retained only for replaying existing v1 task journals.</summary>
public enum CodeRedRecoveryAction
{
    ResumeInvestigation = 1
}

/// <summary>
/// Legacy v1 signed-receipt audit payload retained only for backward-compatible journal
/// deserialization and structural history validation. Current CODE RED recovery does not
/// accept or verify this receipt; human approval is enforced by the host approval gate.
/// </summary>
public sealed record CodeRedRecoveryApprovalReceipt(
    int SchemaVersion,
    string ReceiptId,
    string Issuer,
    string Principal,
    string TaskId,
    string IncidentId,
    long TaskEventSequence,
    long IncidentSequence,
    CodeRedRecoveryAction Action,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Signature)
{
    public const int CurrentSchemaVersion = 1;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);

    public CodeRedRecoveryApprovalReceipt Validate()
    {
        ValidateText(ReceiptId, nameof(ReceiptId), 128);
        ValidateText(Issuer, nameof(Issuer), 128);
        ValidateText(Principal, nameof(Principal), 128);
        ValidateText(TaskId, nameof(TaskId), 128);
        ValidateText(IncidentId, nameof(IncidentId), 128);
        ValidateText(Signature, nameof(Signature), 8192);
        if (SchemaVersion != CurrentSchemaVersion ||
            TaskEventSequence < 0 || IncidentSequence <= 0 ||
            Action != CodeRedRecoveryAction.ResumeInvestigation ||
            IssuedAt == default || ExpiresAt <= IssuedAt ||
            ExpiresAt - IssuedAt > MaximumLifetime)
            throw new ArgumentException("Legacy recovery receipt schema, scope, action, or validity window is invalid.");
        return this;
    }

    /// <summary>Canonical v1 signing payload retained for audit compatibility only.</summary>
    public byte[] GetSigningPayload()
    {
        Validate();
        var fields = new[]
        {
            "foundry.code-red-recovery-receipt.v1",
            SchemaVersion.ToString(CultureInfo.InvariantCulture),
            ReceiptId, Issuer, Principal, TaskId, IncidentId,
            TaskEventSequence.ToString(CultureInfo.InvariantCulture),
            IncidentSequence.ToString(CultureInfo.InvariantCulture),
            ((int)Action).ToString(CultureInfo.InvariantCulture),
            IssuedAt.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            ExpiresAt.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)
        };
        var payload = new StringBuilder();
        foreach (var field in fields)
            payload.Append(Encoding.UTF8.GetByteCount(field).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(field).Append('\n');
        return Encoding.UTF8.GetBytes(payload.ToString());
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException($"Legacy receipt field must contain 1 to {maximumLength} non-control characters.",
                parameterName);
    }
}
