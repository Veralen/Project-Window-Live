using System.Globalization;
using System.Text;

namespace ScreenTranslator.Translation;

/// <summary>
/// Splits a text block into individual sentences before it is handed to a Marian
/// MT model. opus-mt / NLLB are trained on <em>sentence</em> pairs, so feeding a
/// whole multi-sentence paragraph in one shot badly degrades quality and triggers
/// runaway repetition loops. Segmenting first — translate each sentence, join the
/// English outputs with a single space — is the single biggest accuracy win for
/// dense CJK prose.
///
/// <para>Rules:
/// <list type="bullet">
/// <item>Fullwidth CJK terminators (。！？；…) always end a sentence.</item>
/// <item>Halfwidth terminators (. ! ? ;) end a sentence only when followed by
///   whitespace or end-of-text — this avoids splitting decimals (3.14),
///   ellipses (...), and abbreviations.</item>
/// <item>The terminator (and any run of trailing terminators / closing quotes /
///   brackets, e.g. 。" ) stays attached to the sentence it ends.</item>
/// <item>Input with no terminator (short UI text, menu fragments) passes through
///   as a single segment, unchanged.</item>
/// </list></para>
/// </summary>
public static class SentenceSegmenter
{
    // Fullwidth / CJK sentence terminators — unambiguous, always split.
    private const string FullwidthTerminators = "。！？；…｡"; // 。 ！ ？ ； … ｡

    // Halfwidth terminators — split only when followed by whitespace / EOL.
    private const string HalfwidthTerminators = ".!?;";

    // Closing punctuation that should hug the sentence it trails (quotes, brackets).
    private const string ClosingChars = "\"'”’)）]}」』】》〉｣";

    /// <summary>
    /// Splits <paramref name="text"/> into sentences. Returns trimmed, non-empty
    /// segments in order. A text with no sentence terminator yields exactly one
    /// segment (the trimmed input).
    /// </summary>
    public static IReadOnlyList<string> Split(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        int n = text.Length;
        int start = 0;
        int i = 0;

        while (i < n)
        {
            char c = text[i];
            bool isTerminator;

            if (FullwidthTerminators.IndexOf(c) >= 0)
            {
                isTerminator = true;
            }
            else if (HalfwidthTerminators.IndexOf(c) >= 0)
            {
                char next = i + 1 < n ? text[i + 1] : '\0';
                isTerminator = next == '\0' || char.IsWhiteSpace(next);
            }
            else
            {
                isTerminator = false;
            }

            if (isTerminator)
            {
                // Absorb a run of further terminators and closing quotes/brackets so
                // that e.g.  …"  or  ?!  stays with the sentence it ends.
                int j = i + 1;
                while (j < n &&
                       (FullwidthTerminators.IndexOf(text[j]) >= 0 ||
                        HalfwidthTerminators.IndexOf(text[j]) >= 0 ||
                        ClosingChars.IndexOf(text[j]) >= 0))
                {
                    j++;
                }

                string segment = text.Substring(start, j - start).Trim();
                if (segment.Length > 0) result.Add(segment);
                start = j;
                i = j;
            }
            else
            {
                i++;
            }
        }

        if (start < n)
        {
            string tail = text.Substring(start, n - start).Trim();
            if (tail.Length > 0) result.Add(tail);
        }

        // No terminator found anywhere: return the whole (trimmed) block.
        if (result.Count == 0)
        {
            string whole = text.Trim();
            if (whole.Length > 0) result.Add(whole);
        }

        return result;
    }

    // Comma-class separators used only as a fallback when a single "sentence" is
    // still longer than the model's comfortable source-token budget.
    private const string ClauseSeparators = "，、,；;：:"; // ， 、 , ； ; ： :

    /// <summary>
    /// Fallback splitter for an over-long sentence: breaks it at comma-class
    /// punctuation (，、,；：) keeping each delimiter attached to its clause. Used
    /// by the engine only when a segment exceeds the per-sentence source-token cap;
    /// the engine then regroups clauses to stay under the cap.
    /// </summary>
    public static IReadOnlyList<string> SplitClauses(string sentence)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(sentence)) return result;

        var sb = new StringBuilder();
        foreach (char c in sentence)
        {
            sb.Append(c);
            if (ClauseSeparators.IndexOf(c) >= 0)
            {
                string clause = sb.ToString().Trim();
                if (clause.Length > 0) result.Add(clause);
                sb.Clear();
            }
        }
        if (sb.Length > 0)
        {
            string clause = sb.ToString().Trim();
            if (clause.Length > 0) result.Add(clause);
        }

        if (result.Count == 0) result.Add(sentence.Trim());
        return result;
    }
}
