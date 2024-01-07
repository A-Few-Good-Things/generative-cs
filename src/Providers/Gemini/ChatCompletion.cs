using System.Text.Json;
using System.Text.Json.Nodes;
using ChatAIze.GenerativeCS.Enums;
using ChatAIze.GenerativeCS.Interfaces;
using ChatAIze.GenerativeCS.Models;
using ChatAIze.GenerativeCS.Options.Gemini;
using ChatAIze.GenerativeCS.Utilities;

namespace ChatAIze.GenerativeCS.Providers.Gemini;

internal static class ChatCompletion
{
    internal static async Task<string> CompleteAsync(string prompt, string apiKey, ChatCompletionOptions? options = null, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        httpClient ??= new();
        options ??= new();

        if (options.Functions.Count >= 1)
        {
            var conversation = new ChatConversation();
            conversation.FromUser(prompt);

            return await CompleteAsync(conversation, apiKey, options, httpClient, cancellationToken);
        }

        var request = CreateCompletionRequest(prompt);

        using var response = await httpClient.RepeatPostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/{options.Model}:generateContent?key={apiKey}", request, null, options.MaxAttempts, cancellationToken);
        using var responseContent = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var responseDocument = await JsonDocument.ParseAsync(responseContent, cancellationToken: cancellationToken);

        var generatedMessage = responseDocument.RootElement.GetProperty("candidates")[0];
        var messageContent = generatedMessage.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()!;

        return messageContent;
    }

