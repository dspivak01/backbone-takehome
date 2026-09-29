using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Reporting;

/// <summary>
/// Finds the document a source such as "BH-D005 L10" points to, for the source command and the
/// review page alike. A source names the identifier printed in a document, and two documents can
/// carry the same one, for example a document and its revision. Every source the system writes
/// comes from a saved assertion, and assertions carry the document's hash as well as its printed
/// identifier, so the source is resolved through the assertion it was written from. When more
/// than one document is still possible, all of them are returned and the result says so.
/// </summary>
public static class SourceLookup
{
    /// <summary>
    /// A document can also be named by the start of its hash, as sources do for a document with no
    /// printed identifier. Anything shorter than this matches too many documents to mean one.
    /// </summary>
    public const int ShortestHashPrefix = 8;

    public sealed record Found(StoredDocument Document, string Name, string[] Lines, List<Assertion> Citing);

    public sealed class Result
    {
        public string Identifier { get; init; } = "";

        /// <summary>One document, or every document the source could mean when it cannot be told which.</summary>
        public List<Found> Documents { get; init; } = [];

        /// <summary>How many documents in the database carry the identifier.</summary>
        public int Carrying { get; init; }

        public bool Shared => Documents.Count > 1;

        /// <summary>A plain sentence saying the identifier is shared, when it is.</summary>
        public string? Note => Carrying < 2 ? null
            : Shared
                ? $"{Carrying} documents in this database carry the identifier \"{Identifier}\", and this source cannot be tied to one of them. Each is shown below with its file name."
                : $"{Carrying} documents in this database carry the identifier \"{Identifier}\". These lines are from {Documents[0].Document.Path}, the one the source was written from.";
    }

    /// <summary>
    /// The document or documents a source points to, or null when no document carries the
    /// identifier. A patient key, when given, narrows a shared identifier to that patient's documents.
    /// </summary>
    public static Result? Find(Database database, string identifier, int from, int to, string? patientKey)
    {
        var id = identifier.Trim();
        var documents = database.ListDocuments();
        var carrying = documents.Where(d => string.Equals(d.PrintedId, id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (carrying.Count == 0 && id.Length >= ShortestHashPrefix)
            carrying = documents.Where(d => d.DocHash.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (carrying.Count == 0) return null;

        var assertions = database.GetAssertions();
        var chosen = carrying;
        if (carrying.Count > 1)
        {
            // The source was written from an assertion with exactly these lines. If only one of the
            // documents holds such an assertion, that is the document. Failing that, a patient's
            // own documents are the only ones a source on that patient's record can mean.
            var hashes = carrying.Select(d => d.DocHash).ToHashSet();
            var pool = assertions.Where(a => hashes.Contains(a.DocHash) && (patientKey is null || a.PatientKey == patientKey)).ToList();
            var cited = pool.Where(a => a.LineStart == from && a.LineEnd == to).Select(a => a.DocHash).Distinct().ToList();
            var patients = patientKey is null ? [] : pool.Select(a => a.DocHash).Distinct().ToList();
            var one = cited.Count == 1 ? cited[0] : patients.Count == 1 ? patients[0] : null;
            if (one is not null) chosen = carrying.Where(d => d.DocHash == one).ToList();
        }

        return new Result
        {
            Identifier = id,
            Carrying = carrying.Count,
            Documents = chosen.Select(d => new Found(
                d, d.PrintedId ?? d.DocHash[..Math.Min(12, d.DocHash.Length)], d.Text.Split('\n'),
                assertions.Where(a => a.DocHash == d.DocHash && a.LineStart <= to && a.LineEnd >= from).ToList())).ToList(),
        };
    }
}
