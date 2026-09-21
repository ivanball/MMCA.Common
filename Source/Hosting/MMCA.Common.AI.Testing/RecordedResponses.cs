using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Testing;

/// <summary>
/// Reads and writes a recorded <see cref="ChatResponse"/>, the unit a golden corpus is made of.
/// </summary>
/// <remarks>
/// A corpus is stored in the ABSTRACTION's shape (<see cref="ChatResponse"/> through
/// <see cref="AIJsonUtilities.DefaultOptions"/>, the serializer options Microsoft.Extensions.AI
/// ships for its own types) and never in a vendor wire format. That is the whole reason this type
/// exists rather than a bare <see cref="JsonSerializer"/> call: a provider swap must not invalidate
/// a corpus. Answers recorded against one provider are exactly the answers the code under test sees
/// after the provider changes, because the code under test only ever sees
/// <see cref="ChatResponse"/>. Recording a provider's own JSON envelope instead would tie every
/// recorded answer to the adapter that produced it, and swapping providers would mean re-recording
/// the corpus and losing the regression history it exists to hold.
/// </remarks>
public static class RecordedResponses
{
    /// <summary>
    /// The abstraction's own serializer options, pretty-printed so a recorded answer stays readable
    /// in review and a re-recording produces a reviewable diff rather than one long line.
    /// </summary>
    private static readonly JsonSerializerOptions Options =
        new(AIJsonUtilities.DefaultOptions) { WriteIndented = true };

    /// <summary>Reads a recorded response from disk.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The recorded response.</returns>
    public static ChatResponse Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No recorded response at '{path}'. Check the corpus is copied to the output directory.",
                path);
        }

        return Deserialize(File.ReadAllText(path));
    }

    /// <summary>Writes a response to disk in the recorded shape, creating the folder if needed.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="response">The response to record.</param>
    public static void Write(string path, ChatResponse response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(response);

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, Serialize(response));
    }

    /// <summary>Serializes a response to the recorded shape.</summary>
    /// <param name="response">The response to serialize.</param>
    /// <returns>The recorded JSON.</returns>
    public static string Serialize(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return JsonSerializer.Serialize(response, Options);
    }

    /// <summary>Deserializes a recorded response.</summary>
    /// <param name="json">The recorded JSON.</param>
    /// <returns>The recorded response.</returns>
    public static ChatResponse Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        return JsonSerializer.Deserialize<ChatResponse>(json, Options)
            ?? throw new InvalidOperationException("The recorded JSON did not deserialize to a ChatResponse.");
    }
}