    internal static async Task<string> CompleteAsync<T>(IChatConversation<T> conversation, string apiKey, ChatCompletionOptions? options = null, HttpClient? httpClient = null, CancellationToken cancellationToken = default) where T : IChatMessage, new()
    {
        httpClient ??= new();
        options ??= new();

        var request = CreateChatCompletionRequest(conversation, options);

        using var response = await httpClient.RepeatPostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/{options.Model}:generateContent?key={apiKey}", request, null, options.MaxAttempts, cancellationToken);
        using var responseContent = await response.Content.ReadAsStreamAsync(cancellationToken);

        var responseDocument = await JsonDocument.ParseAsync(responseContent, cancellationToken: cancellationToken);
        var responseParts = responseDocument.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
        var allFunctions = options.Functions.Concat(conversation.Functions).GroupBy(f => f.Name).Select(g => g.Last()).ToList();

        string messageContent = null!;
        foreach (var part in responseParts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var functionCallElement) && functionCallElement.TryGetProperty("name", out var functionNameElement))
            {
                var functionName = functionNameElement.GetString()!;
                var functionArguments = functionCallElement.GetProperty("args").GetString()!;

                var message1 = await conversation.FromAssistantAsync(new FunctionCall(functionName, functionArguments));
                await options.AddMessageCallback(message1);

                var function = allFunctions.LastOrDefault(f => f.Name.Equals(functionName, StringComparison.InvariantCultureIgnoreCase));
                if (function != null)
                {
                    if (function.RequiresConfirmation && conversation.Messages.Count(m => m.FunctionCalls.Any(c => c.Name == functionName)) % 2 != 0)
                    {
                        var message2 = await conversation.FromFunctionAsync(new FunctionResult(functionName, "Before executing, are you sure the user wants to run this function? If yes, call it again to confirm."));
                        await options.AddMessageCallback(message2);
                    }
                    else
                    {
                        if (function.Callback != null)
                        {
                            var functionResult = await FunctionInvoker.InvokeAsync(function.Callback, functionArguments, cancellationToken);
                            var message3 = await conversation.FromFunctionAsync(new FunctionResult(functionName, functionResult));

                            await options.AddMessageCallback(message3);
                        }
                        else
                        {
                            var functionResult = await options.DefaultFunctionCallback(functionName, functionArguments, cancellationToken);
                            var message4 = await conversation.FromFunctionAsync(new FunctionResult(functionName, JsonSerializer.Serialize(functionResult)));

                            await options.AddMessageCallback(message4);
                        }
                    }
                }
                else
                {
                    var message5 = await conversation.FromFunctionAsync(new FunctionResult(functionName, $"Function '{functionName}' was not found."));
                    await options.AddMessageCallback(message5);
                }

                return await CompleteAsync(conversation, apiKey, options, httpClient, cancellationToken);
            }
            else if (part.TryGetProperty("text", out var textElement))
            {
                messageContent = textElement.GetString()!;

                var message6 = await conversation.FromAssistantAsync(messageContent);
                await options.AddMessageCallback(message6);
            }
            else
            {
                var message7 = await conversation.FromFunctionAsync(new FunctionResult("Error", "Either call a function or respond with text."));
                await options.AddMessageCallback(message7);

                return await CompleteAsync(conversation, apiKey, options, httpClient, cancellationToken);
            }
        }

        return messageContent;
    }

    private static JsonObject CreateCompletionRequest(string prompt)
    {
        var partObject = new JsonObject
        {
            { "text", prompt }
        };

        var partsArray = new JsonArray
        {
            partObject
        };

        var contentObject = new JsonObject
        {
            { "parts", partsArray}
        };

        var contentsArray = new JsonArray
        {
            contentObject
        };

        var requestObject = new JsonObject
        {
            { "contents", contentsArray }
        };

        return requestObject;
    }

    private static JsonObject CreateChatCompletionRequest<T>(IChatConversation<T> conversation, ChatCompletionOptions options) where T : IChatMessage, new()
    {
        var messages = conversation.Messages.ToList();
        if (options.IsTimeAware)
        {
            MessageTools.AddTimeInformation(messages, options.TimeCallback());
        }

        MessageTools.LimitTokens(messages, options.MessageLimit, options.CharacterLimit);
        MessageTools.ReplaceSystemRole(messages);
        MessageTools.MergeMessages(messages);

        var contentsArray = new JsonArray();
        foreach (var message in messages)
        {
            var partObject = new JsonObject();
            var functionCall = message.FunctionCalls.FirstOrDefault();

            if (functionCall != null)
            {
                var functionCallObject = new JsonObject
                {
                    { "name", functionCall.Name },
                    { "args", functionCall.Arguments }
                };

                partObject.Add("functionCall", functionCallObject);
            }
            else if (message.FunctionResult != null)
            {
                var responseObject = new JsonObject
                {
                    { "name", message.FunctionResult.Name },
                    { "content", JsonSerializer.SerializeToNode(message.FunctionResult.Result) }
                };

                var functionResponseObject = new JsonObject
                {
                    { "name", message.FunctionResult.Name },
                    { "response", responseObject }
                };

                partObject.Add("functionResponse", functionResponseObject);
            }
            else
            {
                partObject.Add("text", message.Content);
            }

            var partsArray = new JsonArray
            {
                partObject
            };

            var contentObject = new JsonObject
            {
                { "role", GetRoleName(message.Role) },
                { "parts", partsArray }
            };

            contentsArray.Add(contentObject);
        }

        var requestObject = new JsonObject
        {
            { "contents", contentsArray },
        };

        var allFunctions = options.Functions.Concat(conversation.Functions).GroupBy(f => f.Name).Select(g => g.Last()).ToList();
        if (allFunctions.Count > 0)
        {
            var functionsArray = new JsonArray();
            foreach (var function in allFunctions)
            {
                functionsArray.Add(FunctionSerializer.SerializeFunction(function));
            }

            var functionsObject = new JsonObject
            {
                { "function_declarations", functionsArray }
            };

            var toolsArray = new JsonArray
            {
                functionsObject
            };

            requestObject.Add("tools", toolsArray);
        }

        return requestObject;
    }

    private static string GetRoleName(ChatRole role)
    {
        return role switch
        {
            ChatRole.System => "user",
            ChatRole.User => "user",
            ChatRole.Assistant => "model",
            ChatRole.Function => "tool",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Invalid role")
        };
    }
}
