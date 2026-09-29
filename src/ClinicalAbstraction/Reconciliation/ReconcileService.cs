using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Reconciliation;

/// <summary>
/// Rebuilds and saves the abstraction for the patients named, or for everyone. It reads saved
/// assertions only, so it can be re-run at any time without calling a model.
/// </summary>
public sealed class ReconcileService(Database database)
{
    public List<PatientAbstraction> Run(IEnumerable<string>? patientKeys = null)
    {
        var wanted = patientKeys?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejected = database.GetRejected().Where(r => r.Critical).GroupBy(r => r.DocHash).ToDictionary(g => g.Key, g => g.Count());
        var documents = database.ListDocuments().ToDictionary(d => d.DocHash);
        var unfinished = database.ListUnfinishedExtractions();
        var reconciler = new Reconciler();
        var results = new List<PatientAbstraction>();

        foreach (var patient in database.ListPatients())
        {
            if (wanted is not null && !wanted.Contains(patient.PatientKey)) continue;

            var assertions = database.GetAssertions(patient.PatientKey);
            var documentHashes = assertions.Select(a => a.DocHash).Distinct().ToList();
            var warnings = documentHashes
                .Where(rejected.ContainsKey)
                .Select(hash =>
                {
                    var name = documents.TryGetValue(hash, out var d) ? d.PrintedId ?? Path.GetFileName(d.Path) : hash[..12];
                    return $"Extraction incomplete for {name}: {rejected[hash]} assertion(s) about attendance, presence or the plan were rejected because their quotes were not found in the document. Totals that depend on this document may be understated.";
                })
                .Concat(unfinished.Where(u => u.PatientKey == patient.PatientKey).Select(u =>
                {
                    var name = documents.TryGetValue(u.DocHash, out var d) ? d.PrintedId ?? Path.GetFileName(d.Path) : u.DocHash[..12];
                    return $"Extraction {u.Status} for {name}. Nothing from this document is in the abstraction, so counts and minutes for this patient may be understated.";
                }))
                .Order(StringComparer.Ordinal)
                .ToList();

            var abstraction = reconciler.Reconcile(patient.PatientKey, patient.Mrn, patient.Name, patient.DateOfBirth, assertions, documentHashes.Count, warnings);

            // Weekly results are stored with the abstraction, so questions across all patients read rows instead of recomputing.
            var weeks = abstraction.Episode is { Start: { } start, End: { } end }
                ? WeeklyCalculator.Weeks(abstraction, start, end)
                : [];
            database.SaveAbstraction(abstraction, weeks.Select(w =>
                (w.WeekStart, w.DaysMin, w.DaysMax, w.MinutesMin, w.MinutesMax, w.Result, w.PartialWeek, Json.Write(w))));
            results.Add(abstraction);
        }

        return results;
    }
}
