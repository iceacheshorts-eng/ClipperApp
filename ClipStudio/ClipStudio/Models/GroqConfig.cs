namespace ClipStudio.Models
{
    public static class GroqConfig
    {
        public const string EnvVarName = "GROQ_API_KEY";
        public const string BaseUrl = "https://api.groq.com/openai/v1/chat/completions";
        // Recommended model based on https://console.groq.com/docs/deprecations
        public const string ModelId = "openai/gpt-oss-20b";
    }
}
