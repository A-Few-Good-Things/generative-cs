using ChatAIze.GenerativeCS.Constants;

namespace ChatAIze.GenerativeCS.Options.OpenAI;

public record EmbeddingOptions
{
    public EmbeddingOptions(string model = DefaultModels.OpenAI.Embedding, string? user = null)
    {
        Model = model;
        User = user;
    }

    public string Model { get; set; } = DefaultModels.OpenAI.Embedding;

    public int? Dimensions { get; set; }

    public string? User { get; set; }

    public int MaxAttempts { get; set; } = 5;
}
