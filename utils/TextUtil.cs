using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.utils
{
    public static class TextUtil
    {
        public static readonly char[] EosPunctuation = ".?!。？！".ToCharArray();

        private static readonly Regex Acronym = new(@"([A-Z])\s*\.\s*([A-Z])(?![A-Za-z]+)");
        private static readonly Regex AcronymWithWords = new(@"([A-Z])\s*\.\s*([A-Z])(?=[A-Za-z]+)");
        private static readonly Regex PunctuationSpace = new(@"\s*([.!?,])\s*");
        private static readonly Regex CjPunctuationSpace = new(@"\s*([。！？，、])\s*");
        private static readonly Regex Whitespace = new(@"\s+");
        private static readonly Regex ThinkBlock = new(@"<think>.*?</think>", RegexOptions.Singleline);
        private static readonly Regex SystemReminder = new(
            @"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline);
        private static readonly Regex SpecialToken = new(@"<\|[^|]*\|>");
        private static readonly Regex AngleTag = new(@"<[^<>]{0,200}>");

        public static bool IsEos(char c) => EosPunctuation.Contains(c);

        public static string Preprocess(string text)
        {
            text = Acronym.Replace(text, "$1$2");
            text = AcronymWithWords.Replace(text, "$1 $2");
            text = PunctuationSpace.Replace(text, "$1 ");
            text = CjPunctuationSpace.Replace(text, "$1");

            string[] parts = text.Split('\n');
            for (int i = 0; i + 1 < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
                if (parts[i].Length == 0)
                    continue;

                char last = parts[i][^1];
                if (!IsEos(last) && last != ',' && last != '，')
                    parts[i] += IsCjChar(last) ? "。" : ". ";
            }

            return string.Join("", parts).Trim();
        }

        // The key for matching utterances: "so the thing is", "So the thing is." and
        // "So  the thing is ," are all the same line — an appended period does not create a new one.
        public static string NormalizeForMatch(string text)
        {
            text = Whitespace.Replace(text, " ").Trim();

            int end = text.Length;
            while (end > 0)
            {
                char c = text[end - 1];
                if (!IsEos(c) && c != ',' && c != '，' && c != '、' && !char.IsWhiteSpace(c))
                    break;
                end--;
            }

            return text[..end].ToLowerInvariant();
        }

        public static string StripModelNoise(string text)
        {
            text = ThinkBlock.Replace(text, "");
            text = SystemReminder.Replace(text, "");
            text = SpecialToken.Replace(text, "");
            text = AngleTag.Replace(text, "");
            text = text.Replace("🔤", "").Trim();
            return text;
        }

        public static bool IsCjChar(char ch) =>
            (ch >= '一' && ch <= '鿿') ||
            (ch >= '㐀' && ch <= '䶿') ||
            (ch >= '　' && ch <= '〿') ||
            (ch >= '぀' && ch <= 'ゟ') ||
            (ch >= '゠' && ch <= 'ヿ');

        public static string NormalizeUrl(string url)
        {
            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            url = url.TrimEnd('/');
            if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                url += "/chat/completions";
            return url;
        }
    }
}
