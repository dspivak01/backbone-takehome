using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Reconciliation;

/// <summary>
/// How much weight a statement carries as evidence of what happened.
/// Tier 1: signed clinical and attendance records, and signed corrections.
/// Tier 2: administrative records such as exports, desk logs and platform logs.
/// Tier 3: unsigned drafts and billing charges, which are never evidence of what happened.
/// A copy takes the tier of the record it copies, because the extractor describes the original.
/// </summary>
public static class Tiers
{
    private static readonly HashSet<string> ClinicalRecords =
        ["clinical_note", "attendance_record", "correction", "treatment_plan", "measure_record"];

    public static int Of(Assertion assertion)
    {
        if (assertion.RecordType is "draft_note" or "billing_charge") return 3;
        var signed = assertion.Signed is "yes" or "extract_of_signed";
        return signed && ClinicalRecords.Contains(assertion.RecordType) ? 1 : 2;
    }

    public static string Describe(int tier) => tier switch
    {
        1 => "tier 1 (signed record)",
        2 => "tier 2 (administrative record)",
        _ => "tier 3 (draft or billing, not evidence)",
    };
}
