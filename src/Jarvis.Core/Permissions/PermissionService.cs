using System.Text.Json.Serialization;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Permissions;

[JsonConverter(typeof(JsonStringEnumConverter<PermissionOutcome>))]
public enum PermissionOutcome { Allow, RequireApproval, Deny }

public sealed record PermissionDecision(PermissionOutcome Outcome, RiskLevel Risk, string Reason);

/// <summary>
/// Decides whether a tool call may run. The rules, in order:
/// 1. A tool the user blocked never runs; neither does screen capture when privacy settings forbid it.
/// 2. Critical actions always require explicit approval; no setting can bypass this.
/// 3. A per-tool "Allow" or "Ask" override applies to safe/sensitive actions.
/// 4. Safe actions run; sensitive actions ask unless the user enabled auto-approve.
/// </summary>
public sealed class PermissionService
{
    public PermissionDecision Evaluate(ToolDefinition tool, RiskAssessment assessment, JarvisSettings settings)
    {
        var policy = settings.Permissions.ToolOverrides.GetValueOrDefault(tool.Name, ToolPolicy.Default);
        // The per-call assessment is authoritative: a shell tool is "sensitive" in general, but
        // "git status" is safe and "Remove-Item -Recurse" is critical.
        var risk = assessment.Level;

        if (policy == ToolPolicy.Block)
            return new(PermissionOutcome.Deny, risk, "This tool is blocked in your permission settings.");
        if (tool.CapturesScreen && !settings.Privacy.AllowScreenCapture)
            return new(PermissionOutcome.Deny, risk, "Screen capture is turned off in Settings → Privacy.");
        if (tool.UsesCamera && !settings.Privacy.AllowCamera)
            return new(PermissionOutcome.Deny, risk, "The camera is off in Settings → Privacy.");

        if (risk == RiskLevel.Critical)
            return new(PermissionOutcome.RequireApproval, risk, assessment.Reason ?? "Critical actions always need your approval.");

        switch (policy)
        {
            case ToolPolicy.Allow:
                return new(PermissionOutcome.Allow, risk, "Allowed by your permission settings.");
            case ToolPolicy.Ask:
                return new(PermissionOutcome.RequireApproval, risk, "You asked to approve this tool every time.");
        }

        if (risk == RiskLevel.Safe)
            return new(PermissionOutcome.Allow, risk, "Safe action.");

        return settings.Permissions.AutoApproveSensitive
            ? new(PermissionOutcome.Allow, risk, "Sensitive action auto-approved by your settings.")
            : new(PermissionOutcome.RequireApproval, risk, assessment.Reason ?? "Sensitive action.");
    }
}
