#pragma warning disable OPENAI001

using System.IO;
using OpenAI;
using OpenAI.Assistants;
using OpenAI.Files;

namespace TransIt.Core;

// Manages a single summarize+chat session via the OpenAI Assistants API.
// Images are uploaded via Files API (purpose=vision), then referenced by file ID.
// Follow-up messages only send text — assistant retains thread context.
// Call DisposeAsync on window close to delete the thread, assistant, and uploaded files.
public sealed class AssistantsChatService : IAsyncDisposable
{
    private readonly AssistantClient _assistantClient;
    private readonly OpenAIFileClient _fileClient;
    private readonly string _model;

    private string? _assistantId;
    private string? _threadId;
    private readonly List<string> _uploadedFileIds = new();

    public AssistantsChatService(string apiKey, string model)
    {
        var client = new OpenAIClient(apiKey);
        _assistantClient = client.GetAssistantClient();
        _fileClient = client.GetOpenAIFileClient();
        _model = model;
    }

    // Creates assistant + thread, uploads images as vision files, runs initial summarize.
    public async Task<string> StartImageSessionAsync(
        IList<byte[]> images, string sourceLang, string contentType = "other", CancellationToken ct = default)
    {
        await CreateAssistantAndThreadAsync(
            TranslationService.GetSummarizeImagesSystemPrompt(images.Count, sourceLang, contentType));

        await CreateImageMessageRawAsync(_threadId!, images, ct);
        return await RunAndGetResponseAsync(ct);
    }

    // Creates assistant + thread, runs initial summarize on plain text.
    public async Task<string> StartTextSessionAsync(
        string text, string sourceLang, CancellationToken ct)
    {
        await CreateAssistantAndThreadAsync(
            TranslationService.GetSummarizeTextSystemPrompt(sourceLang));

        await _assistantClient.CreateMessageAsync(_threadId!, MessageRole.User,
            [MessageContent.FromText(text)], cancellationToken: ct);

        return await RunAndGetResponseAsync(ct);
    }

    // Adds a follow-up message to the existing thread and returns the AI response.
    public async Task<string> SendMessageAsync(string userMessage, CancellationToken ct)
    {
        if (_threadId is null || _assistantId is null)
            throw new InvalidOperationException("Session not started.");

        await _assistantClient.CreateMessageAsync(_threadId, MessageRole.User,
            [MessageContent.FromText(userMessage)], cancellationToken: ct);

        return await RunAndGetResponseAsync(ct);
    }

    private async Task CreateImageMessageRawAsync(string threadId, IList<byte[]> images, CancellationToken ct)
    {
        var contentParts = new List<MessageContent>
        {
            MessageContent.FromText($"Here are {images.Count} vertical slices of a stitched page (top to bottom):")
        };
        foreach (var jpeg in images)
        {
            using var stream = new MemoryStream(jpeg);
            var uploaded = await _fileClient.UploadFileAsync(stream, "image.jpg", FileUploadPurpose.Vision, ct);
            _uploadedFileIds.Add(uploaded.Value.Id);
            contentParts.Add(MessageContent.FromImageFileId(uploaded.Value.Id));
        }

        await _assistantClient.CreateMessageAsync(threadId, MessageRole.User, contentParts, cancellationToken: ct);
    }

    private async Task CreateAssistantAndThreadAsync(string systemPrompt)
    {
        var assistant = await _assistantClient.CreateAssistantAsync(_model,
            new AssistantCreationOptions { Instructions = systemPrompt });
        _assistantId = assistant.Value.Id;

        var thread = await _assistantClient.CreateThreadAsync();
        _threadId = thread.Value.Id;
    }

    private async Task<string> RunAndGetResponseAsync(CancellationToken ct)
    {
        var run = await _assistantClient.CreateRunAsync(_threadId!, _assistantId!);
        ThreadRun runState = run.Value;

        while (!runState.Status.IsTerminal)
        {
            await Task.Delay(800, ct);
            runState = (await _assistantClient.GetRunAsync(_threadId!, runState.Id)).Value;
        }

        if (runState.Status != RunStatus.Completed)
            throw new Exception($"Run failed ({runState.Status}): {runState.LastError?.Message}");

        await foreach (var msg in _assistantClient.GetMessagesAsync(_threadId!,
            new MessageCollectionOptions { Order = MessageCollectionOrder.Descending }))
        {
            if (msg.Role == MessageRole.Assistant)
            {
                return string.Concat(msg.Content
                    .Select(c => c.Text)
                    .Where(t => t is not null));
            }
        }

        throw new Exception("No assistant message found in thread.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_threadId is not null)
            try { await _assistantClient.DeleteThreadAsync(_threadId); } catch { }

        if (_assistantId is not null)
            try { await _assistantClient.DeleteAssistantAsync(_assistantId); } catch { }

        foreach (var fileId in _uploadedFileIds)
            try { await _fileClient.DeleteFileAsync(fileId); } catch { }
    }
}
