using System.Text;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Ingest;

public enum RegistrationOutcome
{
    /// <summary>Content not seen before. It needs extraction.</summary>
    New,

    /// <summary>Content already registered. The copy is recorded and nothing else happens.</summary>
    Duplicate,
}

public sealed record Registration(RegistrationOutcome Outcome, StoredDocument Document, bool ByteIdentical);

/// <summary>
/// Gives every document an identity based on its content, so the same content is never
/// processed twice no matter what the file is called or how many times it arrives.
/// </summary>
public sealed class DocumentRegistry(Database database)
{
    public Registration Register(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var rawHash = Hashing.Sha256(bytes);
        var text = Normalise(Encoding.UTF8.GetString(bytes));
        var docHash = Hashing.Sha256(text);

        var existing = database.FindDocument(docHash);
        if (existing is not null)
        {
            database.RecordCopy(docHash, rawHash, path);
            return new Registration(RegistrationOutcome.Duplicate, existing, existing.RawHash == rawHash);
        }

        var document = new StoredDocument(docHash, rawHash, null, path, text, text.Split('\n').Length, bytes.LongLength);
        database.InsertDocument(document);
        database.RecordCopy(docHash, rawHash, path);
        return new Registration(RegistrationOutcome.New, document, true);
    }

    /// <summary>How much of a file is looked at to decide whether it is text, as git decides it.</summary>
    public const int TextSniffBytes = 8000;

    /// <summary>
    /// Whether a file holds text. A file with a zero byte near its start, as pictures, PDFs and word
    /// processor files have, is not text. Nothing is written to the file.
    /// </summary>
    public static bool IsText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[TextSniffBytes];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return IsText(buffer.AsSpan(0, read));
    }

    /// <summary>Whether bytes already read hold text: no zero byte anywhere in them.</summary>
    public static bool IsText(ReadOnlySpan<byte> bytes) => !bytes.Contains((byte)0);

    /// <summary>
    /// Removes differences that do not change what a document says: a byte-order mark, Windows line
    /// endings, trailing spaces, and blank lines at the end. Line numbers are unchanged.
    /// </summary>
    public static string Normalise(string text)
    {
        text = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n').Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    /// <summary>The document with a line number in front of each line, as sent to the model.</summary>
    public static string WithLineNumbers(string text)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length; i++) builder.Append(i + 1).Append('\t').Append(lines[i]).Append('\n');
        return builder.ToString();
    }
}
