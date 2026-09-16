using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.engine
{
    public static class TranscriptParser
    {
        public static (string[] Finalized, string Active) Split(string fullText)
        {
            if (string.IsNullOrWhiteSpace(fullText))
                return (Array.Empty<string>(), string.Empty);

            int lastEos = TextUtil.IsEos(fullText[^1])
                ? fullText.AsSpan(0, fullText.Length - 1).LastIndexOfAny(TextUtil.EosPunctuation)
                : fullText.LastIndexOfAny(TextUtil.EosPunctuation);

            if (lastEos < 0)
                return (Array.Empty<string>(), fullText);

            string finalizedPart = fullText[..(lastEos + 1)];
            string active = fullText[(lastEos + 1)..].TrimStart();

            var finalized = new List<string>();
            int start = 0;
            for (int i = 0; i < finalizedPart.Length; i++)
            {
                if (TextUtil.IsEos(finalizedPart[i]))
                {
                    string segment = finalizedPart[start..(i + 1)].Trim();
                    if (segment.Length > 0)
                        finalized.Add(segment);
                    start = i + 1;
                }
            }

            return (finalized.ToArray(), active);
        }
    }
}
