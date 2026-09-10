using CustomSync.Data.Entities;
using CustomSync.Services.Storage;
using Xunit;

namespace CustomSync.Tests;

public class RetentionPolicyTests
{
    private readonly DateTime _now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Test1_No_policies_all_settings_zero_nothing_is_deleted()
    {
        var candidate = new RetentionCandidate(
            Kind: "activity",
            PeerHash: "peer_1",
            HasMedia: false,
            ReceivedAt: _now.AddDays(-100));

        var policies = Array.Empty<RetentionPolicyEntity>();
        var settingsDays = new Dictionary<string, int>
        {
            ["retention.activity_days"] = 0,
            ["retention.deleted_days"] = 0
        };

        var decision = RetentionEvaluator.Evaluate(candidate, policies, settingsDays, minDays: 30, now: _now);

        Assert.False(decision.ShouldAct);
        Assert.Equal(RetentionActions.None, decision.Action);
        Assert.Null(decision.PolicyId);
    }

    [Fact]
    public void Test2_Never_delete_wins_over_higher_priority_archive_then_delete()
    {
        var candidate = new RetentionCandidate(
            Kind: "activity",
            PeerHash: "peer_vip",
            HasMedia: true,
            ReceivedAt: _now.AddDays(-60));

        var archivePolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_archive",
            Name = "Archive older than 30",
            Enabled = true,
            Kind = "activity",
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "target_s3",
            Priority = 100,
            UpdatedAt = _now
        };

