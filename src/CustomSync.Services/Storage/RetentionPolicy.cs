using CustomSync.Data.Entities;

namespace CustomSync.Services.Storage;

public static class RetentionActions
{
    public const string ArchiveThenDelete = RetentionPolicyEntity.ActionArchiveThenDelete;
    public const string DeleteOnly        = RetentionPolicyEntity.ActionDeleteOnly;

    /// <summary>
    /// Himoya siyosati — boshqa hamma narsadan ustun turadi. Muhim
    /// chatlarni tasodifan o'chirib yubormaslik uchun.
    /// </summary>
    public const string NeverDelete       = RetentionPolicyEntity.ActionNeverDelete;

    public const string None              = "none";

    public static bool IsValid(string? action) =>
        action is ArchiveThenDelete or DeleteOnly or NeverDelete;
}

public sealed record RetentionCandidate(
    string Kind, string PeerHash, bool HasMedia, DateTime ReceivedAt);

public sealed record RetentionDecision(
    bool ShouldAct, string Action, string? TargetId,
    string? PolicyId, string Reason);

public static class RetentionEvaluator
{
    private static bool MatchesScope(RetentionPolicyEntity policy, RetentionCandidate candidate)
    {
        if (!policy.Enabled) return false;

        if (!string.IsNullOrWhiteSpace(policy.Kind) &&
            !string.Equals(policy.Kind, candidate.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(policy.PeerHash) &&
            !string.Equals(policy.PeerHash, candidate.PeerHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (policy.MediaOnly && !candidate.HasMedia)
        {
            return false;
        }

        return true;
    }

    private static bool IsOlderThan(DateTime receivedAt, int olderThanDays, DateTime now)
    {
        if (olderThanDays <= 0) return true;
        return receivedAt <= now.AddDays(-olderThanDays);
    }

    private static int GetSpecificity(RetentionPolicyEntity policy)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(policy.PeerHash)) score += 4;
        if (!string.IsNullOrWhiteSpace(policy.Kind)) score += 2;
        if (policy.MediaOnly) score += 1;
        return score;
    }

    /// <summary>
    /// Sof baholash funksiyasi: hech qanday I/O, ma'lumotlar bazasi yoki soat
    /// qaramligisiz toza qaror qabul qiladi (qoida K4).
    /// </summary>
    public static RetentionDecision Evaluate(
        RetentionCandidate candidate,
        IReadOnlyList<RetentionPolicyEntity> policies,
        IReadOnlyDictionary<string, int> settingsDays,
        int minDays,
        DateTime now)
    {
        // 1. Any matching never_delete policy -> ShouldAct = false
        // never_delete boshqa hamma siyosatlardan ustun turadi (ustuvorlikdan qat'i nazar).
        foreach (var policy in policies)
        {
            // DIQQAT: bu yerda ATAYLAB yosh (OlderThanDays) tekshirilmaydi.
            // never_delete -- himoya, va himoya QAMROV bo'yicha ishlaydi,
            // yosh bo'yicha emas. Yoshga bog'lansa quyidagi holat yuzaga
            // keladi: operator "peer_vip ni himoyala, 365 kundan eskisini"
            // deb yozadi, keyinroq kimdir "90 kundan eskisini o'chir" degan
            // keng siyosat qo'shadi -- va 90-365 kun oralig'idagi yozuvlar
            // himoyasiz qolib jimgina o'chadi. Bu aynan never_delete oldini
            // olishi kerak bo'lgan holat (plan 04 Task 2: "muhim chat
            // keyinroq qo'shilgan keng qoidaga ilinib qolmasin").
            if (string.Equals(policy.Action, RetentionActions.NeverDelete, StringComparison.OrdinalIgnoreCase) &&
                MatchesScope(policy, candidate))
            {
                return new RetentionDecision(
                    ShouldAct: false,
                    Action: RetentionActions.NeverDelete,
                    TargetId: null,
                    PolicyId: policy.PolicyId,
                    Reason: $"NeverDelete himoya siyosati '{policy.Name}' ({policy.PolicyId}) bo'yicha saqlanadi");
            }
        }

        // 2. Highest-priority matching valid policy -> its Action applies
        var validMatches = new List<RetentionPolicyEntity>();
        var invalidMatches = new List<(RetentionPolicyEntity Policy, string Error)>();

        foreach (var policy in policies)
        {
            if (string.Equals(policy.Action, RetentionActions.NeverDelete, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!MatchesScope(policy, candidate))
            {
                continue;
            }

            var error = policy.GetValidationError(minDays);
            if (error != null)
            {
                if (IsOlderThan(candidate.ReceivedAt, policy.OlderThanDays, now))
                {
                    invalidMatches.Add((policy, error));
                }
                continue;
            }

            if (IsOlderThan(candidate.ReceivedAt, policy.OlderThanDays, now))
            {
                validMatches.Add(policy);
            }
        }

        if (validMatches.Count > 0)
        {
            // Deterministik saralash:
            // 1. Priority (kamayish tartibida)
            // 2. Specificity (aniqroq moslik: PeerHash > Kind > MediaOnly)
            // 3. PolicyId (ordinal tartibda)
            validMatches.Sort((a, b) =>
            {
                var cmp = b.Priority.CompareTo(a.Priority);
                if (cmp != 0) return cmp;

                cmp = GetSpecificity(b).CompareTo(GetSpecificity(a));
                if (cmp != 0) return cmp;

                return string.CompareOrdinal(a.PolicyId, b.PolicyId);
            });

            var winner = validMatches[0];
            var isArchive = string.Equals(winner.Action, RetentionActions.ArchiveThenDelete, StringComparison.OrdinalIgnoreCase);

            return new RetentionDecision(
                ShouldAct: true,
                Action: winner.Action,
                TargetId: isArchive ? winner.TargetId : null,
                PolicyId: winner.PolicyId,
                Reason: $"Siyosat '{winner.Name}' ({winner.PolicyId}) bo'yicha amal qo'llandi: {winner.Action}");
        }

        // Agar birorta ham valid siyosat mos kelmagan, lekin yaroqsiz siyosat mavjud bo'lsa
        if (invalidMatches.Count > 0)
        {
            invalidMatches.Sort((a, b) =>
            {
                var cmp = b.Policy.Priority.CompareTo(a.Policy.Priority);
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(a.Policy.PolicyId, b.Policy.PolicyId);
            });

            var (badPolicy, error) = invalidMatches[0];
            return new RetentionDecision(
                ShouldAct: false,
                Action: badPolicy.Action,
                TargetId: null,
                PolicyId: badPolicy.PolicyId,
                Reason: $"Siyosat '{badPolicy.Name}' ({badPolicy.PolicyId}) yaroqsiz (invalid) deb topildi: {error}");
        }

        // 3. Implicit policy from settings (§1) -> delete_only
        var settingKey = $"retention.{candidate.Kind}_days";
        int settingDays = 0;
        if (settingsDays.TryGetValue(settingKey, out var d1))
        {
            settingDays = d1;
        }
        else if (settingsDays.TryGetValue(candidate.Kind, out var d2))
        {
            settingDays = d2;
        }

        if (settingDays > 0)
        {
            if (settingDays < minDays)
            {
                return new RetentionDecision(
                    ShouldAct: false,
                    Action: RetentionActions.DeleteOnly,
                    TargetId: null,
                    PolicyId: settingKey,
                    Reason: $"'{settingKey}' sozlamasi ({settingDays} kun) minimal chegara ({minDays} kun) dan past, rad etildi");
            }

            if (IsOlderThan(candidate.ReceivedAt, settingDays, now))
            {
                return new RetentionDecision(
                    ShouldAct: true,
                    Action: RetentionActions.DeleteOnly,
                    TargetId: null,
                    PolicyId: settingKey,
                    Reason: $"Standart '{settingKey}' ({settingDays} kun) sozlamasi bo'yicha faqat o'chirish qo'llandi");
            }

            return new RetentionDecision(
                ShouldAct: false,
                Action: RetentionActions.None,
                TargetId: null,
                PolicyId: null,
                Reason: $"Yozuv muddati standart retention oynasiga ({settingDays} kun) to'g'ri keladi, o'chirilmaydi");
        }

        // 4. Nothing matches -> ShouldAct = false
        return new RetentionDecision(
            ShouldAct: false,
            Action: RetentionActions.None,
            TargetId: null,
            PolicyId: null,
            Reason: "Mos keladigan retention siyosati yoki sozlamasi mavjud emas");
    }
}

public static class RetentionPolicy
{
    public static string? GetValidationError(RetentionPolicyEntity policy, int minDays) =>
        policy.GetValidationError(minDays);

    public static RetentionDecision Evaluate(
        RetentionCandidate candidate,
        IReadOnlyList<RetentionPolicyEntity> policies,
        IReadOnlyDictionary<string, int> settingsDays,
        int minDays,
        DateTime now) => RetentionEvaluator.Evaluate(candidate, policies, settingsDays, minDays, now);
}
