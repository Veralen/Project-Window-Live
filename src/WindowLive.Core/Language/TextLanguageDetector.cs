using Lingua;

namespace WindowLive.Core.Language;

/// <summary>
/// On-device source-language detection for short game-chat lines, wrapping
/// SearchPioneer.Lingua's <see cref="LanguageDetector"/>. Used by the Tesseract
/// OCR path (Phase 2): Tesseract needs a language selected before it can OCR,
/// so this only runs to resolve "auto" for the *next* frame or for post-hoc
/// language selection — not to steer the OCR call that already ran (see
/// TesseractRecognizer's doc comment for the v1 auto→eng rule).
///
/// The detector is restricted to <see cref="LanguageCatalog.All"/>'s languages
/// (rather than Lingua's full ~75-language set) for two reasons: (1) matches
/// what the app can actually act on — a detected language with no catalog
/// entry is useless here — and (2) Lingua's own docs note that restricting the
/// language set measurably improves short-text accuracy (fewer confusable
/// candidates) and reduces the memory footprint of loaded n-gram models.
/// </summary>
public sealed class TextLanguageDetector
{
    /// <summary>
    /// Confidence floor for accepting a detection result. Lingua's
    /// <c>ComputeLanguageConfidenceValues</c> returns a probability per
    /// candidate language that sums to 1.0 across the restricted set; with as
    /// few as ~14 catalog languages, "top guess barely ahead of the pack" is a
    /// real failure mode for short chat lines (a handful of characters gives
    /// the n-gram model little to work with). 0.5 requires the top language to
    /// hold at least half the total probability mass — i.e. genuinely more
    /// likely than all other candidates combined — which in practice rejects
    /// the ambiguous short-text cases while still accepting clear-cut ones.
    /// </summary>
    private const double ConfidenceFloor = 0.5;

    private readonly Lazy<LanguageDetector> _detector;

    /// <summary>
    /// Constructs the (unbuilt) detector. Building the underlying
    /// <see cref="LanguageDetector"/> loads Lingua's n-gram language models
    /// from disk/embedded resources, which is not cheap — callers must invoke
    /// <see cref="WarmUpAsync"/> (or otherwise make the first <see cref="DetectCode"/>
    /// call) off the UI thread so this cost never blocks WPF's dispatcher.
    /// <see cref="Lazy{T}.ExecutionAndPublication"/> mode ensures concurrent
    /// first-callers (e.g. a warm-up task racing a real detection request)
    /// block on one build rather than each building their own detector.
    /// </summary>
    public TextLanguageDetector()
    {
        _detector = new Lazy<LanguageDetector>(BuildDetector, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Forces construction of the underlying <see cref="LanguageDetector"/> now,
    /// on whatever thread calls this. Intended to be awaited from a background
    /// task at app/game-mode startup so the first real <see cref="DetectCode"/>
    /// call (which may happen on a latency-sensitive path) doesn't pay the
    /// model-loading cost.
    /// </summary>
    public Task WarmUpAsync(CancellationToken ct = default) =>
        Task.Run(() => _ = _detector.Value, ct);

    /// <summary>
    /// Detects the language of <paramref name="text"/> and returns the matching
    /// catalog <see cref="LanguageInfo.Code"/>, or null when: the text is
    /// null/whitespace, Lingua could not identify a language
    /// (<see cref="Lingua.Language.Unknown"/>), the top candidate's confidence
    /// is below <see cref="ConfidenceFloor"/>, or the detected Lingua language
    /// has no <see cref="LanguageCatalog"/> entry (shouldn't happen given the
    /// detector is built from exactly the catalog's languages, but guarded
    /// defensively since this feeds user-facing language selection).
    /// </summary>
    public string? DetectCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        IDictionary<Lingua.Language, double> confidences = _detector.Value.ComputeLanguageConfidenceValues(text);
        if (confidences.Count == 0)
            return null;

        // ComputeLanguageConfidenceValues documents its result as sorted by
        // confidence value descending, so the first entry is the top guess.
        KeyValuePair<Lingua.Language, double> top = confidences.First();
        if (top.Key == Lingua.Language.Unknown || top.Value < ConfidenceFloor)
            return null;

        return LanguageCatalog.ByLinguaName(top.Key.ToString())?.Code;
    }

    private static LanguageDetector BuildDetector()
    {
        Lingua.Language[] languages = LanguageCatalog.All
            .Select(l => l.LinguaName)
            .Distinct(StringComparer.Ordinal)
            .Select(name => Enum.TryParse(name, out Lingua.Language lang) ? lang : (Lingua.Language?)null)
            .Where(lang => lang.HasValue)
            .Select(lang => lang!.Value)
            .ToArray();

        return LanguageDetectorBuilder.FromLanguages(languages).Build();
    }
}
