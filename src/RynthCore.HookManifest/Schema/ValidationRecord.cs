namespace RynthCore.HookManifest.Schema;

public sealed record ValidationRecord(string RuleId, bool Passed, string Detail);
