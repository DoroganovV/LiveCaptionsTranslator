using LiveCaptionsTranslator.engine;
using LiveCaptionsTranslator.utils;
using Xunit;

namespace LiveCaptionsTranslator.Tests
{
    public class TranscriptParserTests
    {
        [Fact]
        public void SingleSentenceWithoutEos_IsActive()
        {
            var (finalized, active) = TranscriptParser.Split("Hello world");

            Assert.Empty(finalized);
            Assert.Equal("Hello world", active);
        }

        [Fact]
        public void FinalizedSentence_SeparatesFinalizedAndActive()
        {
            var (finalized, active) = TranscriptParser.Split("Hello world. And more");

            Assert.Equal(new[] { "Hello world." }, finalized);
            Assert.Equal("And more", active);
        }

        [Fact]
        public void MultipleSentences_SplitsAtEachEos()
        {
            var (finalized, active) = TranscriptParser.Split("One. Two! Three? Four");

            Assert.Equal(new[] { "One.", "Two!", "Three?" }, finalized);
            Assert.Equal("Four", active);
        }

        [Fact]
        public void TextEndingWithEos_LastSentenceStaysActive()
        {
            // The last sentence stays "active" (it may still be refined),
            // even if it ends with punctuation.
            var (finalized, active) = TranscriptParser.Split("First. Second.");

            Assert.Equal(new[] { "First." }, finalized);
            Assert.Equal("Second.", active);
        }

        [Fact]
        public void EmptyText_ReturnsNothing()
        {
            var (finalized, active) = TranscriptParser.Split("");

            Assert.Empty(finalized);
            Assert.Equal(string.Empty, active);
        }

        [Fact]
        public void WhitespaceBetweenSentences_IsTrimmed()
        {
            var (finalized, active) = TranscriptParser.Split("One.   Two.  ");

            Assert.Equal(new[] { "One.", "Two." }, finalized);
        }
    }

    public class TextUtilTests
    {
        [Fact]
        public void Preprocess_FixesNewlines()
        {
            string result = TextUtil.Preprocess("line one\nline two");

            Assert.Equal("line one. line two", result);
        }

        [Fact]
        public void Preprocess_KeepsEosLines()
        {
            string result = TextUtil.Preprocess("done.\nnext line");

            Assert.Equal("done. next line", result);
        }

        [Fact]
        public void StripModelNoise_RemovesTokens()
        {
            string result = TextUtil.StripModelNoise("response 🔤");

            Assert.Equal("response", result);
        }

        [Fact]
        public void StripModelNoise_RemovesThinkBlock()
        {
            string result = TextUtil.StripModelNoise("<think>reasoning</think>Answer");

            Assert.Equal("Answer", result);
        }

        [Fact]
        public void NormalizeUrl_AddsChatCompletionsPath()
        {
            string result = TextUtil.NormalizeUrl("https://api.example.com/v1");

            Assert.Equal("https://api.example.com/v1/chat/completions", result);
        }

        [Fact]
        public void NormalizeUrl_AddsHttpsScheme()
        {
            string result = TextUtil.NormalizeUrl("api.example.com");

            Assert.Equal("https://api.example.com/chat/completions", result);
        }

        [Fact]
        public void NormalizeForMatch_IgnoresTrailingPunctuationAndCase()
        {
            Assert.Equal(
                TextUtil.NormalizeForMatch("So the thing is"),
                TextUtil.NormalizeForMatch("So the thing is."));
            Assert.Equal(
                TextUtil.NormalizeForMatch("So the thing is,"),
                TextUtil.NormalizeForMatch("so  the thing is ?"));
        }

        [Fact]
        public void NormalizeForMatch_KeepsDifferentSentencesApart()
        {
            Assert.NotEqual(
                TextUtil.NormalizeForMatch("So the thing is"),
                TextUtil.NormalizeForMatch("And then we left"));
        }
    }
}
