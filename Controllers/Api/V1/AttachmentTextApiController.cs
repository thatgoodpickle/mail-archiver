using MailArchiver.Attributes;
using MailArchiver.Services.Shared;
using Microsoft.AspNetCore.Mvc;

namespace MailArchiver.Controllers.Api.V1;

/// <summary>
/// Progress of the attachment text worker (text read out of attachments for search). Admin API key required.
/// </summary>
[Route("api/v1/attachment-text")]
[AdminOnly]
public class AttachmentTextApiController : ApiControllerBase
{
    private readonly IAttachmentTextProcessor _processor;

    public AttachmentTextApiController(IAttachmentTextProcessor processor) => _processor = processor;

    /// <summary>
    /// Counts of results by status (Done, NeedsOcr, Unsupported, Failed) and method, attachments still waiting
    /// to be read, and emails whose search text is not filled in yet. <c>mode</c> is the Ocr:Mode setting.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var s = await _processor.GetStatusAsync();
        return Ok(new
        {
            mode = s.Mode,
            texts = s.Texts.Select(t => new { status = t.Status, method = t.Method, count = t.Count }),
            pendingAttachments = s.PendingAttachments,
            emailsAwaitingIndex = s.EmailsAwaitingIndex,
            ocrEngine = s.OcrEngine,
            ocrUnavailable = s.OcrUnavailable
        });
    }

    /// <summary>
    /// Reads attachments again. <c>scope=failed</c> (default) drops failed results only; <c>scope=needsocr</c> drops the results
    /// that wait for an OCR engine (the worker does this itself at start when the engine is available); <c>scope=all</c> drops every
    /// result and every email's search text. The worker does the reading afterwards; nothing is read inside this call.
    /// </summary>
    [HttpPost("rescan")]
    public async Task<IActionResult> Rescan([FromQuery] string scope = "failed")
    {
        if (string.Equals(scope, "needsocr", StringComparison.OrdinalIgnoreCase))
            return Ok(new { scope = "needsocr", removed = await _processor.RescanNeedsOcrAsync() });

        if (!string.Equals(scope, "failed", StringComparison.OrdinalIgnoreCase) && !string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
            return Problem(statusCode: 400, title: "scope must be 'failed', 'needsocr' or 'all'.");

        var removed = await _processor.RescanAsync(string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase));
        return Ok(new { scope = scope.ToLowerInvariant(), removed });
    }
}