        var neverDeletePolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_vip",
            Name = "Protect VIP",
            Enabled = true,
            PeerHash = "peer_vip",
            OlderThanDays = 0,
            Action = RetentionActions.NeverDelete,
            Priority = 10, // Pastroq ustuvorlik
            UpdatedAt = _now
        };

        var policies = new[] { archivePolicy, neverDeletePolicy };
        var settings = new Dictionary<string, int>();

        var decision = RetentionEvaluator.Evaluate(candidate, policies, settings, minDays: 30, now: _now);

        Assert.False(decision.ShouldAct);
        Assert.Equal(RetentionActions.NeverDelete, decision.Action);
        Assert.Equal("pol_vip", decision.PolicyId);
    }

    [Fact]
    public void Test3_Highest_priority_wins_among_ordinary_matching_policies()
    {
        var candidate = new RetentionCandidate(
            Kind: "activity",
            PeerHash: "peer_1",
            HasMedia: false,
            ReceivedAt: _now.AddDays(-50));

        var policyLow = new RetentionPolicyEntity
        {
            PolicyId = "pol_low",
            Name = "Low priority delete",
            Enabled = true,
            Kind = "activity",
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 20,
            UpdatedAt = _now
        };

        var policyHigh = new RetentionPolicyEntity
        {
            PolicyId = "pol_high",
            Name = "High priority archive",
            Enabled = true,
            Kind = "activity",
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "s3_archive",
            Priority = 80,
            UpdatedAt = _now
        };

        var policies = new[] { policyLow, policyHigh };
        var settings = new Dictionary<string, int>();

        var decision = RetentionEvaluator.Evaluate(candidate, policies, settings, minDays: 30, now: _now);

        Assert.True(decision.ShouldAct);
        Assert.Equal("pol_high", decision.PolicyId);
        Assert.Equal(RetentionActions.ArchiveThenDelete, decision.Action);
        Assert.Equal("s3_archive", decision.TargetId);
    }

    [Fact]
    public void Test4_Record_younger_than_OlderThanDays_is_not_selected()
    {
        var candidate = new RetentionCandidate(
            Kind: "activity",
            PeerHash: "peer_1",
            HasMedia: false,
            ReceivedAt: _now.AddDays(-20)); // 20 kunlik (60 kundan yosh)

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_60",
            Name = "Delete older than 60",
            Enabled = true,
            Kind = "activity",
            OlderThanDays = 60,
            Action = RetentionActions.DeleteOnly,
            Priority = 50,
            UpdatedAt = _now
        };

        var policies = new[] { policy };
        var settings = new Dictionary<string, int>();

        var decision = RetentionEvaluator.Evaluate(candidate, policies, settings, minDays: 30, now: _now);

        Assert.False(decision.ShouldAct);
    }

    [Fact]
    public void Test5_Policy_with_OlderThanDays_below_floor_is_refused_and_does_not_clamp()
    {
        // 40 kunlik yozuv. Operator 15 kun deb yozgan, minimal chegara (floor) esa 30 kun.
        // Agar yashirin clamp (15 -> 30) qilinganda, 40 kunlik yozuv o'chib ketardi.
        // Rad etilgani (refused) uchun hech narsa o'chirilmasligi shart.
        var candidate = new RetentionCandidate(
            Kind: "activity",
            PeerHash: "peer_1",
            HasMedia: false,
            ReceivedAt: _now.AddDays(-40));

        var belowFloorPolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_invalid_floor",
            Name = "Aggressive 15 days",
            Enabled = true,
            Kind = "activity",
            OlderThanDays = 15, // < minDays (30)
            Action = RetentionActions.DeleteOnly,
            Priority = 50,
            UpdatedAt = _now
        };

        var policies = new[] { belowFloorPolicy };
        var settings = new Dictionary<string, int>();

        var decision = RetentionEvaluator.Evaluate(candidate, policies, settings, minDays: 30, now: _now);

        Assert.False(decision.ShouldAct);
        Assert.Equal("pol_invalid_floor", decision.PolicyId);
        Assert.Contains("invalid", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test6_Implicit_settings_policy_applies_when_no_stored_policy_and_loses_to_stored_policy()
    {
        var candidate = new RetentionCandidate(
            Kind: "activity",
            PeerHash: "peer_1",
            HasMedia: false,
            ReceivedAt: _now.AddDays(-100));

        var settings = new Dictionary<string, int>
        {
            ["retention.activity_days"] = 90
        };

        // 1. Saqlangan siyosat bo'lmaganda implicit setting qo'llanadi:
        var decisionImplicit = RetentionEvaluator.Evaluate(
            candidate, Array.Empty<RetentionPolicyEntity>(), settings, minDays: 30, now: _now);

        Assert.True(decisionImplicit.ShouldAct);
        Assert.Equal(RetentionActions.DeleteOnly, decisionImplicit.Action);

        // 2. Saqlangan siyosat mavjud bo'lganda, saqlangan siyosat g'olib bo'ladi:
        var storedPolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_stored",
            Name = "Archive older than 60",
            Enabled = true,
            Kind = "activity",
            OlderThanDays = 60,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "archive_target",
            Priority = 1,
            UpdatedAt = _now
        };

        var decisionStored = RetentionEvaluator.Evaluate(
            candidate, new[] { storedPolicy }, settings, minDays: 30, now: _now);

        Assert.True(decisionStored.ShouldAct);
        Assert.Equal("pol_stored", decisionStored.PolicyId);
        Assert.Equal(RetentionActions.ArchiveThenDelete, decisionStored.Action);
        Assert.Equal("archive_target", decisionStored.TargetId);
    }

    [Fact]
    public void Test7_Enabled_false_never_matches()
    {
        var candidate = new RetentionCandidate(
            Kind: "activity",
            PeerHash: "peer_1",
            HasMedia: false,
            ReceivedAt: _now.AddDays(-100));

        var disabledPolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_disabled",
            Name = "Disabled policy",
            Enabled = false,
            Kind = "activity",
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 100,
            UpdatedAt = _now
        };

        var policies = new[] { disabledPolicy };
        var settings = new Dictionary<string, int>();

        var decision = RetentionEvaluator.Evaluate(candidate, policies, settings, minDays: 30, now: _now);

        Assert.False(decision.ShouldAct);
    }

    [Fact]
    public void Test8_Unknown_action_string_does_not_delete()
    {
        var candidate = new RetentionCandidate(
            Kind: "activity",
            PeerHash: "peer_1",
            HasMedia: false,
            ReceivedAt: _now.AddDays(-100));

        var badActionPolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_bad_action",
            Name = "Bad action policy",
            Enabled = true,
            Kind = "activity",
            OlderThanDays = 30,
            Action = "destroy_immediately", // Noma'lum action
            Priority = 100,
            UpdatedAt = _now
        };

        var policies = new[] { badActionPolicy };
        var settings = new Dictionary<string, int>();

        var decision = RetentionEvaluator.Evaluate(candidate, policies, settings, minDays: 30, now: _now);

        Assert.False(decision.ShouldAct);
        Assert.Contains("invalid", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
